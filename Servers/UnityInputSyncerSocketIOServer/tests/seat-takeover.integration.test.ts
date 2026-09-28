import 'reflect-metadata';
import { Test } from '@nestjs/testing';
import { INestApplication } from '@nestjs/common';
import { IoAdapter } from '@nestjs/platform-socket.io';
import { io, Socket } from 'socket.io-client';
import { AppModule } from '../src/app.module';

/**
 * A relaunch that beats the transport (football-card E46/S01).
 *
 * A device that loses its network sends nothing, so the server only learns its socket is dead from
 * the ping timeout. A relaunch is faster than that, and arrives on a second socket for a user who
 * is still seated and still counted as connected. It must take the seat over rather than be told
 * `match-full` — but only where the user id is proven by a bound token.
 */

const ADMIN_AUTH = `Bearer ${process.env.INPUT_SYNCER_ADMIN_AUTH_TOKEN ?? ''}`;

let app: INestApplication;
let baseUrl: string;

beforeAll(async () => {
  process.env.INPUT_SYNCER_PORT = '0';
  process.env.INPUT_SYNCER_ALLOW_LATE_JOIN = 'true';

  const module = await Test.createTestingModule({ imports: [AppModule] }).compile();
  app = module.createNestApplication();
  app.useWebSocketAdapter(new IoAdapter(app));
  await app.listen(0);
  baseUrl = (await app.getUrl()).replace('[::1]', 'localhost');
});

afterAll(async () => {
  await app.close().catch(() => {});
});

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

async function createInstance(opts: Record<string, unknown>): Promise<{ id: string }> {
  const res = await fetch(`${baseUrl}/api/instances`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Authorization: ADMIN_AUTH },
    body: JSON.stringify({
      maxPlayers: 2,
      autoStartWhenFull: true,
      allowLateJoin: true,
      disconnectAbandonTimeoutSeconds: 5,
      users: { 'user-a': 'home', 'user-b': 'away' },
      ...opts,
    }),
  });
  if (!res.ok) throw new Error(`createInstance failed: ${res.status} ${await res.text()}`);
  return res.json() as Promise<{ id: string }>;
}

const BOUND = {
  matchAccess: 'token',
  allowedMatchTokens: { 'user-a': 'token-a', 'user-b': 'token-b' },
};

function connect(matchId: string, userId: string, matchToken?: string): Socket {
  const query: Record<string, string> = { matchId, userId };
  if (matchToken) query.matchToken = matchToken;
  return io(baseUrl, { path: '/match-gateway', transports: ['websocket'], query, forceNew: true });
}

function connected(socket: Socket): Promise<void> {
  return new Promise((resolve, reject) => {
    socket.on('connect', () => resolve());
    socket.on('connect_error', (err) => reject(err));
    setTimeout(() => reject(new Error('connect timeout')), 5000);
  });
}

function contentErrors(socket: Socket): string[] {
  const reasons: string[] = [];
  socket.on('content-error', (e: { reason: string }) => reasons.push(e.reason));
  return reasons;
}

function stepInputs(socket: Socket): Record<string, unknown>[] {
  const inputs: Record<string, unknown>[] = [];
  socket.on('on-steps', (steps: { inputs?: Record<string, unknown>[] }[]) => {
    for (const step of steps) for (const input of step.inputs ?? []) inputs.push(input);
  });
  return inputs;
}

describe('seat takeover — a relaunch that beats the ping timeout', () => {
  let sockets: Socket[] = [];

  afterEach(() => {
    for (const s of sockets) if (s.connected) s.disconnect();
    sockets = [];
  });

  it('a second socket for a seated, connected user takes the seat and gets the history', async () => {
    const instance = await createInstance(BOUND);
    const a = connect(instance.id, 'user-a', 'token-a');
    const b = connect(instance.id, 'user-b', 'token-b');
    sockets.push(a, b);
    await Promise.all([connected(a), connected(b)]);
    await delay(400);

    const oldErrors = contentErrors(a);
    const inputsB = stepInputs(b);

    // The relaunch: user-a again, while the first socket is still open.
    const a2 = connect(instance.id, 'user-a', 'token-a');
    sockets.push(a2);
    const history = new Promise<{ steps: unknown[] }>((resolve) =>
      a2.on('on-all-steps', (all: { steps: unknown[] }) => resolve(all)),
    );
    const newErrors = contentErrors(a2);
    await connected(a2);

    const all = await history;
    expect(all.steps.length).toBeGreaterThan(0);
    await delay(400);

    expect(newErrors).not.toContain('match-full');
    expect(oldErrors).toContain('superseded');
    expect(a.connected).toBe(false);

    // The seat is the new socket's: its inputs are stamped as user-a and reach the other player.
    a2.emit('input', { inputData: { type: 'probe', value: 7 } });
    await delay(400);
    const probe = inputsB.find((i) => i.type === 'probe');
    expect(probe).toBeDefined();
    expect(probe!.userId).toBe('user-a');

    // Closing the superseded socket marked nobody disconnected.
    expect(inputsB.find((i) => i.type === 'disconnect')).toBeUndefined();
  });

  it('before the match starts, the new socket gets the match context', async () => {
    const instance = await createInstance(BOUND);
    const a = connect(instance.id, 'user-a', 'token-a');
    sockets.push(a);
    await connected(a);
    await delay(200);

    const a2 = connect(instance.id, 'user-a', 'token-a');
    sockets.push(a2);
    const context = new Promise<unknown>((resolve) => a2.on('on-match-context', resolve));
    await connected(a2);
    await expect(context).resolves.toBeDefined();

    // user-a is still one seat: user-b arriving starts the match rather than being refused.
    const b = connect(instance.id, 'user-b', 'token-b');
    sockets.push(b);
    const started = new Promise<void>((resolve) => b.on('on-start', () => resolve()));
    const bErrors = contentErrors(b);
    await connected(b);
    await started;
    expect(bErrors).not.toContain('match-full');
  });

  it('without a bound token nothing is taken over — the second socket is refused as before', async () => {
    const instance = await createInstance({});
    const a = connect(instance.id, 'user-a');
    const b = connect(instance.id, 'user-b');
    sockets.push(a, b);
    await Promise.all([connected(a), connected(b)]);
    await delay(400);

    const a2 = connect(instance.id, 'user-a');
    sockets.push(a2);
    const errors = contentErrors(a2);
    await connected(a2);
    await delay(300);

    expect(errors).toContain('match-full');
    expect(a.connected).toBe(true);
  });
});

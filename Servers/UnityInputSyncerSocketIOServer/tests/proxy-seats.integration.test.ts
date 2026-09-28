import 'reflect-metadata';
import { Test } from '@nestjs/testing';
import { INestApplication } from '@nestjs/common';
import { IoAdapter } from '@nestjs/platform-socket.io';
import { io, Socket } from 'socket.io-client';
import { AppModule } from '../src/app.module';

/**
 * Proxy seats (football-card E46/S02): a seat no socket holds, spoken for by one seated user.
 *
 * The shape of a match against an opponent the player's own client plays — a bot. The bot's
 * picks must reach the step stream stamped as the bot, so the stream reads exactly as a match
 * between two people; and the relay, not the client, must stay the authority on who sent what.
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

async function create(body: Record<string, unknown>): Promise<Response> {
  return fetch(`${baseUrl}/api/instances`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Authorization: ADMIN_AUTH },
    body: JSON.stringify(body),
  });
}

/** One human seat, one bot seat the human speaks for — a bot match as Nakama provisions it. */
const BOT_MATCH = {
  maxPlayers: 1,
  autoStartWhenFull: true,
  allowLateJoin: true,
  disconnectAbandonTimeoutSeconds: 2,
  abandonMatchTimeoutSeconds: 1,
  matchAccess: 'token',
  allowedMatchTokens: { player: 'token-player' },
  users: { player: 'home', donor: 'away' },
  proxySeats: { donor: 'player' },
};

async function createBotMatch(opts: Record<string, unknown> = {}): Promise<{ id: string }> {
  const res = await create({ ...BOT_MATCH, ...opts });
  if (!res.ok) throw new Error(`create failed: ${res.status} ${await res.text()}`);
  return res.json() as Promise<{ id: string }>;
}

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

function stepInputs(socket: Socket): Record<string, unknown>[] {
  const inputs: Record<string, unknown>[] = [];
  socket.on('on-steps', (steps: { inputs?: Record<string, unknown>[] }[]) => {
    for (const step of steps) for (const input of step.inputs ?? []) inputs.push(input);
  });
  return inputs;
}

describe('proxy seats — a seat one user speaks for', () => {
  let sockets: Socket[] = [];

  afterEach(() => {
    for (const s of sockets) if (s.connected) s.disconnect();
    sockets = [];
  });

  it('a one-seat match starts when its one player joins, and the proxy seat does not count', async () => {
    const instance = await createBotMatch();
    const player = connect(instance.id, 'player', 'token-player');
    sockets.push(player);
    const started = new Promise<void>((resolve) => player.on('on-start', () => resolve()));
    await connected(player);
    await started;
  });

  it("the speaker's asUserId input is stamped as the seat, and asUserId never reaches the stream", async () => {
    const instance = await createBotMatch();
    const player = connect(instance.id, 'player', 'token-player');
    sockets.push(player);
    const inputs = stepInputs(player);
    await connected(player);
    await delay(300);

    player.emit('input', { inputData: { type: 'duel-pick', turnStep: 3, action: 7, asUserId: 'donor' } });
    player.emit('input', { inputData: { type: 'duel-pick', turnStep: 3, action: 2 } });
    await delay(400);

    const botPick = inputs.find((i) => i.type === 'duel-pick' && i.action === 7);
    const ownPick = inputs.find((i) => i.type === 'duel-pick' && i.action === 2);
    expect(botPick).toBeDefined();
    expect(botPick!.userId).toBe('donor');
    expect('asUserId' in botPick!).toBe(false);
    expect(ownPick!.userId).toBe('player');
  });

  it('an undeclared asUserId is dropped, not re-stamped as the sender', async () => {
    const instance = await createBotMatch();
    const player = connect(instance.id, 'player', 'token-player');
    sockets.push(player);
    const inputs = stepInputs(player);
    await connected(player);
    await delay(300);

    player.emit('input', { inputData: { type: 'duel-pick', action: 9, asUserId: 'somebody-else' } });
    player.emit('input', { inputData: { type: 'duel-pick', action: 8, asUserId: 42 } });
    await delay(400);

    expect(inputs.find((i) => i.action === 9)).toBeUndefined();
    expect(inputs.find((i) => i.action === 8)).toBeUndefined();
  });

  it('in a two-person match nobody can speak for the opponent', async () => {
    const res = await create({
      maxPlayers: 2,
      autoStartWhenFull: true,
      allowLateJoin: true,
      matchAccess: 'token',
      allowedMatchTokens: { a: 'token-a', b: 'token-b' },
      users: { a: 'home', b: 'away' },
    });
    const instance = (await res.json()) as { id: string };
    const a = connect(instance.id, 'a', 'token-a');
    const b = connect(instance.id, 'b', 'token-b');
    sockets.push(a, b);
    const inputsB = stepInputs(b);
    await Promise.all([connected(a), connected(b)]);
    await delay(400);

    a.emit('input', { inputData: { type: 'duel-pick', action: 5, asUserId: 'b' } });
    await delay(400);

    expect(inputsB.find((i) => i.action === 5)).toBeUndefined();
  });

  it('no socket may hold a proxy seat', async () => {
    const instance = await createBotMatch({ matchAccess: 'open', allowedMatchTokens: undefined });
    const impostor = connect(instance.id, 'donor');
    sockets.push(impostor);
    const errors: string[] = [];
    impostor.on('content-error', (e: { reason: string }) => errors.push(e.reason));
    await connected(impostor).catch(() => {});
    await delay(300);

    expect(errors).toContain('match-access-denied');
    expect(impostor.connected).toBe(false);
  });

  it('the speaker leaving past its window ends the match: the seat has no life of its own', async () => {
    const instance = await createBotMatch();
    const player = connect(instance.id, 'player', 'token-player');
    sockets.push(player);
    await connected(player);
    await delay(300);

    player.disconnect();
    await delay(2600);

    const res = await fetch(`${baseUrl}/api/instances/${instance.id}`, {
      headers: { Authorization: ADMIN_AUTH },
    });
    // Gone (recycled) or finished — either way no longer a match in play.
    if (res.ok) {
      const info = (await res.json()) as { matchFinished: boolean };
      expect(info.matchFinished).toBe(true);
    } else {
      expect(res.status).toBe(404);
    }
  });

  describe('the admin API refuses a seat a socket could also hold', () => {
    it('refuses a proxy seat that has a bound token', async () => {
      const res = await create({
        ...BOT_MATCH,
        allowedMatchTokens: { player: 'token-player', donor: 'token-donor' },
      });
      expect(res.status).toBe(400);
      expect(await res.text()).toContain('donor has a match token');
    });

    it('refuses a seat that speaks for itself', async () => {
      const res = await create({ ...BOT_MATCH, proxySeats: { player: 'player' } });
      expect(res.status).toBe(400);
    });

    it('refuses a shape that is not a map of ids', async () => {
      const res = await create({ ...BOT_MATCH, proxySeats: ['donor'] });
      expect(res.status).toBe(400);
    });
  });
});

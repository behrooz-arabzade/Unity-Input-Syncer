import 'reflect-metadata';
import { Test } from '@nestjs/testing';
import { INestApplication } from '@nestjs/common';
import { IoAdapter } from '@nestjs/platform-socket.io';
import { io, Socket } from 'socket.io-client';
import { AppModule } from '../src/app.module';

/**
 * `mergeServerOptions` accepts 21 per-instance keys; `overridesFromCreateBody` used to forward
 * 13, and the other eight were accepted with a 201 and dropped in silence. The silence was the
 * defect: a caller setting `abandonMatchTimeoutSeconds` got a success and a match that never
 * timed out, and the symptom arrives weeks later as instances that never die.
 *
 * So these tests do two things: prove each newly forwarded option actually reaches the
 * instance — by its behaviour, since none of them is echoed back — and prove an unrecognised
 * key is now a named 400 rather than a shrug.
 */

let app: INestApplication;
let baseUrl: string;

beforeAll(async () => {
  process.env.INPUT_SYNCER_PORT = '0';

  const module = await Test.createTestingModule({
    imports: [AppModule],
  }).compile();

  app = module.createNestApplication();
  app.useWebSocketAdapter(new IoAdapter(app));
  await app.listen(0);
  baseUrl = (await app.getUrl()).replace('[::1]', 'localhost');
});

afterAll(async () => {
  await app.close().catch(() => {});
});

const delay = (ms: number) => new Promise((r) => setTimeout(r, ms));

async function post(body: Record<string, unknown>): Promise<Response> {
  return fetch(`${baseUrl}/api/instances`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

async function createInstance(
  opts: Record<string, unknown>,
): Promise<{ id: string }> {
  const res = await post({
    maxPlayers: 2,
    autoStartWhenFull: true,
    stepIntervalSeconds: 0.05,
    ...opts,
  });
  if (!res.ok) {
    throw new Error(`createInstance failed: ${res.status} ${await res.text()}`);
  }
  return res.json() as Promise<{ id: string }>;
}

function connect(matchId: string, userId: string): Socket {
  return io(baseUrl, {
    path: '/match-gateway',
    transports: ['websocket'],
    query: { matchId, userId },
    forceNew: true,
    reconnection: false,
  });
}

function joined(socket: Socket): Promise<void> {
  return new Promise((resolve, reject) => {
    socket.on('on-match-context', () => resolve());
    setTimeout(() => reject(new Error('join timeout')), 5000);
  });
}

/** Every input the server broadcast in a step, in arrival order. */
function collectInputs(socket: Socket): Record<string, unknown>[] {
  const seen: Record<string, unknown>[] = [];
  socket.on('on-steps', (steps: { inputs?: Record<string, unknown>[] }[]) => {
    for (const step of steps) for (const i of step.inputs ?? []) seen.push(i);
  });
  return seen;
}

describe('per-instance options — the six that used to be dropped', () => {
  let sockets: Socket[] = [];
  afterEach(() => {
    for (const s of sockets) s.close();
    sockets = [];
  });

  it('rejectInputAfterSessionFinish stops a finished sender’s inputs', async () => {
    const instance = await createInstance({
      matchData: { v: 1 },
      rejectInputAfterSessionFinish: true,
    });
    const a = connect(instance.id, 'u1');
    const b = connect(instance.id, 'u2');
    sockets.push(a, b);
    await Promise.all([joined(a), joined(b)]);

    const seen = collectInputs(b);
    a.emit('input', { inputData: { tag: 'before' } });
    await delay(200);
    a.emit('player-session-finish', { data: { score: 1 } });
    await delay(100);
    a.emit('input', { inputData: { tag: 'after' } });
    await delay(400);

    expect(seen.some((i) => i.tag === 'before')).toBe(true);
    // The window this closes: one client has declared its result while the other has not
    // yet voted, and the architecture already assumes a client may be modified.
    expect(seen.some((i) => i.tag === 'after')).toBe(false);
  });

  it('the same input is delivered when the option is left off', async () => {
    const instance = await createInstance({ matchData: { v: 1 } });
    const a = connect(instance.id, 'u1');
    const b = connect(instance.id, 'u2');
    sockets.push(a, b);
    await Promise.all([joined(a), joined(b)]);

    const seen = collectInputs(b);
    a.emit('player-session-finish', { data: { score: 1 } });
    await delay(100);
    a.emit('input', { inputData: { tag: 'after' } });
    await delay(400);

    // Proves the test above measured the option rather than some unrelated ordering.
    expect(seen.some((i) => i.tag === 'after')).toBe(true);
  });

  it('sessionFinishBroadcast false keeps a result from the opponent', async () => {
    const instance = await createInstance({
      matchData: { v: 1 },
      sessionFinishBroadcast: false,
    });
    const a = connect(instance.id, 'u1');
    const b = connect(instance.id, 'u2');
    sockets.push(a, b);
    await Promise.all([joined(a), joined(b)]);

    let heard = false;
    b.on('on-player-session-finish', () => {
      heard = true;
    });
    a.emit('player-session-finish', { data: { score: 1 } });
    await delay(400);
    expect(heard).toBe(false);
  });

  it('sessionFinishMaxPayloadBytes refuses a payload over the cap', async () => {
    const instance = await createInstance({
      matchData: { v: 1 },
      sessionFinishMaxPayloadBytes: 32,
      sessionFinishBroadcast: true,
    });
    const a = connect(instance.id, 'u1');
    const b = connect(instance.id, 'u2');
    sockets.push(a, b);
    await Promise.all([joined(a), joined(b)]);

    let heard = false;
    b.on('on-player-session-finish', () => {
      heard = true;
    });
    a.emit('player-session-finish', { data: { blob: 'x'.repeat(200) } });
    await delay(400);
    expect(heard).toBe(false);

    a.emit('player-session-finish', { data: { s: 1 } });
    await delay(400);
    expect(heard).toBe(true);
  });

  it('quorumUserFinishEndsMatch false leaves a voted match running', async () => {
    const instance = await createInstance({
      matchData: { v: 1 },
      quorumUserFinishEndsMatch: false,
    });
    const a = connect(instance.id, 'u1');
    const b = connect(instance.id, 'u2');
    sockets.push(a, b);
    await Promise.all([joined(a), joined(b)]);

    let finished = false;
    a.on('on-finish', () => {
      finished = true;
    });
    a.emit('user-finish');
    b.emit('user-finish');
    await delay(500);
    expect(finished).toBe(false);
  });

  it('abandonMatchTimeoutSeconds reaches the instance and ends the match', async () => {
    // `disconnectAbandonTimeoutSeconds: 0` is the one configuration in which the abandon
    // deadline can arm at all — see the test below, which is why.
    const instance = await createInstance({
      matchData: { v: 1 },
      allowLateJoin: true,
      abandonMatchTimeoutSeconds: 1,
      disconnectAbandonTimeoutSeconds: 0,
    });
    const a = connect(instance.id, 'u1');
    const b = connect(instance.id, 'u2');
    sockets.push(a, b);
    await Promise.all([joined(a), joined(b)]);

    const reason = new Promise<string>((resolve) => {
      a.on('on-finish', (d: { reason?: string }) => resolve(d?.reason ?? ''));
      setTimeout(() => resolve('no-finish'), 5000);
    });
    b.close();
    // The option E08/S02 wrote into a provisioning contract and that used to be dropped on
    // the floor: without it forwarded, this match never ends.
    expect(await reason).toBe('abandon_timeout');
  });

  it('and does nothing when it is not set, which is what makes the above a measurement', async () => {
    const instance = await createInstance({
      matchData: { v: 1 },
      allowLateJoin: true,
      disconnectAbandonTimeoutSeconds: 0,
    });
    const a = connect(instance.id, 'u1');
    const b = connect(instance.id, 'u2');
    sockets.push(a, b);
    await Promise.all([joined(a), joined(b)]);

    const reason = new Promise<string>((resolve) => {
      a.on('on-finish', (d: { reason?: string }) => resolve(d?.reason ?? ''));
      setTimeout(() => resolve('no-finish'), 2500);
    });
    b.close();
    expect(await reason).toBe('no-finish');
  });

  it('CURRENT BEHAVIOUR: a non-zero disconnect window never arms the deadline', async () => {
    // Not the behaviour anyone wants, and it is not this story's to change. A disconnected
    // player is marked `abandoned` but never *removed*, and `getJoinedPlayerCount` counts
    // `joined` regardless — so `updateAbandonDeadline` is never reached and the seat still
    // reads as filled. Every realistic configuration has a non-zero window (ours is 90 s),
    // so `abandon_timeout` cannot fire in production and such a match runs to
    // `max_instance_lifetime` instead. Filed as E08/S09; this expectation flips when it lands.
    const instance = await createInstance({
      matchData: { v: 1 },
      allowLateJoin: true,
      abandonMatchTimeoutSeconds: 1,
      disconnectAbandonTimeoutSeconds: 0.2,
    });
    const a = connect(instance.id, 'u1');
    const b = connect(instance.id, 'u2');
    sockets.push(a, b);
    await Promise.all([joined(a), joined(b)]);

    const reason = new Promise<string>((resolve) => {
      a.on('on-finish', (d: { reason?: string }) => resolve(d?.reason ?? ''));
      setTimeout(() => resolve('no-finish'), 3000);
    });
    b.close();
    expect(await reason).toBe('no-finish');
  });
});

describe('per-instance options — an unrecognised key is refused', () => {
  it('names the unknown field in a 400', async () => {
    const res = await post({ maxPlayers: 2, definitelyNotAnOption: true });
    expect(res.status).toBe(400);
    expect(await res.text()).toContain('definitelyNotAnOption');
  });

  it('catches a near-miss spelling rather than applying nothing', async () => {
    const res = await post({ maxPlayers: 2, abandonMatchTimeout: 120 });
    expect(res.status).toBe(400);
    expect(await res.text()).toContain('abandonMatchTimeout');
  });

  it('still accepts the allocator context Nakama sends with every request', async () => {
    // The Hiro match manager posts these alongside the options; refusing them would break
    // allocation outright, so they are accepted and deliberately unused.
    const res = await post({
      maxPlayers: 2,
      participant_user_ids: ['u1', 'u2'],
      matchmaker: { ticket: 'whatever' },
      nakamaMatchId: 'nakama-1',
    });
    expect(res.status).toBe(201);
  });

  it('rejects an out-of-range rewardOutcomeDelivery', async () => {
    const res = await post({ maxPlayers: 2, rewardOutcomeDelivery: 9 });
    expect(res.status).toBe(400);
    expect(await res.text()).toContain('rewardOutcomeDelivery');
  });
});

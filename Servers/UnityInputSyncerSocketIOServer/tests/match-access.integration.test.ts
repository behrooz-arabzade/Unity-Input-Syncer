import 'reflect-metadata';
import { Test } from '@nestjs/testing';
import { INestApplication } from '@nestjs/common';
import { IoAdapter } from '@nestjs/platform-socket.io';
import { io, Socket } from 'socket.io-client';
import { AppModule } from '../src/app.module';

/** Set by tests/setup/adminAuth.js — the guard fails closed without it (E08/S10). */
const ADMIN_AUTH = `Bearer ${process.env.INPUT_SYNCER_ADMIN_AUTH_TOKEN ?? ''}`;


/**
 * A match token used to be a *match* secret rather than a *player* secret: the token list was
 * a flat Set, the joining userId came from the client's own handshake query, and nothing
 * compared the two. One of two invited players could therefore seat both sides — which, for a
 * game that accepts a result only when both clients submit the same one, is a lone cheater
 * producing two identical submissions by construction.
 *
 * These tests pin the bound form (`{ userId: token }`) shut, and pin the unbound form's
 * documented looseness so nobody "fixes" it by accident and breaks an existing deployment.
 */

let app: INestApplication;
let baseUrl: string;

beforeAll(async () => {
  process.env.INPUT_SYNCER_PORT = '0';
  process.env.INPUT_SYNCER_ALLOW_LATE_JOIN = 'true';

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

async function createInstance(
  opts: Record<string, unknown>,
): Promise<{ id: string }> {
  const res = await fetch(`${baseUrl}/api/instances`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Authorization: ADMIN_AUTH,
    },
    // matchData is what makes `on-match-context` carry something; it is emitted either way,
    // and it is this test file's proof that a socket really joined.
    body: JSON.stringify({
      maxPlayers: 2,
      autoStartWhenFull: true,
      matchData: { v: 1 },
      ...opts,
    }),
  });
  if (!res.ok) {
    throw new Error(`createInstance failed: ${res.status} ${await res.text()}`);
  }
  return res.json() as Promise<{ id: string }>;
}

interface Attempt {
  joined: boolean;
  reason?: string;
}

/**
 * Connect and report whether the socket was admitted. `connect` proves nothing — a refused
 * socket connects first and is then sent `content-error` and disconnected. The positive
 * signal is `on-match-context`, which is emitted from `handleJoin` and therefore only after
 * both gates have passed. Waiting for a positive event rather than for silence also means a
 * hung server fails these tests instead of passing them.
 */
function attempt(
  matchId: string,
  query: Record<string, string>,
): Promise<Attempt> {
  return new Promise((resolve) => {
    const socket: Socket = io(baseUrl, {
      path: '/match-gateway',
      transports: ['websocket'],
      query: { matchId, ...query },
      forceNew: true,
      reconnection: false,
    });
    let settled = false;
    const finish = (a: Attempt) => {
      if (settled) return;
      settled = true;
      socket.close();
      resolve(a);
    };
    socket.on('content-error', (d: { reason?: string }) =>
      finish({ joined: false, reason: d?.reason }),
    );
    socket.on('connect_error', () =>
      finish({ joined: false, reason: 'connect_error' }),
    );
    socket.on('on-match-context', () => finish({ joined: true }));
    setTimeout(() => finish({ joined: false, reason: 'timeout' }), 3000);
  });
}

describe('match access — a token admits the user it was minted for', () => {
  it('refuses a token presented with someone else’s userId', async () => {
    const instance = await createInstance({
      matchAccess: 'token',
      allowedMatchTokens: { u1: 'token-for-u1', u2: 'token-for-u2' },
      users: { u1: 'home', u2: 'away' },
    });

    // The row that used to read "joined". This is the whole point of the change.
    const stolen = await attempt(instance.id, {
      userId: 'u2',
      matchToken: 'token-for-u1',
    });
    expect(stolen.joined).toBe(false);
    expect(stolen.reason).toBe('match-access-denied');
  });

  it('admits each user with its own token', async () => {
    const instance = await createInstance({
      matchAccess: 'token',
      allowedMatchTokens: { u1: 'token-for-u1', u2: 'token-for-u2' },
      users: { u1: 'home', u2: 'away' },
    });

    const a = await attempt(instance.id, {
      userId: 'u1',
      matchToken: 'token-for-u1',
    });
    expect(a).toEqual({ joined: true });
  });

  it('refuses a valid token with no userId at all', async () => {
    const instance = await createInstance({
      matchAccess: 'token',
      allowedMatchTokens: { u1: 'token-for-u1', u2: 'token-for-u2' },
    });

    const a = await attempt(instance.id, { matchToken: 'token-for-u1' });
    expect(a.joined).toBe(false);
    expect(a.reason).toBe('match-access-denied');
  });

  it('refuses a made-up token, and a missing one, as before', async () => {
    const instance = await createInstance({
      matchAccess: 'token',
      allowedMatchTokens: { u1: 'token-for-u1' },
    });

    const guessed = await attempt(instance.id, {
      userId: 'u1',
      matchToken: 'nonsense',
    });
    expect(guessed.reason).toBe('match-access-denied');

    const absent = await attempt(instance.id, { userId: 'u1' });
    expect(absent.reason).toBe('missing-match-token');
  });

  it('still accepts the unbound array form, which stays deliberately loose', async () => {
    const instance = await createInstance({
      matchAccess: 'token',
      allowedMatchTokens: ['shared-a', 'shared-b'],
    });

    // Not a flag day: an existing deployment keeps working, token unbound to any user.
    const a = await attempt(instance.id, {
      userId: 'whoever',
      matchToken: 'shared-a',
    });
    expect(a).toEqual({ joined: true });
  });
});

describe('match access — the roster is checked even when the tokens cannot', () => {
  it('refuses a userId absent from a non-empty users map', async () => {
    const instance = await createInstance({
      matchAccess: 'token',
      allowedMatchTokens: ['shared-a'],
      users: { u1: 'home', u2: 'away' },
    });

    const a = await attempt(instance.id, {
      userId: 'gatecrasher',
      matchToken: 'shared-a',
    });
    expect(a.joined).toBe(false);
    expect(a.reason).toBe('match-access-denied');
  });

  it('refuses an unknown userId on an open match with a declared roster', async () => {
    const instance = await createInstance({ users: { u1: 'home', u2: 'away' } });

    const bad = await attempt(instance.id, { userId: 'gatecrasher' });
    expect(bad.joined).toBe(false);

    const good = await attempt(instance.id, { userId: 'u1' });
    expect(good).toEqual({ joined: true });
  });

  it('checks nothing when no roster was declared', async () => {
    const instance = await createInstance({});
    const a = await attempt(instance.id, { userId: 'anyone-at-all' });
    expect(a).toEqual({ joined: true });
  });
});

describe('match access — provisioning validation', () => {
  it('refuses a bound form whose two users share one token', async () => {
    const res = await fetch(`${baseUrl}/api/instances`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Authorization: ADMIN_AUTH,
      },
      body: JSON.stringify({
        maxPlayers: 2,
        matchAccess: 'token',
        allowedMatchTokens: { u1: 'same', u2: 'same' },
      }),
    });
    expect(res.status).toBe(400);
    expect(await res.text()).toContain('distinct');
  });

  it('refuses an empty bound form', async () => {
    const res = await fetch(`${baseUrl}/api/instances`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Authorization: ADMIN_AUTH,
      },
      body: JSON.stringify({
        maxPlayers: 2,
        matchAccess: 'token',
        allowedMatchTokens: {},
      }),
    });
    expect(res.status).toBe(400);
  });
});

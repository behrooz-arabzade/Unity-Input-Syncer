import 'reflect-metadata';
import { Test } from '@nestjs/testing';
import { INestApplication } from '@nestjs/common';
import { IoAdapter } from '@nestjs/platform-socket.io';
import { io, Socket } from 'socket.io-client';
import { AppModule } from '../src/app.module';

/** Set by tests/setup/adminAuth.js — the guard fails closed without it (E08/S10). */
const ADMIN_AUTH = `Bearer ${process.env.INPUT_SYNCER_ADMIN_AUTH_TOKEN ?? ''}`;


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
  const url = await app.getUrl();
  baseUrl = url.replace('[::1]', 'localhost');
});

afterAll(async () => {
  await app.close().catch(() => {});
});

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

interface InstanceInfo {
  id: string;
  state: string;
  matchStarted: boolean;
}

async function createInstance(opts?: Record<string, unknown>): Promise<InstanceInfo> {
  const res = await fetch(`${baseUrl}/api/instances`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Authorization: ADMIN_AUTH,
    },
    body: JSON.stringify({
      maxPlayers: 2,
      autoStartWhenFull: true,
      allowLateJoin: true,
      disconnectAbandonTimeoutSeconds: 2,
      ...opts,
    }),
  });
  if (!res.ok) {
    const body = await res.text();
    throw new Error(`createInstance failed: ${res.status} ${body}`);
  }
  return res.json() as Promise<InstanceInfo>;
}

function connectPlayer(matchId: string, userId: string): Socket {
  return io(baseUrl, {
    path: '/match-gateway',
    transports: ['websocket'],
    query: { matchId, userId },
    forceNew: true,
  });
}

function waitForConnect(socket: Socket): Promise<void> {
  return new Promise((resolve, reject) => {
    socket.on('connect', () => resolve());
    socket.on('connect_error', (err) => reject(err));
    setTimeout(() => reject(new Error('connect timeout')), 5000);
  });
}

function collectStepInputs(socket: Socket): Record<string, unknown>[] {
  const inputs: Record<string, unknown>[] = [];
  socket.on('on-steps', (steps: any[]) => {
    for (const step of steps) {
      if (step.inputs) {
        for (const input of step.inputs) {
          inputs.push(input);
        }
      }
    }
  });
  return inputs;
}

function waitForMatchStart(socket: Socket): Promise<void> {
  return new Promise((resolve) => {
    socket.on('on-start', () => resolve());
    setTimeout(() => resolve(), 3000);
  });
}

describe('abandon flow — Socket.IO integration', () => {
  let sockets: Socket[] = [];

  afterEach(() => {
    for (const s of sockets) {
      if (s.connected) s.disconnect();
    }
    sockets = [];
  });

  it('disconnect injects disconnect input into step stream', async () => {
    const instance = await createInstance();
    const playerA = connectPlayer(instance.id, 'user-a');
    const playerB = connectPlayer(instance.id, 'user-b');
    sockets.push(playerA, playerB);

    await Promise.all([waitForConnect(playerA), waitForConnect(playerB)]);

    const inputsB = collectStepInputs(playerB);

    // Wait for match to start (autoStartWhenFull)
    await waitForMatchStart(playerB);
    await delay(200);

    // Player A disconnects
    playerA.disconnect();

    // Wait for a few steps to include the disconnect input
    await delay(500);

    const disconnectInput = inputsB.find((i) => i.type === 'disconnect');
    expect(disconnectInput).toBeDefined();
    expect(disconnectInput!.userId).toBe('user-a');
    expect(disconnectInput!.reason).toBe('socket_closed');
  });

  it('reconnect cancels abandon timer and injects reconnect input', async () => {
    const instance = await createInstance();
    const playerA = connectPlayer(instance.id, 'user-a');
    const playerB = connectPlayer(instance.id, 'user-b');
    sockets.push(playerA, playerB);

    await Promise.all([waitForConnect(playerA), waitForConnect(playerB)]);

    const inputsB = collectStepInputs(playerB);

    await waitForMatchStart(playerB);
    await delay(200);

    // Player A disconnects
    playerA.disconnect();
    await delay(500);

    // Player A reconnects with same userId
    const playerA2 = connectPlayer(instance.id, 'user-a');
    sockets.push(playerA2);
    await waitForConnect(playerA2);

    await delay(500);

    const reconnectInput = inputsB.find((i) => i.type === 'reconnect');
    expect(reconnectInput).toBeDefined();
    expect(reconnectInput!.userId).toBe('user-a');
    expect(reconnectInput!.reason).toBe('client_returned');

    // Wait past the full timeout (2s) — no abandon should appear
    await delay(2500);

    const abandonInput = inputsB.find((i) => i.type === 'abandon');
    expect(abandonInput).toBeUndefined();
  });

  it('disconnect timeout triggers abandon input', async () => {
    const instance = await createInstance({ disconnectAbandonTimeoutSeconds: 2 });
    const playerA = connectPlayer(instance.id, 'user-a');
    const playerB = connectPlayer(instance.id, 'user-b');
    sockets.push(playerA, playerB);

    await Promise.all([waitForConnect(playerA), waitForConnect(playerB)]);

    const inputsB = collectStepInputs(playerB);

    await waitForMatchStart(playerB);
    await delay(200);

    // Player A disconnects
    playerA.disconnect();

    // Wait for timeout (2s + buffer)
    await delay(2500);

    const abandonInput = inputsB.find((i) => i.type === 'abandon');
    expect(abandonInput).toBeDefined();
    expect(abandonInput!.userId).toBe('user-a');
    expect(abandonInput!.reason).toBe('disconnect_timeout');
  });

  it('manual abandon input from client', async () => {
    const instance = await createInstance();
    const playerA = connectPlayer(instance.id, 'user-a');
    const playerB = connectPlayer(instance.id, 'user-b');
    sockets.push(playerA, playerB);

    await Promise.all([waitForConnect(playerA), waitForConnect(playerB)]);

    const inputsB = collectStepInputs(playerB);

    await waitForMatchStart(playerB);
    await delay(200);

    // Player A manually abandons
    playerA.emit('abandon');

    await delay(500);

    const abandonInput = inputsB.find((i) => i.type === 'abandon');
    expect(abandonInput).toBeDefined();
    expect(abandonInput!.userId).toBe('user-a');
    expect(abandonInput!.reason).toBe('manual');
  });
});

const NAKAMA_URL = process.env.NAKAMA_HTTP_URL || '';
const NAKAMA_KEY = process.env.NAKAMA_SERVER_KEY || 'defaulthttpkey';

const describeWithNakama = NAKAMA_URL ? describe : describe.skip;

describeWithNakama('abandon flow — full Nakama integration', () => {
  let sockets: Socket[] = [];

  afterEach(() => {
    for (const s of sockets) {
      if (s.connected) s.disconnect();
    }
    sockets = [];
  });

  async function nakamaServerRpc<T = unknown>(rpcId: string, payload: object): Promise<T> {
    const url = `${NAKAMA_URL}/v2/rpc/${rpcId}?http_key=${NAKAMA_KEY}`;
    const res = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(JSON.stringify(payload)),
    });
    if (!res.ok) {
      const body = await res.text().catch(() => '');
      throw new Error(`Nakama RPC ${rpcId} failed: ${res.status} ${body}`);
    }
    const json = (await res.json()) as any;
    if (json.payload) {
      return typeof json.payload === 'string' ? JSON.parse(json.payload) : json.payload;
    }
    return json as T;
  }

  async function nakamaAuth(): Promise<{ userId: string; token: string }> {
    const customId = `test-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
    const url = `${NAKAMA_URL}/v2/account/authenticate/custom?create=true`;
    const res = await fetch(url, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Authorization: 'Basic ' + Buffer.from('defaultkey:').toString('base64'),
      },
      body: JSON.stringify({ id: customId }),
    });
    if (!res.ok) throw new Error(`nakamaAuth failed: ${res.status}`);
    const json = (await res.json()) as any;
    const tokenParts = json.token.split('.');
    const claims = JSON.parse(Buffer.from(tokenParts[1], 'base64').toString());
    return { userId: claims.uid, token: json.token };
  }

  async function nakamaRpcAsUser<T = unknown>(token: string, rpcId: string, payload: object): Promise<T> {
    const url = `${NAKAMA_URL}/v2/rpc/${rpcId}`;
    const res = await fetch(url, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Authorization: `Bearer ${token}`,
      },
      body: JSON.stringify(JSON.stringify(payload)),
    });
    if (!res.ok) {
      const body = await res.text().catch(() => '');
      throw new Error(`Nakama RPC ${rpcId} failed: ${res.status} ${body}`);
    }
    const json = (await res.json()) as any;
    if (json.payload) {
      return typeof json.payload === 'string' ? JSON.parse(json.payload) : json.payload;
    }
    return json as T;
  }

  it('disconnect timeout → Nakama abandon + finish with penalty', async () => {
    // Create Nakama users and match
    const adminAuth = await nakamaAuth();
    await nakamaRpcAsUser(adminAuth.token, 'fv_admin_group_ensure', {
      group_name: 'fv_match_manager_admins',
      secret: 'test-secret',
    });

    const userAAuth = await nakamaAuth();
    const userBAuth = await nakamaAuth();

    const nakamaMatchId = crypto.randomUUID();
    await nakamaRpcAsUser(adminAuth.token, 'match_manager_admin_upsert', {
      match_id: nakamaMatchId,
      participant_user_ids: [userAAuth.userId, userBAuth.userId],
      status: 'active',
    });

    // Create Socket.IO instance linked to Nakama match
    const instance = await createInstance({
      nakamaMatchId,
      disconnectAbandonTimeoutSeconds: 2,
    });

    const playerA = connectPlayer(instance.id, userAAuth.userId);
    const playerB = connectPlayer(instance.id, userBAuth.userId);
    sockets.push(playerA, playerB);

    await Promise.all([waitForConnect(playerA), waitForConnect(playerB)]);
    await waitForMatchStart(playerB);
    await delay(300);

    // Player A disconnects → timeout → abandon reported to Nakama
    playerA.disconnect();
    await delay(2500);

    // Player B sends session finish (win)
    playerB.emit('player-session-finish', {
      data: { home_score: 3, away_score: 1 },
    });
    await delay(1000);

    // Verify Nakama: match should be completed with abandon
    const matchGet = await nakamaRpcAsUser<any>(adminAuth.token, 'match_manager_get', {
      match_id: nakamaMatchId,
    });
    expect(matchGet.match.status).toBe('completed');
    expect(matchGet.match.result.abandoned_users).toContain(userAAuth.userId);
    expect(matchGet.match.result.resolved_by).toBe('abandon');
  });
});

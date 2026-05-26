import 'reflect-metadata';
import { Test } from '@nestjs/testing';
import { INestApplication } from '@nestjs/common';
import { IoAdapter } from '@nestjs/platform-socket.io';
import { io, Socket } from 'socket.io-client';
import { randomUUID } from 'node:crypto';
import { AppModule } from '../src/app.module';

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
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      maxPlayers: 2,
      autoStartWhenFull: true,
      allowLateJoin: true,
      stepIntervalSeconds: 0.05,
      disconnectAbandonTimeoutSeconds: 30,
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

function waitForMatchStart(socket: Socket): Promise<void> {
  return new Promise((resolve) => {
    socket.on('on-start', () => resolve());
    setTimeout(() => resolve(), 3000);
  });
}

const NAKAMA_URL = process.env.NAKAMA_HTTP_URL || '';
const NAKAMA_KEY = process.env.NAKAMA_SERVER_KEY || 'defaulthttpkey';

const describeWithNakama = NAKAMA_URL ? describe : describe.skip;

describeWithNakama('match input log — Socket.IO + Nakama integration', () => {
  let sockets: Socket[] = [];

  afterEach(() => {
    for (const s of sockets) {
      if (s.connected) s.disconnect();
    }
    sockets = [];
  });

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

  async function nakamaRpcAsUser<T = unknown>(
    token: string,
    rpcId: string,
    payload: object,
  ): Promise<T> {
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

  it(
    'PvP match: InputSyncer uploads per-step input log to Nakama on quorum finish',
    async () => {
      // 1. Bootstrap two players + an admin in Nakama.
      const adminAuth = await nakamaAuth();
      await nakamaRpcAsUser(adminAuth.token, 'fv_admin_group_ensure', {
        group_name: 'fv_match_manager_admins',
        secret: 'test-secret',
      });

      const userAAuth = await nakamaAuth();
      const userBAuth = await nakamaAuth();

      // Both players need a squad before fv_match_loadouts_get can build a snapshot.
      await nakamaRpcAsUser(userAAuth.token, 'fv_team_squad_get', {});
      await nakamaRpcAsUser(userBAuth.token, 'fv_team_squad_get', {});

      // 2. Create the managed match.
      const nakamaMatchId = randomUUID();
      await nakamaRpcAsUser(adminAuth.token, 'match_manager_admin_upsert', {
        match_id: nakamaMatchId,
        participant_user_ids: [userAAuth.userId, userBAuth.userId],
        status: 'active',
      });

      // 3. Materialize the loadout snapshot — fv_match_input_log_put auth
      //    checks participant membership in custom_match_loadouts/<id>.
      await nakamaRpcAsUser(userAAuth.token, 'fv_match_loadouts_get', {
        match_id: nakamaMatchId,
      });

      // 4. Create the Socket.IO instance bound to the Nakama match.
      const instance = await createInstance({ nakamaMatchId });

      const playerA = connectPlayer(instance.id, userAAuth.userId);
      const playerB = connectPlayer(instance.id, userBAuth.userId);
      sockets.push(playerA, playerB);

      await Promise.all([waitForConnect(playerA), waitForConnect(playerB)]);
      await waitForMatchStart(playerA);
      await delay(150);

      // 5. Each player submits one input — these are what we expect the
      //    persisted log to contain.
      playerA.emit('input', {
        inputData: { type: 'duel-choice', slot: 0, action_id: 'card_pass' },
      });
      playerB.emit('input', {
        inputData: { type: 'duel-choice', slot: 1, action_id: 'card_tackle' },
      });
      // Let a few step intervals fire so the inputs land in stepHistory.
      await delay(400);

      // 6. Quorum finish — both players send `user-finish`. With
      //    quorumUserFinishEndsMatch (default true), once every joined
      //    player has finished, the server fires finishMatch → the bridge
      //    uploads the input log.
      playerA.emit('user-finish');
      playerB.emit('user-finish');

      // Wait for: server-side finishMatch → fire-and-forget upload → Nakama
      // RPC round-trip. The InputSyncer logs the upload result; we just need
      // to give the chain time to settle.
      await delay(2500);

      // 7. Read back from Nakama and assert.
      const got = await nakamaRpcAsUser<{
        match_id: string;
        log_steps: Array<{ step: number; inputs: Array<Record<string, unknown>> }>;
        finish_reason: string;
        recorded_by: string;
      }>(userAAuth.token, 'fv_match_input_log_get', { match_id: nakamaMatchId });

      expect(got.match_id).toBe(nakamaMatchId);
      // InputSyncer uploads via http_key (server-to-server), so recorded_by == "server".
      expect(got.recorded_by).toBe('server');
      // Quorum finish is "completed".
      expect(got.finish_reason).toBe('completed');

      // The log must include both submitted inputs. They're keyed by userId
      // by handleInput, so check each user's input made it through.
      const allInputs: Array<Record<string, unknown>> = [];
      for (const s of got.log_steps) {
        for (const inp of s.inputs ?? []) allInputs.push(inp);
      }
      const inputFromA = allInputs.find(
        (i) => i.userId === userAAuth.userId && i.action_id === 'card_pass',
      );
      const inputFromB = allInputs.find(
        (i) => i.userId === userBAuth.userId && i.action_id === 'card_tackle',
      );
      expect(inputFromA).toBeDefined();
      expect(inputFromB).toBeDefined();

      // Steps must be sorted ascending (the bridge sorts before upload).
      let prev = -1;
      for (const s of got.log_steps) {
        expect(s.step).toBeGreaterThanOrEqual(prev);
        prev = s.step;
      }
    },
    30000,
  );
});

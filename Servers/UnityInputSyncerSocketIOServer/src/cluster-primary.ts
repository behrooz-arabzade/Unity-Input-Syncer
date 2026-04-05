/**
 * Multi-process entry: spawns N NestJS workers (each with its own in-memory match pool)
 * and exposes a single public HTTP/WebSocket port that routes by instance id (matchId).
 */
import * as crypto from 'crypto';
import { ChildProcess, fork } from 'child_process';
import * as fs from 'fs';
import * as http from 'http';
import httpProxy from 'http-proxy';
import * as os from 'os';
import * as path from 'path';
import { URL } from 'url';

const LOG = '[InputSyncerCluster]';
const INTERNAL_HEADER = 'x-input-syncer-internal';

type AdminInstanceInfo = {
  id: string;
  state: string;
  playerCount: number;
  joinedPlayerCount: number;
  matchStarted: boolean;
  matchFinished: boolean;
  createdAt: string;
  currentStep: number;
  uptimeSeconds: number;
  matchAccess: 'open' | 'password' | 'token';
  allowedMatchTokenCount: number;
  serverUrl?: string;
  clientConnection?: {
    transport: string;
    matchId: string;
    host: string;
    port: number;
    socketIoUrl: string;
    matchGatewayPath: string;
  };
};

type AdminPoolStats = {
  totalInstances: number;
  availableSlots: number;
  idleCount: number;
  waitingCount: number;
  inMatchCount: number;
  finishedCount: number;
  instances: AdminInstanceInfo[];
  resourceUsage: {
    heapUsedBytes: number;
    rssBytes: number;
    processorCount: number;
  };
};

type PoolMeta = {
  instanceCount: number;
  availableSlots: number;
  maxInstances: number;
};

function envInt(name: string, fallback: number): number {
  const raw = process.env[name];
  if (!raw) return fallback;
  const n = parseInt(raw, 10);
  return Number.isNaN(n) ? fallback : n;
}

function readBody(req: http.IncomingMessage): Promise<Buffer> {
  return new Promise((resolve, reject) => {
    const chunks: Buffer[] = [];
    req.on('data', (c: Buffer | string) =>
      chunks.push(Buffer.isBuffer(c) ? c : Buffer.from(c)),
    );
    req.on('end', () => resolve(Buffer.concat(chunks)));
    req.on('error', reject);
  });
}

function applyCors(res: http.ServerResponse): void {
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader(
    'Access-Control-Allow-Methods',
    'GET, POST, DELETE, OPTIONS',
  );
  res.setHeader(
    'Access-Control-Allow-Headers',
    'Authorization, Content-Type',
  );
}

function copyFetchHeadersToNode(
  src: Headers,
  res: http.ServerResponse,
): void {
  const hop = new Set([
    'connection',
    'keep-alive',
    'transfer-encoding',
    'content-encoding',
  ]);
  src.forEach((value, key) => {
    if (!hop.has(key.toLowerCase())) {
      res.setHeader(key, value);
    }
  });
}

async function fetchPoolMeta(
  port: number,
  secret: string,
): Promise<PoolMeta> {
  const res = await fetch(`http://127.0.0.1:${port}/api/internal/pool-meta`, {
    headers: { [INTERNAL_HEADER]: secret },
    signal: AbortSignal.timeout(8000),
  });
  if (!res.ok) {
    throw new Error(`pool-meta ${port} HTTP ${res.status}`);
  }
  return (await res.json()) as PoolMeta;
}

async function findWorkerPortForInstance(
  id: string,
  workerPorts: number[],
  secret: string,
): Promise<number | null> {
  for (const p of workerPorts) {
    try {
      const res = await fetch(
        `http://127.0.0.1:${p}/api/internal/instance/${encodeURIComponent(id)}/exists`,
        {
          headers: { [INTERNAL_HEADER]: secret },
          signal: AbortSignal.timeout(5000),
        },
      );
      if (!res.ok) continue;
      const j = (await res.json()) as { exists?: boolean };
      if (j.exists) return p;
    } catch {
      /* try next worker */
    }
  }
  return null;
}

async function pickWorkerForCreate(
  workerPorts: number[],
  secret: string,
): Promise<number> {
  const scored: { port: number; slots: number; count: number }[] = [];
  for (const p of workerPorts) {
    try {
      const m = await fetchPoolMeta(p, secret);
      scored.push({
        port: p,
        slots: m.availableSlots,
        count: m.instanceCount,
      });
    } catch (e) {
      console.error(`${LOG} worker ${p} unhealthy:`, e);
    }
  }
  if (scored.length === 0) {
    throw new Error('No healthy workers');
  }
  scored.sort((a, b) => {
    if (b.slots !== a.slots) return b.slots - a.slots;
    return a.count - b.count;
  });
  return scored[0].port;
}

function mergeStats(parts: AdminPoolStats[]): AdminPoolStats {
  const merged: AdminPoolStats = {
    totalInstances: 0,
    availableSlots: 0,
    idleCount: 0,
    waitingCount: 0,
    inMatchCount: 0,
    finishedCount: 0,
    instances: [],
    resourceUsage: {
      heapUsedBytes: 0,
      rssBytes: 0,
      processorCount: os.cpus().length,
    },
  };
  for (const s of parts) {
    merged.totalInstances += s.totalInstances;
    merged.availableSlots += s.availableSlots;
    merged.idleCount += s.idleCount;
    merged.waitingCount += s.waitingCount;
    merged.inMatchCount += s.inMatchCount;
    merged.finishedCount += s.finishedCount;
    merged.instances.push(...s.instances);
    merged.resourceUsage.heapUsedBytes += s.resourceUsage.heapUsedBytes;
    merged.resourceUsage.rssBytes += s.resourceUsage.rssBytes;
  }
  return merged;
}

async function waitForWorkers(
  workerPorts: number[],
  secret: string,
  deadlineMs: number,
): Promise<void> {
  const start = Date.now();
  for (;;) {
    let ok = 0;
    for (const p of workerPorts) {
      try {
        await fetchPoolMeta(p, secret);
        ok++;
      } catch {
        /* not ready */
      }
    }
    if (ok === workerPorts.length) return;
    if (Date.now() - start > deadlineMs) {
      throw new Error(
        `Workers not healthy within ${deadlineMs}ms (${ok}/${workerPorts.length} up)`,
      );
    }
    await new Promise((r) => setTimeout(r, 200));
  }
}

function installEditorLogMirror(): void {
  const p = process.env.INPUT_SYNCER_EDITOR_LOG;
  if (!p) return;
  const wrap = (stream: NodeJS.WriteStream) => {
    const orig = stream.write.bind(stream) as (
      chunk: unknown,
      encoding?: BufferEncoding | ((err?: Error | null) => void),
      cb?: (err?: Error | null) => void,
    ) => boolean;
    (stream as NodeJS.WriteStream & { write: typeof stream.write }).write = (
      chunk: unknown,
      enc?: BufferEncoding | ((err?: Error | null) => void),
      cb?: (err?: Error | null) => void,
    ): boolean => {
      try {
        if (chunk !== undefined && chunk !== null) {
          const s =
            typeof chunk === 'string'
              ? chunk
              : Buffer.isBuffer(chunk)
                ? chunk.toString('utf8')
                : String(chunk);
          fs.appendFileSync(p, s, 'utf8');
        }
      } catch {
        /* ignore */
      }
      return orig(chunk, enc as never, cb as never);
    };
  };
  wrap(process.stdout);
  wrap(process.stderr);
}

installEditorLogMirror();

async function main(): Promise<void> {
  const publicPort = envInt('INPUT_SYNCER_PORT', 3000);
  const cpu = os.cpus().length;
  const workerCount = Math.max(
    1,
    envInt(
      'INPUT_SYNCER_WORKER_COUNT',
      Math.max(1, cpu > 1 ? cpu - 1 : 1),
    ),
  );
  const internalBase = envInt(
    'INPUT_SYNCER_INTERNAL_PORT_BASE',
    publicPort + 1,
  );

  if (internalBase <= publicPort) {
    throw new Error(
      `INPUT_SYNCER_INTERNAL_PORT_BASE (${internalBase}) must be greater than INPUT_SYNCER_PORT (${publicPort})`,
    );
  }

  const workerPorts: number[] = [];
  for (let i = 0; i < workerCount; i++) {
    workerPorts.push(internalBase + i);
  }

  const secret = crypto.randomBytes(32).toString('hex');
  const mainJs = path.join(__dirname, 'main.js');

  const instanceIdToPort = new Map<string, number>();
  const children: ChildProcess[] = [];
  let shuttingDown = false;

  const startWorker = (index: number): void => {
    const wport = workerPorts[index];
    if (!wport) return;

    const child = fork(mainJs, [], {
      env: {
        ...process.env,
        INPUT_SYNCER_ROLE: 'worker',
        INPUT_SYNCER_WORKER_INDEX: String(index),
        INPUT_SYNCER_PORT: String(wport),
        INPUT_SYNCER_BIND: '127.0.0.1',
        INPUT_SYNCER_INTERNAL_SECRET: secret,
      },
      stdio: 'inherit',
    });

    children[index] = child;

    child.on('exit', (code, signal) => {
      console.error(
        `${LOG} worker ${index} (127.0.0.1:${wport}) exited code=${code} signal=${signal}`,
      );
      for (const [id, p] of instanceIdToPort) {
        if (p === wport) instanceIdToPort.delete(id);
      }
      if (shuttingDown) return;
      setTimeout(() => {
        if (shuttingDown) return;
        console.log(`${LOG} restarting worker ${index}…`);
        startWorker(index);
      }, 1000);
    });
  };

  for (let i = 0; i < workerCount; i++) {
    startWorker(i);
  }

  console.log(
    `${LOG} Starting ${workerCount} workers on 127.0.0.1:${workerPorts[0]}–${workerPorts[workerPorts.length - 1]}…`,
  );

  await waitForWorkers(workerPorts, secret, 60000);

  const proxy = httpProxy.createProxyServer({
    ws: true,
    xfwd: true,
    changeOrigin: true,
  });

  proxy.on('error', (err: Error, _req: unknown, res: unknown) => {
    console.error(`${LOG} proxy error:`, err);
    if (res && typeof (res as http.ServerResponse).writeHead === 'function') {
      const r = res as http.ServerResponse;
      if (!r.headersSent) {
        r.writeHead(502);
        r.end('Bad gateway');
      }
    }
  });

  const server = http.createServer((req, res) => {
    void (async () => {
      applyCors(res);

      if (req.method === 'OPTIONS') {
        res.writeHead(204);
        res.end();
        return;
      }

      const url = new URL(req.url ?? '/', 'http://localhost');
      const pathname = url.pathname;
      const auth = req.headers.authorization;

      try {
        if (pathname === '/api/instances' && req.method === 'POST') {
          const body = await readBody(req);
          const target = await pickWorkerForCreate(workerPorts, secret);
          const headers: Record<string, string> = {
            'content-type':
              (req.headers['content-type'] as string) || 'application/json',
          };
          if (auth) headers.authorization = auth;

          const r = await fetch(`http://127.0.0.1:${target}/api/instances`, {
            method: 'POST',
            headers,
            body: body.length ? new Uint8Array(body) : undefined,
            signal: AbortSignal.timeout(120000),
          });
          const text = await r.text();
          if (r.status === 201) {
            try {
              const j = JSON.parse(text) as { id?: string };
              if (typeof j.id === 'string') {
                instanceIdToPort.set(j.id, target);
              }
            } catch {
              /* ignore */
            }
          }
          copyFetchHeadersToNode(r.headers, res);
          res.writeHead(r.status);
          res.end(text);
          return;
        }

        if (pathname === '/api/instances' && req.method === 'GET') {
          const headers: Record<string, string> = {};
          if (auth) headers.authorization = auth;

          const lists = await Promise.all(
            workerPorts.map(async (p) => {
              const r = await fetch(`http://127.0.0.1:${p}/api/instances`, {
                headers,
                signal: AbortSignal.timeout(60000),
              });
              if (!r.ok) {
                throw new Error(`GET instances worker ${p}: ${r.status}`);
              }
              return (await r.json()) as AdminInstanceInfo[];
            }),
          );
          const merged = lists.flat();
          res.setHeader('content-type', 'application/json');
          res.writeHead(200);
          res.end(JSON.stringify(merged));
          return;
        }

        const instMatch = /^\/api\/instances\/([^/]+)$/.exec(pathname);
        if (instMatch && req.method === 'GET') {
          const id = instMatch[1];
          let target = instanceIdToPort.get(id);
          if (target == null) {
            target =
              (await findWorkerPortForInstance(id, workerPorts, secret)) ??
              undefined;
            if (target != null) instanceIdToPort.set(id, target);
          }
          if (target == null) {
            res.setHeader('content-type', 'application/json');
            res.writeHead(404);
            res.end(JSON.stringify({ error: 'Not found' }));
            return;
          }
          const headers: Record<string, string> = {};
          if (auth) headers.authorization = auth;
          const r = await fetch(
            `http://127.0.0.1:${target}/api/instances/${encodeURIComponent(id)}`,
            { headers, signal: AbortSignal.timeout(60000) },
          );
          const text = await r.text();
          copyFetchHeadersToNode(r.headers, res);
          res.writeHead(r.status);
          res.end(text);
          return;
        }

        if (instMatch && req.method === 'DELETE') {
          const id = instMatch[1];
          let target = instanceIdToPort.get(id);
          if (target == null) {
            target =
              (await findWorkerPortForInstance(id, workerPorts, secret)) ??
              undefined;
          }
          if (target == null) {
            res.setHeader('content-type', 'application/json');
            res.writeHead(404);
            res.end(JSON.stringify({ error: 'Not found' }));
            return;
          }
          const headers: Record<string, string> = {};
          if (auth) headers.authorization = auth;
          const r = await fetch(
            `http://127.0.0.1:${target}/api/instances/${encodeURIComponent(id)}`,
            { method: 'DELETE', headers, signal: AbortSignal.timeout(60000) },
          );
          const text = await r.text();
          if (r.status === 200 || r.status === 204) {
            instanceIdToPort.delete(id);
          }
          copyFetchHeadersToNode(r.headers, res);
          res.writeHead(r.status);
          if (text.length) res.end(text);
          else res.end();
          return;
        }

        if (pathname === '/api/stats' && req.method === 'GET') {
          const headers: Record<string, string> = {};
          if (auth) headers.authorization = auth;

          const statsList = await Promise.all(
            workerPorts.map(async (p) => {
              const r = await fetch(`http://127.0.0.1:${p}/api/stats`, {
                headers,
                signal: AbortSignal.timeout(60000),
              });
              if (!r.ok) {
                throw new Error(`GET stats worker ${p}: ${r.status}`);
              }
              return (await r.json()) as AdminPoolStats;
            }),
          );
          const merged = mergeStats(statsList);
          res.setHeader('content-type', 'application/json');
          res.writeHead(200);
          res.end(JSON.stringify(merged));
          return;
        }

        if (pathname.startsWith('/match-gateway')) {
          const matchId = url.searchParams.get('matchId');
          if (!matchId) {
            res.writeHead(400);
            res.end('matchId query parameter required');
            return;
          }
          let target = instanceIdToPort.get(matchId);
          if (target == null) {
            const found = await findWorkerPortForInstance(
              matchId,
              workerPorts,
              secret,
            );
            if (found != null) {
              target = found;
              instanceIdToPort.set(matchId, target);
            }
          }
          if (target == null) {
            res.writeHead(502);
            res.end('Unknown match instance');
            return;
          }
          proxy.web(req, res, {
            target: `http://127.0.0.1:${target}`,
          });
          return;
        }

        res.writeHead(404);
        res.end('Not found');
      } catch (e) {
        console.error(`${LOG} request error:`, e);
        if (!res.headersSent) {
          res.setHeader('content-type', 'application/json');
          res.writeHead(500);
          res.end(JSON.stringify({ error: 'Cluster primary error' }));
        }
      }
    })();
  });

  server.on('upgrade', (req, socket, head) => {
    try {
      const url = new URL(req.url ?? '/', 'http://localhost');
      if (!url.pathname.startsWith('/match-gateway')) {
        socket.destroy();
        return;
      }
      const matchId = url.searchParams.get('matchId');
      if (!matchId) {
        socket.destroy();
        return;
      }

      void (async () => {
        let target = instanceIdToPort.get(matchId);
        if (target == null) {
          const found = await findWorkerPortForInstance(
            matchId,
            workerPorts,
            secret,
          );
          if (found != null) {
            target = found;
            instanceIdToPort.set(matchId, target);
          }
        }
        if (target == null) {
          socket.destroy();
          return;
        }
        proxy.ws(req, socket, head, {
          target: `http://127.0.0.1:${target}`,
        });
      })();
    } catch {
      socket.destroy();
    }
  });

  const shutdown = (): void => {
    if (shuttingDown) return;
    shuttingDown = true;
    console.log(`${LOG} shutting down…`);
    for (const c of children) {
      try {
        if (c && !c.killed) c.kill('SIGTERM');
      } catch {
        /* ignore */
      }
    }
    server.close();
    process.exit(0);
  };

  process.on('SIGINT', shutdown);
  process.on('SIGTERM', shutdown);

  await new Promise<void>((resolve, reject) => {
    server.listen(publicPort, () => resolve());
    server.on('error', reject);
  });

  console.log(`${LOG} Public listen on 0.0.0.0:${publicPort}`);
  console.log(`${LOG} Admin API: http://localhost:${publicPort}/api`);
  console.log(`${LOG} WebSocket path: /match-gateway`);
}

main().catch((err: unknown) => {
  console.error(`${LOG} fatal`, err);
  process.exit(1);
});

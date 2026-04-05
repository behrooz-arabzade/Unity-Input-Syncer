# Unity Input Syncer

A Unity 6 library for deterministic multiplayer input synchronization using lockstep architecture.

## Table of Contents

- [Introduction](#introduction)
- [Architecture](#architecture)
- [Wire protocol & events](#wire-protocol--events)
- [Socket.IO server (NestJS)](#socketio-server-nestjs)
- [Getting Started](#getting-started)
- [How-To Examples](#how-to-examples)
- [Admin HTTP API (workflows)](#admin-http-api-workflows)
- [Match access (open / password / token)](#match-access-open--password--token)
- [Match finish & session APIs](#match-finish--session-apis)
- [SyncSimulation (ECS)](#syncsimulation-ecs)
- [Features](#features)

---

## Introduction

Unity Input Syncer provides a client-side SDK and server components for synchronizing player inputs across clients in a deterministic lockstep simulation. Instead of syncing game state, it synchronizes _inputs_ — the server collects all player inputs each tick, batches them into numbered "steps," and broadcasts them to every client. Each client then replays the same inputs in the same order, producing identical simulation results.

### Key Highlights

- **Dual transport support** — Connect via Socket.IO (TCP/WebSocket) or Unity Transport Package (UDP) with a single unified API.
- **Reference Socket.IO server** — A NestJS app under `Assets/UnityInputSyncerSocketIOServer/` implements the same match pool + admin API as the UTP multi-instance server (optional single-process or multi-worker cluster).
- **Dedicated server builds** — Ship headless UTP servers from pre-configured Unity scenes, configurable via environment variables.
- **Multi-instance server pool (UTP)** — Host multiple match instances on one machine with automatic port allocation and lifecycle management.
- **Admin HTTP API** — Create, monitor, and destroy match instances via authenticated REST endpoints (UTP pool and NestJS server).
- **Mock mode** — Develop and test game logic offline without a server.
- **Match context & access control** — Admin-defined `matchData` / per-user payloads (`on-match-context`), plus optional password or token gates per instance.
- **Optional ECS layer** — `SyncSimulation` builds a dedicated Entities world with prediction, rollback, and input merging on top of `InputSyncerState`.

---

## Architecture

### High-Level Data Flow

```
┌──────────────┐         ┌──────────────────────┐         ┌──────────────┐
│   Client A   │         │       Server         │         │   Client B   │
│              │         │                      │         │              │
│  SendInput() ├────────►│  Collect inputs      │◄────────┤ SendInput()  │
│              │         │  Batch into step N   │         │              │
│              │         │  Broadcast step N    │         │              │
│  OnSteps ◄───┤◄────────┤──────────────────────┤────────►├───► OnSteps  │
│              │         │                      │         │              │
│  FixedUpdate │         │                      │         │  FixedUpdate │
│  HasStep(N)  │         │                      │         │  HasStep(N)  │
│  GetInputs(N)│         │                      │         │  GetInputs(N)│
│  Simulate    │         │                      │         │  Simulate    │
└──────────────┘         └──────────────────────┘         └──────────────┘
```

### Choosing a Server Stack

| Role | UTP (Unity / C#) | Socket.IO (NestJS) |
|------|------------------|--------------------|
| Process | Unity headless build (`DedicatedServerBootstrap` or `MultiInstanceServerBootstrap`) | Node.js (`npm run start:prod` or `start:cluster`) |
| Client transport | `UTPClientDriver` (UDP) | `SocketIODriver` (WebSocket, path `/match-gateway`) |
| Multi-match | `InputSyncerServerPool` + per-instance UDP ports | In-memory pool; all matches share the HTTP/WebSocket port; clients disambiguate with `matchId` query |
| Admin API | `AdminHttpServer` on `INPUT_SYNCER_ADMIN_PORT` | Same routes on `INPUT_SYNCER_PORT` (Nest HTTP) |

Game simulation still runs on clients in the default model; the server only synchronizes inputs (and optional custom JSON/binary events you add).

### Namespace Breakdown

The codebase is organized under `Assets/`:

**UnityInputSyncerCore** — Shared networking and utility layer.

- `UTPSocket/` — Custom socket abstraction over Unity Transport Package (connection lifecycle, handshake, heartbeat, binary wire protocol).
- `INativeArraySerializable` — Interface for binary serialization to/from `NativeArray<byte>`.
- `InputSyncerEvents` — String constants for all protocol event names.
- `InputSyncerFinishReasons` — Constants for `on-finish` payload `reason`.
- `Utils/PlayerLoopHook` — Injects tick callbacks into Unity's player loop without MonoBehaviour.
- `Utils/UnityThreadDispatcher` — Dispatches work back to the main thread from background tasks.

**UnityInputSyncerClient** — Client SDK consumed by game code.

- `IClientDriver` — Abstract base class defining the transport contract (connect, disconnect, emit, listen).
- `SocketIODriver` — Socket.IO implementation: WebSocket only, fixed gateway path `/match-gateway`, query string from `SocketIODriverOptions.Payload`, optional `Authorization: Bearer` from `JwtToken`. **No binary events.**
- `UTPClientDriver` — UTP implementation (UDP): reliable + unreliable channels, binary events, latency via heartbeat.
- `InputSyncerClient` — Main entry point: wraps a driver, step batching, mock mode, match context, finish callbacks.
- `InputSyncerState` — Tracks received steps, detects missed steps, triggers resync (`request-all-steps`).
- `BaseInputData` — Abstract base class for custom input types (JSON-shaped on the wire).

**SyncSimulation** — Optional ECS layer (`Assets/SyncSimulation/`) on top of `InputSyncerClient`.

- `SyncSimulationHost` — Dedicated `Unity.Entities` world, lockstep ingest, optional prediction, rollback snapshots, manual `Tick()`.
- `InputTimeline` — Merges authoritative steps with predicted local input.
- `RollbackSnapshotStore` — Ring buffer of blittable component snapshots; culls mispredicted spawns via `SpawnedOnStep`.
- `StepInputHash` — Deterministic-enough hashing of local-player inputs for misprediction checks.

**UnityInputSyncerUTPServer** — UTP server components.

- `InputSyncerServer` — Core server logic: players, step loop, batching, broadcast, match access handshake, finish reasons.
- `DedicatedServerBootstrap` — Single-instance server from a scene.
- `InputSyncerServerPool` / `MultiInstanceServerBootstrap` — Pool + `AdminHttpServer`.
- `AdminController` — REST handler shared contract with NestJS admin routes.
- `ServerInstance` — Wraps a server with lifecycle state (`Idle` → `WaitingForPlayers` → `InMatch` → `Finished`).

**UnityInputSyncerSocketIOServer** — Reference NestJS + Socket.IO server (`package.json` at repo path above).

- `MatchGateway` — WebSocket gateway on path `/match-gateway`; routes sockets to pool instances by `matchId` query.
- `InputSyncerPoolService` — Instance pool (mirrors UTP pool semantics).
- `AdminController` (`/api/...`) — Same REST surface as Unity admin (Bearer auth optional).

### Data Flow (Step by Step)

1. Game code calls `InputSyncerClient.SendInput(BaseInputData)` — the driver emits an `"input"` event to the server.
2. The server collects incoming inputs into a pending queue.
3. At each step interval (default 100ms), the server batches pending inputs into a numbered step and broadcasts `on-steps` to all joined clients.
4. `InputSyncerState` on each client collects the step. If a step is missed, it automatically sends `request-all-steps` for a full resync (`on-all-steps`).
5. Game code reads inputs each `FixedUpdate` via `GetState().HasStep(n)` and `GetState().GetInputsForStep(n)`.

### Transport Comparison

| Feature               | Socket.IO (`SocketIODriver`)           | UTP (`UTPClientDriver`)                |
| --------------------- | -------------------------------------- | -------------------------------------- |
| Protocol              | TCP / WebSocket                        | UDP (Unity Transport)                  |
| Gateway / path        | Fixed: `/match-gateway`                | N/A (binary wire protocol)             |
| Channels              | Reliable only                          | Reliable + Unreliable                  |
| Binary events         | Not supported                          | Supported (`INativeArraySerializable`) |
| Latency measurement   | Not supported (`LatencyMs` returns -1) | Supported via heartbeat ping/pong      |
| Server implementation | NestJS in repo or your own             | C# / Unity dedicated server            |

### Key Dependencies

| Package                           | Version | Purpose                             |
| --------------------------------- | ------- | ----------------------------------- |
| `com.unity.transport`             | 2.6.0   | UDP networking for UTP transport    |
| `com.itisnajim.socketiounity`     | —       | WebSocket transport for Socket.IO   |
| `com.unity.nuget.newtonsoft-json` | 3.2.2   | JSON serialization                  |
| `com.unity.entities`              | 1.4.5+  | SyncSimulation ECS world            |
| NuGetForUnity                     | —       | Additional NuGet package management |

---

## Wire protocol & events

Event names are defined in `InputSyncerEvents` (C#) and mirrored in `input-syncer-events.ts` (NestJS).

### Server → client

| Event | Purpose |
|-------|---------|
| `on-steps` | Batch of `{ step, inputs[] }` for lockstep consumption. |
| `on-all-steps` | Full resync: `{ steps[], lastSentStep, requestedUser }`. |
| `on-start` | Match started (lobby → running); also used internally to surface `OnMatchStarted`. |
| `on-match-context` | After join: `{ matchId, matchData, users }` from admin create payload. |
| `on-finish` | Match ended: JSON with `reason` (see `InputSyncerFinishReasons`). |
| `on-user-finish` | Legacy quorum signal when another player calls `user-finish`. |
| `on-player-session-finish` | Per-player session end: `{ userId, data }`. |
| `content-error` | Fatal setup errors (bad `matchId`, access denied, instance destroyed). |

### Client → server

| Event | Purpose |
|-------|---------|
| `join` | Associate connection with `userId` (and server player list). |
| `input` | Gameplay input; body includes serialized `inputData`. |
| `request-all-steps` | Client missed a step; requests history. |
| `user-finish` | Legacy: signal “this player is done” for quorum match end. |
| `player-session-finish` | Independent per-player finish with optional JSON `data` (size-limited on server). |

---

## Socket.IO server (NestJS)

The folder `Assets/UnityInputSyncerSocketIOServer/` contains a **reference** multi-match server using Socket.IO 4.x and NestJS 11. It is intended to match the **behavior and admin API** of the Unity UTP pool so you can prototype or ship without a Unity headless build.

### Install and run

```bash
cd Assets/UnityInputSyncerSocketIOServer
npm ci
npm run build
npm run start:prod
```

- **Dev (watch):** `npm run start:dev`
- **Listen port:** `INPUT_SYNCER_PORT` (default `3000`)
- **Bind address:** optional `INPUT_SYNCER_BIND` (e.g. `0.0.0.0` or `127.0.0.1`)

On boot, HTTP (including admin) and WebSocket share that port. Logs also mention:

- Admin API: `http://localhost:<port>/api`
- WebSocket path: `/match-gateway`

### Unity editor integration

Use **Socket.IO Server** in the Unity Editor (see `Assets/Editor/SocketIOServerWindow.cs`) to build/start the Nest app, optionally enable **multi-core cluster** mode, and inspect pool/instance admin responses without leaving the editor.

### Environment variables (NestJS `AppModule`)

Pool and defaults are driven by the same conceptual flags as the UTP multi-instance server:

| Variable | Description |
|----------|-------------|
| `INPUT_SYNCER_PORT` | HTTP + Socket.IO listen port (default `3000`). |
| `INPUT_SYNCER_BIND` | Optional bind address. |
| `INPUT_SYNCER_MAX_INSTANCES` | Max concurrent match instances (default `10`). |
| `INPUT_SYNCER_AUTO_RECYCLE` | Auto-remove finished instances (`true` / `1`). |
| `INPUT_SYNCER_IDLE_TIMEOUT` | Seconds before destroying idle instances (`0` = off). |
| `INPUT_SYNCER_MAX_INSTANCE_LIFETIME` | Hard cap on instance age in seconds (`0` = off). |
| `INPUT_SYNCER_PUBLIC_CLIENT_SOCKET_IO_URL` | Public base URL (scheme + host + port, **no path**) embedded in admin `serverUrl` / `clientConnection.socketIoUrl` for real deployments (e.g. `https://game.example.com`). |
| `INPUT_SYNCER_ADMIN_REQUIRE_MATCH_USER_DATA` | If `true`, `POST /api/instances` must include non-empty `matchData` and/or `users`. |
| `INPUT_SYNCER_MAX_PLAYERS` | Default max players per instance. |
| `INPUT_SYNCER_AUTO_START_WHEN_FULL` | Start match when lobby full. |
| `INPUT_SYNCER_STEP_INTERVAL` | Step interval in seconds. |
| `INPUT_SYNCER_ALLOW_LATE_JOIN` | Allow joins after match start. |
| `INPUT_SYNCER_SEND_HISTORY_ON_LATE_JOIN` | Send step history to late joiners. |
| `INPUT_SYNCER_QUORUM_USER_FINISH_ENDS_MATCH` | Require all players to emit `user-finish` before match end when using that flow. |
| `INPUT_SYNCER_ABANDON_MATCH_TIMEOUT` | If set, can end matches stuck waiting for players (see server implementation). |
| `INPUT_SYNCER_ADMIN_AUTH_TOKEN` | If non-empty, admin routes require `Authorization: Bearer <token>`. |
| `INPUT_SYNCER_REWARD_OUTCOME_DELIVERY` | `0` = client-to-admin default; `1` / `2` = server hook modes (see `RewardOutcomeDeliveryMode` in server code). |

`INPUT_SYNCER_EDITOR_LOG` can be set to a file path so Unity’s process launcher can tail logs across domain reloads.

### Multi-core cluster (one machine)

```bash
npm run build
npm run start:cluster
```

This runs `dist/cluster-primary.js`: a small primary accepts the **public** `INPUT_SYNCER_PORT`, spawns **worker** Nest processes on `127.0.0.1` at `INPUT_SYNCER_INTERNAL_PORT_BASE`, `BASE+1`, …, and proxies HTTP/WebSocket to the worker that owns a given `instanceId` / `matchId`.

| Variable | Description |
|----------|-------------|
| `INPUT_SYNCER_WORKER_COUNT` | Worker processes (default ≈ logical CPUs − 1). |
| `INPUT_SYNCER_INTERNAL_PORT_BASE` | First worker port; must be **greater than** `INPUT_SYNCER_PORT`. |
| `INPUT_SYNCER_INTERNAL_SECRET` | On workers, validates internal admin/meta routes (generated by primary when forking). |

If a worker crashes, its matches are lost; the primary clears routing and restarts workers after a delay.

### Unity client connection (Socket.IO)

`SocketIODriver` always uses path `/match-gateway` and WebSocket transport. Connection options come from **query string** entries in `SocketIODriverOptions.Payload`:

- **`matchId`** — Required. For pooled servers, use the instance `id` returned by `POST /api/instances`.
- **`userId`** — Strongly recommended. The gateway may treat this like UTP `AutoJoinOnConnect` and register the player on connect.
- **`matchPassword`** / **`matchToken`** — Required when the instance was created with `matchAccess` `password` or `token` (see [Match access](#match-access-open--password--token)).

`JwtToken` is sent as HTTP header `Authorization: Bearer …` during the Socket.IO handshake (for your own auth integration).

```csharp
var driverOptions = new SocketIODriverOptions
{
    Url = "http://localhost:3000",
    Payload = new Dictionary<string, string>
    {
        { "matchId", instanceIdFromAdmin },
        { "userId", localPlayerId },
    },
    JwtToken = optionalBearerToken,
};
```

Optional test hooks on `SocketIODriverOptions`: `FakeLatency`, `ConnectDelayMs`, `EmitMinDelayMs`, `EmitMaxDelayMs`, `JsonSerializerSettings`.

---

## Getting Started

### Prerequisites

- **Unity 6** (6000.3.0f1 or later)
- **Node.js 18+** (for the NestJS Socket.IO server only)
- Packages resolve via Unity Package Manager and NuGet (`Assets/packages.config`)

### Installation

1. Clone or copy the repository into your Unity project.
2. Open the project in Unity 6.
3. Reference assemblies: `UnityInputSyncerClient` for the SDK; `UnityInputSyncerUTPServer` for server components; `SyncSimulation` for ECS helpers.

### Quick Start: Mock Mode (No Server)

```csharp
var client = new InputSyncerClient(null, new InputSyncerClientOptions
{
    Mock = true,
    MockCurrentUserId = "player-1",
    StepIntervalMs = 100,
});

client.OnMatchStarted += () => Debug.Log("Mock match started");

await client.ConnectAsync();
client.JoinMatch("player-1");
```

### Quick Start: UTP Dedicated Server

1. Open `Assets/Scenes/DedicatedServerScene.unity` (contains `DedicatedServerBootstrap`).
2. Build: `make build-server` → `Builds/Server/`.
3. Connect:

```csharp
var driver = new UTPClientDriver(new UTPDriverOptions
{
    Ip = "127.0.0.1",
    Port = 7777,
});

var client = new InputSyncerClient(driver);
bool connected = await client.ConnectAsync();
client.JoinMatch("player-1");
```

### Quick Start: Socket.IO (NestJS)

1. Build and run the Nest server (`npm run start:prod` in `Assets/UnityInputSyncerSocketIOServer`).
2. Create an instance via admin API or use a known `matchId` if you run a single static config.
3. Use `SocketIODriver` with `Payload` containing at least `matchId` and `userId`.

### Build targets

```bash
make test               # Run all tests (edit mode + play mode)
make test-edit          # Edit mode tests only
make test-play          # Play mode tests only
make build-server       # Build single-instance UTP dedicated server
make build-multi-server # Build multi-instance UTP server (see note below)
```

**Multi-instance Unity build:** `BuildServer.cs` expects `Assets/Scenes/MultiInstanceServerScene.unity`. That scene is not always present in the tree; if `make build-multi-server` fails, create a scene with a single root object and `MultiInstanceServerBootstrap`, save it at that path, then rebuild.

### Environment variable configuration

Bool values accept `true`/`false` or `1`/`0`. Inspector values on bootstraps are overridden when env vars are set.

#### Single-instance server (`DedicatedServerBootstrap`)

| Env Var | Type | Default | Description |
| ------- | ---- | ------- | ----------- |
| `INPUT_SYNCER_PORT` | ushort | 7777 | Server listen port |
| `INPUT_SYNCER_MAX_PLAYERS` | int | 2 | Max connected players |
| `INPUT_SYNCER_AUTO_START_WHEN_FULL` | bool | true | Start match when lobby is full |
| `INPUT_SYNCER_AUTO_JOIN_ON_CONNECT` | bool | true | Apply join/handshake on connect (aligns with Socket.IO gateway) |
| `INPUT_SYNCER_STEP_INTERVAL` | float | 0.1 | Seconds between step broadcasts |
| `INPUT_SYNCER_ALLOW_LATE_JOIN` | bool | false | Allow joining after match start |
| `INPUT_SYNCER_SEND_HISTORY_ON_LATE_JOIN` | bool | true | Send step history to late joiners |
| `INPUT_SYNCER_HEARTBEAT_TIMEOUT` | float | 15 | Seconds before disconnecting idle clients |
| `INPUT_SYNCER_ABANDON_MATCH_TIMEOUT` | float | 0 | Optional timeout when waiting for players with late join (`0` = disabled) |
| `INPUT_SYNCER_QUORUM_USER_FINISH_ENDS_MATCH` | bool | true | Legacy `user-finish` quorum ends match |
| `INPUT_SYNCER_SESSION_FINISH_MAX_PAYLOAD_BYTES` | int | 4096 | Max UTF-8 bytes for `player-session-finish` data |
| `INPUT_SYNCER_SESSION_FINISH_BROADCAST` | bool | true | Broadcast `on-player-session-finish` to all players |
| `INPUT_SYNCER_REJECT_INPUT_AFTER_SESSION_FINISH` | bool | false | Drop gameplay input after a player finishes session |
| `INPUT_SYNCER_REWARD_OUTCOME_DELIVERY` | int | 0 | `0` default; `1` / `2` server hook modes |

#### Multi-instance server (`MultiInstanceServerBootstrap`)

All single-instance options above apply as **defaults** for new instances, plus:

| Env Var | Type | Default | Description |
| ------- | ---- | ------- | ----------- |
| `INPUT_SYNCER_BASE_PORT` | ushort | 7778 | First UDP port for instances |
| `INPUT_SYNCER_MAX_INSTANCES` | int | 10 | Maximum concurrent instances |
| `INPUT_SYNCER_AUTO_RECYCLE` | bool | true | Auto-destroy finished instances |
| `INPUT_SYNCER_ADMIN_PORT` | ushort | 8080 | Admin HTTP listen port |
| `INPUT_SYNCER_ADMIN_AUTH_TOKEN` | string | "" | Bearer token for admin API (empty = no auth) |
| `INPUT_SYNCER_IDLE_TIMEOUT` | float | 0 | Seconds before destroying idle instances (`0` = off) |
| `INPUT_SYNCER_MAX_INSTANCE_LIFETIME` | float | 0 | Hard lifetime cap per instance (`0` = off) |
| `INPUT_SYNCER_PUBLIC_HOST` | string | "" | Hostname/IP **without scheme**; fills `serverUrl` / `clientConnection.host` as `host:port` for UTP clients |
| `INPUT_SYNCER_ADMIN_REQUIRE_MATCH_USER_DATA` | bool | false | Require non-empty `matchData` and/or `users` on create |

### Running tests

```bash
make test
```

Or **Window > General > Test Runner** in the Unity Editor.

---

## How-To Examples

### Define a Custom Input Type

```csharp
using UnityInputSyncerClient;

public class MoveInput : BaseInputData
{
    public static string Type => "move";
    public override string type { get => Type; set { } }

    public MoveInput(MoveInputData data) : base(data) { }
}

public class MoveInputData
{
    public int dx;
    public int dy;
}
```

Send and parse (receiving side often sees `JObject`):

```csharp
client.SendInput(new MoveInput(new MoveInputData { dx = 1, dy = 0 }));

using Newtonsoft.Json.Linq;
var inputs = client.GetState().GetInputsForStep(step);
foreach (var rawInput in inputs)
{
    JObject input = JObject.FromObject(rawInput);
    string inputType = input.Value<string>("type");
    if (inputType == "move")
    {
        var data = input["data"] as JObject;
        int dx = data.Value<int>("dx");
        int dy = data.Value<int>("dy");
    }
}
```

### Connect with Socket.IO

```csharp
using UnityInputSyncerClient;
using UnityInputSyncerClient.Drivers;

var driverOptions = new SocketIODriverOptions
{
    Url = "http://localhost:3000",
    Payload = new Dictionary<string, string>
    {
        { "matchId", "instance-id-from-admin" },
        { "userId", "player-1" },
    },
    JwtToken = "",
};

var client = new InputSyncerClient(
    new SocketIODriver(driverOptions),
    new InputSyncerClientOptions { StepIntervalMs = 100 }
);

client.OnMatchStarted += () => Debug.Log("Match started");
client.OnMatchContext = ctx => { /* matchData / users */ };
client.OnMatchFinishedWithReason += reason => Debug.Log($"Finished: {reason}");
client.OnConnected += () => Debug.Log("Connected");
client.OnDisconnected += (reason) => Debug.Log($"Disconnected: {reason}");
client.OnError += (msg) => Debug.LogError($"Error: {msg}");

bool connected = await client.ConnectAsync();
if (connected)
    client.JoinMatch("player-1");
```

Listen for fatal setup errors from the gateway:

```csharp
using UnityInputSyncerCore;

client.RegisterOnCustomEvent(InputSyncerEvents.INPUT_SYNCER_CONTENT_ERROR, response =>
{
    var jo = client.Driver.GetData<Newtonsoft.Json.Linq.JObject>(response);
    Debug.LogWarning($"content-error: {jo?["reason"]} — {jo?["message"]}");
});
```

### Connect with UTP

Handshake payload is built from `UTPDriverOptions.Payload` (JSON). For password/token matches, include `matchPassword` or `matchToken` keys there in addition to `matchId` / `userId`.

```csharp
var driverOptions = new UTPDriverOptions
{
    Ip = "127.0.0.1",
    Port = 7778,
    Payload = new Dictionary<string, string>
    {
        { "matchId", "pool-instance-id" },
        { "userId", "player-1" },
    },
};

var client = new InputSyncerClient(
    new UTPClientDriver(driverOptions),
    new InputSyncerClientOptions { StepIntervalMs = 100 }
);

bool connected = await client.ConnectAsync();
if (connected)
    client.JoinMatch("player-1");
```

### Consume inputs in FixedUpdate

```csharp
public class GameSimulation : MonoBehaviour
{
    private InputSyncerClient client;
    private int currentStep = 0;

    void FixedUpdate()
    {
        var state = client.GetState();

        while (state.HasStep(currentStep))
        {
            var inputs = state.GetInputsForStep(currentStep);
            foreach (var rawInput in inputs)
            {
                JObject input = JObject.FromObject(rawInput);
                ProcessInput(input);
            }
            currentStep++;
        }
    }
}
```

### Set Up a Dedicated Server (code)

```csharp
using UnityInputSyncerUTPServer;

public class MyServer : MonoBehaviour
{
    private InputSyncerServer server;

    void Start()
    {
        var options = new InputSyncerServerOptions
        {
            Port = 7777,
            MaxPlayers = 2,
            AutoStartWhenFull = true,
            StepIntervalSeconds = 0.1f,
            AllowLateJoin = false,
        };

        server = new InputSyncerServer(options);
        server.OnPlayerJoined += player => Debug.Log($"Player joined: {player.UserId}");
        server.OnMatchStarted += () => Debug.Log("Match started!");
        server.OnStepBroadcast += (step, data) =>
            Debug.Log($"Step {step} broadcast with {data.inputs.Count} inputs");

        server.Start();
    }

    void OnDestroy() => server?.Dispose();
}
```

### Run server-side simulation hooks

See existing doc pattern: subscribe to `OnStepBroadcast` and call `SendJsonToAll` / per-player sends; clients use `RegisterOnCustomEvent`.

### Multi-instance pool (code)

```csharp
var poolOptions = new InputSyncerServerPoolOptions
{
    BasePort = 7778,
    MaxInstances = 10,
    AutoRecycleOnFinish = true,
    IdleTimeoutSeconds = 300,
    PublicHost = "game.example.com",
    DefaultServerOptions = new InputSyncerServerOptions
    {
        MaxPlayers = 2,
        AutoStartWhenFull = true,
        StepIntervalSeconds = 0.1f,
    },
};

var pool = new InputSyncerServerPool(poolOptions);
```

### Match context on the client (`on-match-context`)

```csharp
client.OnMatchContext = ctx =>
{
    string id = ctx.MatchId;
    JToken match = ctx.MatchData;
    JObject allUsers = ctx.Users;
};
```

Not fired in **mock** mode.

### Use Mock Mode for Offline Testing

Custom events are not supported in mock mode (`RegisterOnCustomEvent` logs a warning). `SendUserFinish` / `SendPlayerSessionFinish` are no-ops in mock mode.

---

## Admin HTTP API (workflows)

Both the **UTP** multi-instance server (`AdminHttpServer` on `INPUT_SYNCER_ADMIN_PORT`) and the **NestJS** server (same routes on `INPUT_SYNCER_PORT`) implement:

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/instances` | Create a new match instance |
| `GET` | `/api/instances` | List instances |
| `GET` | `/api/instances/{id}` | Instance details |
| `DELETE` | `/api/instances/{id}` | Destroy instance |
| `GET` | `/api/stats` | Pool statistics + optional per-instance summary |

### Authentication

If `INPUT_SYNCER_ADMIN_AUTH_TOKEN` is set (Unity) or non-empty in Nest `AppModule`, every request must include:

```http
Authorization: Bearer <token>
```

### Typical operator flow

1. **Provision** — `POST /api/instances` with optional overrides (see body fields below).
2. **Distribute** — Send each player the `clientConnection` object (or `serverUrl`) from the response so they configure `UTPClientDriver` or `SocketIODriver`.
3. **Observe** — Poll `GET /api/stats` or `GET /api/instances/{id}` for `state`, `playerCount`, `matchStarted`, `matchFinished`, `currentStep`.
4. **Tear down** — `DELETE /api/instances/{id}` when the match is done (or rely on auto-recycle / idle timeout).

### `POST /api/instances` body (optional fields)

All fields are optional; omitted fields keep pool/module defaults.

| Field | Description |
|-------|-------------|
| `maxPlayers` | ≥ 1 |
| `stepIntervalSeconds` | > 0 |
| `autoStartWhenFull` | bool |
| `allowLateJoin` | bool |
| `sendStepHistoryOnLateJoin` | bool |
| `matchAccess` | `"open"` \| `"password"` \| `"token"` |
| `matchPassword` | Required when `matchAccess` is `password` |
| `allowedMatchTokens` | Non-empty string array when `matchAccess` is `token` (max 64 tokens, 256 chars each) |
| `matchData` | Arbitrary JSON object stored on the instance; sent to clients in `on-match-context` |
| `users` | Map of `userId` → arbitrary JSON; merged into `on-match-context.users` |

**Size limits (validation):** `matchData` ≤ 65536 UTF-8 bytes; each `users` entry ≤ 16384 bytes; at most 64 user keys. If `INPUT_SYNCER_ADMIN_REQUIRE_MATCH_USER_DATA` is enabled, `matchData` and/or `users` must be non-empty.

### Responses

- **201** — Create success; body is instance descriptor.
- **200** — List, get, delete success, stats.
- **400** — Invalid JSON or validation errors (`details` array).
- **404** — Unknown instance or unknown route.
- **405** — Wrong HTTP method.
- **409** — Pool full or similar `InvalidOperationException` message from the pool.

**UTP** `clientConnection` example shape:

```json
{
  "transport": "utp",
  "matchId": "instance-uuid",
  "host": "game.example.com",
  "port": 7778,
  "socketIoUrl": null,
  "matchGatewayPath": null
}
```

**Socket.IO** shape uses `transport: "socket.io"`, shared listener `port`, `socketIoUrl` set from `INPUT_SYNCER_PUBLIC_CLIENT_SOCKET_IO_URL` or localhost default, and `matchGatewayPath: "/match-gateway"`.

**`GET /api/stats`** includes counts (`idleCount`, `waitingCount`, `inMatchCount`, `finishedCount`, `availableSlots`) and `resourceUsage`. Unity reports `managedMemoryBytes` / `workingSetBytes`; Node reports `heapUsedBytes` / `rssBytes`.

### Example: create and connect (curl + Unity)

```bash
curl -s -X POST http://localhost:8080/api/instances \
  -H "Authorization: Bearer your-token" \
  -H "Content-Type: application/json" \
  -d '{"maxPlayers":4,"matchData":{"map":"arena"},"users":{"p1":{"deck":[1,2,3]}}}'
```

Use returned `id` as `matchId` for Socket.IO query string or UTP handshake payload.

---

## Match access (open / password / token)

When creating an instance, `matchAccess` selects how clients prove they may join.

| Mode | Admin body | Socket.IO client | UTP client |
|------|------------|------------------|------------|
| `open` | Default | Only `matchId` (+ `userId`) in query | Handshake JSON from `Payload` |
| `password` | `matchPassword` | Query `matchPassword` | Handshake JSON field `matchPassword` |
| `token` | `allowedMatchTokens: ["..."]` | Query `matchToken` | Handshake JSON field `matchToken` |

The server compares secrets using a constant-time hash where applicable (see `match-access.ts` / `MatchAccessHandshake.cs`).

---

## Match finish & session APIs

### `on-finish` reasons (`InputSyncerFinishReasons`)

Common `reason` strings on `OnMatchFinishedWithReason`:

| Constant | Value |
|----------|--------|
| `Completed` | `completed` |
| `AllDisconnected` | `all_disconnected` |
| `InsufficientPlayers` | `insufficient_players` |
| `AbandonTimeout` | `abandon_timeout` |
| `MaxInstanceLifetime` | `max_instance_lifetime` |

### Client methods

- **`SendUserFinish()`** — Emits `user-finish`. When `QuorumUserFinishEndsMatch` is true, the match can end after all joined players signal.
- **`SendPlayerSessionFinish(object data)`** — Emits `player-session-finish` with optional payload; triggers `on-player-session-finish` (broadcast controlled by `SessionFinishBroadcast`). Payload size is capped by `SessionFinishMaxPayloadBytes`.
- **`OnPlayerSessionFinish`** — `(userId, data)` per event.

Configure related behavior via env vars / `InputSyncerServerOptions`: `QuorumUserFinishEndsMatch`, `AbandonMatchTimeoutSeconds`, `SessionFinishMaxPayloadBytes`, `SessionFinishBroadcast`, `RejectInputAfterSessionFinish`.

---

## SyncSimulation (ECS)

The **SyncSimulation** assembly builds a **simulation-only** ECS world on top of `InputSyncerState`. It does not drive GameObjects or rendering.

### Responsibilities

- **Lockstep ingest** — Advances when `InputSyncerState.HasStep(n)` allows.
- **Local prediction** — `MaxPredictionSteps > 0` simulates ahead; remote inputs are **carried** from the last authoritative step unless you replace that policy.
- **Continuous vs discrete local input** — `InputTimeline.SetContinuousLocalSample` vs `EnqueueDiscreteLocalForStep`.
- **Rollback** — Register blittable `IComponentData` with `RegisterRollbackComponent<T>()`. `CreateSimEntity()` assigns `RollbackEntityId` and `SpawnedOnStep`.
- **Misprediction checks** — `StepInputHash.ComputeForLocalUser` hashes local-player inputs for comparison after authoritative steps arrive.
- **Determinism** — Same ordered inputs per step yield the same outcome only if your systems and numerics are deterministic.

### Full input resync

When `InputSyncerState.AddAllStepInputs` runs, call `SyncSimulationHost.AfterFullInputResync()` so the timeline and rollback baseline stay aligned.

### Options (summary)

| Option | Role |
|--------|------|
| `LocalUserId` | Prediction and hashing identity |
| `MaxPredictionSteps` | `0` = strict lockstep only |
| `MaxRollbackSteps` | Snapshot ring depth |
| `MaxSimulateStepsPerTick` | Caps work per `Tick()` |

### JSON size limit

`JsonInputEventElement` uses `FixedString512Bytes`; longer UTF-8 payloads are truncated with a warning.

---

## Features

### Available

**Client SDK**

- Lockstep input sync with automatic step tracking and resync request (`request-all-steps`)
- Dual transport: Socket.IO and UTP
- Reliable + unreliable channels and binary events (UTP only)
- Mock mode
- Match context callback (`OnMatchContext` / `LastMatchContext`)
- Match and session finish callbacks (`OnMatchFinishedWithReason`, `OnPlayerSessionFinish`)
- `SendUserFinish` / `SendPlayerSessionFinish`
- Custom JSON events (`RegisterOnCustomEvent`), including `content-error` handling
- Connection lifecycle events
- `IDisposable` on `InputSyncerClient`

**UTP server**

- Dedicated server and multi-instance pool
- Auto-start, late join, step history, heartbeat disconnect
- Match access modes (open / password / token) on handshake
- Admin HTTP API and pool stats
- Match finish reasons (disconnect, abandon timeout, max lifetime, quorum, etc.)
- Configurable session-finish payload and broadcast behavior

**Socket.IO server (NestJS)**

- Multi-instance pool with admin API parity
- Optional multi-worker cluster primary
- Gateway routing by `matchId`; disconnect clients when instance destroyed

**SyncSimulation**

- Dedicated ECS world, manual `Tick()`, prediction, rollback, `StepInputHash`

**Tooling**

- Makefile targets for tests and server builds
- Unity Editor window for Socket.IO server lifecycle
- Assembly definitions for clean dependencies

### Planned

- **Server-side gameplay input validation** — Stronger size/schema checks on every `input` before broadcast
- **Client resync timeout** — Retry / fail if `on-all-steps` never arrives
- **Admin HTTP rate limiting** — Per-IP throttling
- **Driver capability flag** — e.g. `SupportsLatency` on `IClientDriver`

### Future features

- **JWT per match invitation** — Tokens returned from admin create, enforced on connect
- **Spectator mode** — Read-only step consumers, optional reaction channel, `MaxSpectators`

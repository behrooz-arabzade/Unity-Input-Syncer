# Unity Input Syncer

A Unity 6 library for deterministic multiplayer input synchronization using lockstep architecture.

## Table of Contents

- [Introduction](#introduction)
- [Architecture](#architecture)
- [Getting Started](#getting-started)
- [How-To Examples](#how-to-examples)
- [SyncSimulation (ECS)](#syncsimulation-ecs)
- [Features](#features)

---

## Introduction

Unity Input Syncer provides a client-side SDK and server components for synchronizing player inputs across clients in a deterministic lockstep simulation. Instead of syncing game state, it synchronizes _inputs_ — the server collects all player inputs each tick, batches them into numbered "steps," and broadcasts them to every client. Each client then replays the same inputs in the same order, producing identical simulation results.

### Key Highlights

- **Dual transport support** — Connect via Socket.IO (TCP/WebSocket) or Unity Transport Package (UDP) with a single unified API.
- **Dedicated server builds** — Ship headless servers from pre-configured Unity scenes, configurable via environment variables.
- **Multi-instance server pool** — Host multiple match instances on a single machine with automatic port allocation and lifecycle management.
- **Admin HTTP API** — Create, monitor, and destroy match instances via authenticated REST endpoints.
- **Mock mode** — Develop and test game logic offline without a server.
- **Server-side simulation** — Optionally run game simulation on the server for authoritative state.

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

### Namespace Breakdown

The codebase is organized into three main namespaces under `Assets/`:

**UnityInputSyncerCore** — Shared networking and utility layer.

- `UTPSocket/` — Custom socket abstraction over Unity Transport Package (connection lifecycle, handshake, heartbeat, binary wire protocol).
- `INativeArraySerializable` — Interface for binary serialization to/from `NativeArray<byte>`.
- `InputSyncerEvents` — String constants for all protocol event names.
- `Utils/PlayerLoopHook` — Injects tick callbacks into Unity's player loop without MonoBehaviour.
- `Utils/UnityThreadDispatcher` — Dispatches work back to the main thread from background tasks.

**UnityInputSyncerClient** — Client SDK consumed by game code.

- `IClientDriver` — Abstract base class defining the transport contract (connect, disconnect, emit, listen).
- `SocketIODriver` — Socket.IO implementation (WebSocket, TCP only, no binary events).
- `UTPClientDriver` — UTP implementation (UDP, supports reliable + unreliable channels and binary events).
- `InputSyncerClient` — Main entry point. Wraps a driver, manages input collection, supports mock mode.
- `InputSyncerState` — Tracks received steps, detects missed steps, triggers resync.
- `BaseInputData` — Abstract base class for custom input types.

**SyncSimulation** — Optional ECS layer on top of `InputSyncerClient` (`Assets/SyncSimulation/`).

- `SyncSimulationHost` — Dedicated `Unity.Entities` world, lockstep ingest from `InputSyncerState`, optional local prediction, rollback snapshots, manual `Tick()`.
- `InputTimeline` — Merges authoritative steps with predicted local input (continuous carry + discrete events per step).
- `RollbackSnapshotStore` — Ring buffer of blittable component snapshots keyed by completed step; culls mispredicted spawns via `SpawnedOnStep`.

**UnityInputSyncerUTPServer** — Server components for hosting matches.

- `InputSyncerServer` — Core server logic: player management, step tick loop, input batching, broadcasting.
- `DedicatedServerBootstrap` — MonoBehaviour that starts a single-instance server from a Unity scene.
- `InputSyncerServerPool` — Manages multiple `InputSyncerServer` instances with port allocation and lifecycle.
- `MultiInstanceServerBootstrap` — MonoBehaviour that starts a pool + admin HTTP server.
- `AdminHttpServer` / `AdminController` — REST API for instance management.
- `ServerInstance` — Wraps a server with state tracking (`Idle → WaitingForPlayers → InMatch → Finished`).

### Data Flow (Step by Step)

1. Game code calls `InputSyncerClient.SendInput(BaseInputData)` — the driver emits an `"input"` event to the server.
2. The server collects all incoming inputs into a pending queue.
3. At each step interval (default 100ms), the server batches pending inputs into a numbered step and broadcasts it to all joined clients.
4. `InputSyncerState` on each client collects the step. If a step is missed, it automatically sends `"request-all-steps"` for a full resync.
5. Game code reads inputs each `FixedUpdate` via `GetState().HasStep(n)` and `GetState().GetInputsForStep(n)`.

### Transport Comparison

| Feature               | Socket.IO (`SocketIODriver`)           | UTP (`UTPClientDriver`)                |
| --------------------- | -------------------------------------- | -------------------------------------- |
| Protocol              | TCP / WebSocket                        | UDP (Unity Transport)                  |
| Channels              | Reliable only                          | Reliable + Unreliable                  |
| Binary events         | Not supported                          | Supported (`INativeArraySerializable`) |
| Latency measurement   | Not supported (`LatencyMs` returns -1) | Supported via heartbeat ping/pong      |
| Server implementation | External (any language)                | C# / Unity dedicated server            |

### Key Dependencies

| Package                           | Version | Purpose                             |
| --------------------------------- | ------- | ----------------------------------- |
| `com.unity.transport`             | 2.6.0   | UDP networking for UTP transport    |
| `com.itisnajim.socketiounity`     | —       | WebSocket transport for Socket.IO   |
| `com.unity.nuget.newtonsoft-json` | 3.2.2   | JSON serialization                  |
| NuGetForUnity                     | —       | Additional NuGet package management |

---

## Getting Started

### Prerequisites

- **Unity 6** (6000.3.0f1 or later)
- All package dependencies are resolved via Unity Package Manager and NuGet (see `Assets/packages.config`)

### Installation

1. Clone or copy the repository into your Unity project.
2. Open the project in Unity 6. Package dependencies will resolve automatically.
3. Assembly definitions are pre-configured — reference `UnityInputSyncerClient` from your game assemblies to access the client SDK, or `UnityInputSyncerUTPServer` for server components.

### Quick Start: Mock Mode (No Server)

The fastest way to test input syncing logic without any server:

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

In mock mode, inputs sent via `SendInput()` are collected locally and delivered as steps on the configured interval.

### Quick Start: UTP Dedicated Server

1. Open `Assets/Scenes/DedicatedServerScene.unity` — it contains a `DedicatedServerBootstrap` component that starts a UTP server on launch.
2. Build the server: `make build-server` (produces a headless macOS build in `Builds/Server/`).
3. Run the server build, then connect a client:

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

### Build Targets

```bash
make test               # Run all tests (edit mode + play mode)
make test-edit          # Edit mode tests only
make test-play          # Play mode tests only
make build-server       # Build single-instance dedicated server
make build-multi-server # Build multi-instance dedicated server
```

### Environment Variable Configuration

All server options can be set via the Unity Inspector or overridden with environment variables at runtime. Bool values accept `true`/`false` or `1`/`0`.

#### Single-Instance Server (`DedicatedServerBootstrap`)

| Env Var                                  | Type   | Default | Description                               |
| ---------------------------------------- | ------ | ------- | ----------------------------------------- |
| `INPUT_SYNCER_PORT`                      | ushort | 7777    | Server listen port                        |
| `INPUT_SYNCER_MAX_PLAYERS`               | int    | 2       | Max connected players                     |
| `INPUT_SYNCER_AUTO_START_WHEN_FULL`      | bool   | true    | Start match when lobby is full            |
| `INPUT_SYNCER_STEP_INTERVAL`             | float  | 0.1     | Seconds between step broadcasts           |
| `INPUT_SYNCER_ALLOW_LATE_JOIN`           | bool   | false   | Allow joining after match start           |
| `INPUT_SYNCER_SEND_HISTORY_ON_LATE_JOIN` | bool   | true    | Send step history to late joiners         |
| `INPUT_SYNCER_HEARTBEAT_TIMEOUT`         | float  | 15      | Seconds before disconnecting idle clients |

#### Multi-Instance Server (`MultiInstanceServerBootstrap`)

All of the above plus:

| Env Var                         | Type   | Default | Description                                             |
| ------------------------------- | ------ | ------- | ------------------------------------------------------- |
| `INPUT_SYNCER_BASE_PORT`        | ushort | 7778    | Starting port for instance allocation                   |
| `INPUT_SYNCER_MAX_INSTANCES`    | int    | 10      | Maximum concurrent match instances                      |
| `INPUT_SYNCER_AUTO_RECYCLE`     | bool   | true    | Auto-destroy finished instances                         |
| `INPUT_SYNCER_ADMIN_PORT`       | ushort | 8080    | Admin HTTP listen port                                  |
| `INPUT_SYNCER_ADMIN_AUTH_TOKEN` | string | ""      | Bearer token for admin API (empty = no auth)            |
| `INPUT_SYNCER_IDLE_TIMEOUT`     | float  | 0       | Seconds before destroying idle instances (0 = disabled) |

### Running Tests

Tests use Unity Test Framework (v1.6.0):

```bash
make test          # All tests
make test-edit     # Edit mode tests (Assets/Tests/EditMode/)
make test-play     # Play mode tests (Assets/Tests/PlayMode/)
```

Or use **Window > General > Test Runner** in the Unity Editor.

---

## How-To Examples

### Define a Custom Input Type

Extend `BaseInputData` with a unique `type` string:

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

Send it:

```csharp
client.SendInput(new MoveInput(new MoveInputData { dx = 1, dy = 0 }));
```

Read it back from a step (inputs arrive as `JObject` on the receiving side):

```csharp
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
        { "matchId", "my-match" },
        { "userId", "player-1" },
    },
    JwtToken = "your-auth-token",
};

var client = new InputSyncerClient(
    new SocketIODriver(driverOptions),
    new InputSyncerClientOptions { StepIntervalMs = 100 }
);

client.OnMatchStarted += () => Debug.Log("Match started");
client.OnConnected += () => Debug.Log("Connected");
client.OnDisconnected += (reason) => Debug.Log($"Disconnected: {reason}");
client.OnError += (msg) => Debug.LogError($"Error: {msg}");

bool connected = await client.ConnectAsync();
if (connected)
{
    client.JoinMatch("player-1");
}
```

### Connect with UTP

```csharp
using UnityInputSyncerClient;
using UnityInputSyncerClient.Drivers;

var driverOptions = new UTPDriverOptions
{
    Ip = "127.0.0.1",
    Port = 7777,
    Payload = new Dictionary<string, string>
    {
        { "matchId", "my-match" },
        { "userId", "player-1" },
    },
};

var client = new InputSyncerClient(
    new UTPClientDriver(driverOptions),
    new InputSyncerClientOptions { StepIntervalMs = 100 }
);

client.OnMatchStarted += () => Debug.Log("Match started");

bool connected = await client.ConnectAsync();
if (connected)
{
    client.JoinMatch("player-1");
}
```

### Consume Inputs in FixedUpdate

The standard pattern for reading synchronized inputs in your game loop:

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

    private void ProcessInput(JObject input)
    {
        string type = input.Value<string>("type");
        string userId = input.Value<string>("userId");
        // Handle each input type...
    }
}
```

### Set Up a Dedicated Server

**Option A: Use the pre-built scene.** Open `Assets/Scenes/DedicatedServerScene.unity`, configure the `DedicatedServerBootstrap` component in the Inspector, and build with `make build-server`. Override settings at runtime with environment variables.

**Option B: Create a server from code.**

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

    void OnDestroy()
    {
        server?.Dispose();
    }
}
```

### Run Server-Side Simulation

Attach a simulation component alongside `DedicatedServerBootstrap`. The server processes inputs each step and broadcasts authoritative game state via a custom event:

```csharp
using UnityInputSyncerUTPServer;
using UnityInputSyncerClient;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class MyServerSimulation : MonoBehaviour
{
    [SerializeField] private DedicatedServerBootstrap bootstrap;

    private Dictionary<string, Vector2Int> playerPositions = new();

    void Start()
    {
        var server = bootstrap.Server;

        server.OnPlayerJoined += player =>
        {
            playerPositions[player.UserId] = Vector2Int.zero;
        };

        server.OnPlayerDisconnected += player =>
        {
            if (player.UserId != null)
                playerPositions.Remove(player.UserId);
        };

        server.OnStepBroadcast += (step, stepInputs) =>
        {
            // Process inputs
            foreach (var rawInput in stepInputs.inputs)
            {
                JObject input = JObject.FromObject(rawInput);
                if (input.Value<string>("type") != "move") continue;

                string userId = input.Value<string>("userId");
                var data = input["data"] as JObject;
                int dx = data.Value<int>("dx");
                int dy = data.Value<int>("dy");

                if (playerPositions.ContainsKey(userId))
                {
                    var pos = playerPositions[userId];
                    playerPositions[userId] = new Vector2Int(pos.x + dx, pos.y + dy);
                }
            }

            // Broadcast authoritative state
            string json = JsonConvert.SerializeObject(new
            {
                step,
                players = playerPositions
            });
            server.SendJsonToAll("game-state", json);
        };
    }
}
```

On the client side, listen for the custom event:

```csharp
client.RegisterOnCustomEvent("game-state", response =>
{
    var state = client.Driver.GetData<MyGameState>(response);
    // Update visuals from authoritative state
});
```

### Multi-Instance Server with Admin API

The multi-instance server runs a pool of match instances managed through an HTTP admin API.

**Setup:** Use `Assets/Scenes/MultiInstanceServerScene.unity` with the `MultiInstanceServerBootstrap` component, or create programmatically:

```csharp
var poolOptions = new InputSyncerServerPoolOptions
{
    BasePort = 7778,
    MaxInstances = 10,
    AutoRecycleOnFinish = true,
    IdleTimeoutSeconds = 300,
    DefaultServerOptions = new InputSyncerServerOptions
    {
        MaxPlayers = 2,
        AutoStartWhenFull = true,
        StepIntervalSeconds = 0.1f,
    },
};

var pool = new InputSyncerServerPool(poolOptions);
```

**Admin API Endpoints:**

All endpoints require `Authorization: Bearer <token>` if `INPUT_SYNCER_ADMIN_AUTH_TOKEN` is set.

| Method   | Path                  | Description                 |
| -------- | --------------------- | --------------------------- |
| `POST`   | `/api/instances`      | Create a new match instance |
| `GET`    | `/api/instances`      | List all instances          |
| `GET`    | `/api/instances/{id}` | Get instance details        |
| `DELETE` | `/api/instances/{id}` | Destroy an instance         |
| `GET`    | `/api/stats`          | Get pool statistics         |

**Create instance (with optional overrides):**

```bash
curl -X POST http://localhost:8080/api/instances \
  -H "Authorization: Bearer your-token" \
  -H "Content-Type: application/json" \
  -d '{"maxPlayers": 4, "stepIntervalSeconds": 0.05}'
```

Response:

```json
{
  "id": "abc-123",
  "port": 7778,
  "state": "Idle",
  "createdAt": "2026-01-01T00:00:00Z"
}
```

**Instance lifecycle states:** `Idle` → `WaitingForPlayers` → `InMatch` → `Finished`

Clients connect to the instance using the returned port number.

### Use Mock Mode for Offline Testing

Mock mode runs a local step loop with no server connection — useful for developing and testing game logic in isolation:

```csharp
var client = new InputSyncerClient(null, new InputSyncerClientOptions
{
    Mock = true,
    MockCurrentUserId = "test-player",
    StepIntervalMs = 100,
});

await client.ConnectAsync();
client.JoinMatch("test-player");

// Inputs are collected and delivered as steps locally
client.SendInput(new MoveInput(new MoveInputData { dx = 1, dy = 0 }));

// Read them back in FixedUpdate as usual
var state = client.GetState();
if (state.HasStep(0))
{
    var inputs = state.GetInputsForStep(0);
    // Process inputs...
}
```

Note: Custom events via `RegisterOnCustomEvent` are not supported in mock mode.

---

## SyncSimulation (ECS)

The **SyncSimulation** assembly (`com.unity.entities` 1.4.5+) builds a **simulation-only** ECS world on top of `InputSyncerState`. It does **not** drive GameObjects or rendering; presentation reads state from your own code.

### Responsibilities

- **Lockstep ingest** — Advances the authoritative step cursor whenever `InputSyncerState.HasStep(n)` allows it.
- **Local prediction** — When `SyncSimulationOptions.MaxPredictionSteps > 0`, simulates ahead of the latest authoritative step. Remote players’ inputs are **carried** from the last authoritative step (repeat last frame’s payloads); document and tune this for your game.
- **Continuous vs discrete local input** — Call `InputTimeline.SetContinuousLocalSample` for held axes/buttons (re-applied each predicted step). Call `InputTimeline.EnqueueDiscreteLocalForStep(step, input)` for one-shot actions on an exact future step.
- **Rollback** — Registers blittable `IComponentData` types with `SyncSimulationHost.RegisterRollbackComponent<T>()`. Each completed step stores a snapshot; on local misprediction the host restores the snapshot after step `D - 1` and fast-forwards. Entities created via `SyncSimulationHost.CreateSimEntity()` get `RollbackEntityId` and `SpawnedOnStep`; predicted spawns with a spawn step after the restore point are removed during restore.
- **Determinism** — The framework only guarantees: *same ordered inputs per step ⇒ same simulation outcome* if your systems and numeric types are deterministic. Floating point, `UnityEngine.Random`, `Time`, Burst, and platform differences are your responsibility (fixed-point, integer math, seeded RNG, etc.).

### Per-step data for systems

Before each `SimulationGroup.Update()`, the host fills:

- `SimulationStepState` on the singleton entity (`SimulationSingletonTag`): `CurrentStep`, `SimulationPhase` (`Authoritative` vs `Predicted`).
- `DynamicBuffer<JsonInputEventElement>` — one element per merged input, JSON text (same shapes as lockstep wire data). Parse with Newtonsoft or your own decoder inside systems (keep parsing on the main thread if you use managed APIs).

Register your game systems with `host.AddSystemToSimulation(host.World.CreateSystemManaged<MySystem>())`, ordered after the built-in `SimulationInputBridgeSystem` (use `[UpdateAfter(typeof(SimulationInputBridgeSystem))]`).

### Full input resync

When `InputSyncerState.AddAllStepInputs` runs (missed-step recovery), call `SyncSimulationHost.AfterFullInputResync()` so the timeline’s authoritative cursor and rollback baseline stay aligned. Then recreate or re-register simulation entities as your game requires.

### Options (summary)

| Option | Role |
|--------|------|
| `LocalUserId` | Used for prediction and misprediction hashing. |
| `MaxPredictionSteps` | `0` = strict lockstep only. |
| `MaxRollbackSteps` | Ring buffer depth; must cover worst-case prediction depth. |
| `MaxSimulateStepsPerTick` | Caps work per `Tick()` call (replay may span multiple frames). |

### JSON size limit

Each `JsonInputEventElement` uses `FixedString512Bytes`. Payloads longer than 512 UTF-8 bytes are truncated with a console warning.

---

## Features

### Available

**Client SDK**

- Lockstep input synchronization with automatic step tracking
- Dual transport: Socket.IO (TCP/WebSocket) and UTP (UDP)
- Reliable and unreliable channel support (UTP)
- Binary event support for high-performance data (UTP, via `INativeArraySerializable`)
- JSON event support on both transports
- Mock mode for offline development and testing
- Automatic resync on missed steps (`request-all-steps`)
- Client latency measurement (UTP)
- Connection lifecycle events (`OnConnected`, `OnDisconnected`, `OnReconnected`, `OnError`)
- Match start detection (`OnMatchStarted`)
- Custom event registration (`RegisterOnCustomEvent`)
- `IDisposable` support for clean resource management

**Server**

- UTP-based dedicated server with configurable options
- Automatic match start when lobby is full
- Late join support with step history replay
- Player connection/disconnection tracking
- Step-based input batching and broadcasting
- Custom event sending to all players or individual players
- Server-side simulation support via `OnStepBroadcast` event
- Match finish detection (all players finished)
- Heartbeat-based idle client disconnection

**Multi-Instance Server**

- Server instance pool with automatic port allocation
- Instance lifecycle management (`Idle → WaitingForPlayers → InMatch → Finished`)
- Auto-recycle finished instances
- Idle timeout for automatic instance cleanup
- Authenticated admin HTTP API (Bearer token)
- REST endpoints for creating, listing, inspecting, and destroying instances
- Pool statistics endpoint for monitoring

**SyncSimulation (ECS)**

- Dedicated Entities world with manual `Tick()`, optional prediction, rollback snapshots for registered components
- Input timeline merging authoritative lockstep data with local continuous/discrete samples
- `SpawnedOnStep` / `RollbackEntityId` for culling wrong predicted spawns

**Infrastructure**

- Environment variable overrides for all configuration
- Pre-built dedicated server scene (`DedicatedServerScene.unity`)
- Makefile targets for building and testing
- Assembly definitions for clean dependency management

### Planned

- **Server-side input validation** — Size limits and schema validation before broadcasting inputs.
- **Client resync timeout** — Configurable timeout and retry for `request-all-steps` to prevent indefinite hangs.
- **Match end on disconnect** — Auto-finish match when all players leave instead of waiting for idle timeout.
- **Admin HTTP rate limiting** — Per-IP request throttling on the admin endpoint.
- **Driver latency support flag** — `bool SupportsLatency` property on `IClientDriver` for runtime capability checks.

### Future Features

- **JWT-authenticated instance creation** — Admin provides user IDs and match data when creating an instance. The server generates per-user JWT tokens and returns them. Clients must present their JWT to connect, enabling a secure invitation-based matchmaking flow.
- **Spectator mode** — Spectators connect to a match and receive step broadcasts (inputs) without sending gameplay inputs. Not counted toward `MaxPlayers`. Optionally can send non-gameplay inputs (cheers, reactions). Configurable `MaxSpectators` with `INPUT_SYNCER_MAX_SPECTATORS` env var.

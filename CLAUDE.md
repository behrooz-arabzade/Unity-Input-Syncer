# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Unity Input Syncer is a Unity 6 (6000.3.0f1) library for deterministic multiplayer input synchronization. It provides a client-side SDK that collects player inputs, sends them to a server in lockstep "steps," and replays received inputs in order. This enables lockstep deterministic simulation for multiplayer games.

## Requirements

1. **Input syncing** — Synchronize inputs between all connected clients.
2. **Server-side collection & broadcast** — A server collects all client inputs and broadcasts them to every connected client.
3. **Dual server transport** — The server can be either:

- A **Socket.IO** server (external, any language), or
- A **UTP server** written in C# using Unity Transport Package.

4. **Client SDK setup** — The client SDK allows developers to configure and connect to either server type.
5. **UTP dedicated server builds** — When using the UTP server, the developer builds a dedicated server from a Unity scene containing only server components (no game logic needed).
6. **Ready-made server scene** — A pre-configured server scene should be provided that can be built as-is or configured via environment variables, making dedicated server builds easy.
7. **Optional server-side simulation** — If the developer wants the server to run game simulation, this should be possible with extra configuration and setup.
8. **Channel support per transport**:

- **Socket.IO** — TCP only, reliable channel only.
- **UTP** — Supports both reliable and unreliable channels (unreliable for lower-latency use cases).

9. **Multi-instance dedicated server** — A dedicated server scene that hosts multiple server (match) instances on different ports, using a pool pattern to manage instance lifecycle.
10. **Admin HTTP controller** — An HTTP server with authentication that allows admins to request new match instances and manage the server pool.
11. **Server monitoring** — A monitoring HTTP endpoint exposing the number of active server instances and resource usage statistics.

## Architecture

The codebase is split into two namespaces under `Assets/`:

### UnityInputSyncerCore

Shared networking and utility layer:

- **UTPSocket/** — A custom socket abstraction built on Unity Transport Package (UTP). `UTPSocketServer` and `UTPSocketClient` handle connection lifecycle, handshake validation, heartbeat keep-alive, and a custom wire protocol supporting JSON events (string-keyed), binary events (int-keyed), handshake, and heartbeat ping/pong. Both tick via `PlayerLoopHook` (injected into Unity's player loop at `Update`), not MonoBehaviour.
- **INativeArraySerializable** — Interface for types that serialize to/from `NativeArray<byte>`, used by the binary event path.
- **InputSyncerEvents** — Constants for all event names in the input syncer protocol (e.g., `on-steps`, `input`, `join`).
- **Utils/PlayerLoopHook** — Injects callbacks into Unity's `PlayerLoop` without requiring a MonoBehaviour.
- **Utils/UnityThreadDispatcher** — Singleton MonoBehaviour for dispatching work back to the main thread from async/background tasks.

### UnityInputSyncerClient

Client SDK for game code to consume:

- **IClientDriver** — Abstract base class (not interface despite the name) defining the transport contract: connect, disconnect, emit (JSON or binary), listen for events. Two implementations exist:
  - `SocketIODriver` — Uses Socket.IO (via `SocketIOUnity` package) for WebSocket-based transport. Does **not** support binary events.
  - `UTPClientDriver` — Uses the UTPSocket layer for UDP-based transport via Unity Transport. Supports both JSON and binary events.
- **InputSyncerClient** — Main entry point. Wraps a driver, manages step-based input collection, supports a **mock mode** (local-only tick loop, no server needed) for offline testing. Fires `OnMatchStarted` once steps begin arriving.
- **InputSyncerState** — Tracks received steps as an ordered dictionary. Detects missed steps and triggers a full resync (`request-all-steps`). Consumers call `HasStep(n)` / `GetInputsForStep(n)` to consume inputs deterministically.
- **BaseInputData** — Abstract base for all input types. Uses Newtonsoft.Json (`JObject`) for serialization. Subclass it with a unique `type` string to define game-specific inputs.
- **Examples/** — `SocketIODriverExample` and `UTPSocketDriverExample` show how to wire up each driver in a MonoBehaviour.

### Data Flow

1. Game code creates `InputSyncerClient` with a driver and calls `ConnectAsync()` / `JoinMatch()`
2. Player inputs are sent via `SendInput(BaseInputData)` → driver emits `"input"` event
3. Server batches inputs into steps and broadcasts them back
4. `InputSyncerState` collects steps; game reads them with `GetInputsForStep(step)` each `FixedUpdate`
5. If a step is missed, the client automatically requests all steps for resync

## Key Dependencies

- **Unity Transport** (`com.unity.transport` 2.6.0) — UDP networking for UTP driver
- **SocketIOUnity** (`com.itisnajim.socketiounity`) — WebSocket transport for Socket.IO driver
- **Newtonsoft.Json** (`com.unity.nuget.newtonsoft-json` 3.2.2) — JSON serialization throughout
- **NuGetForUnity** — Manages additional NuGet packages via `Assets/NuGet.config` and `Assets/packages.config`

## Build & Development

This is a Unity project — open in Unity 6 (6000.3.0f1+). There is no standalone build system or CLI test runner.

Assembly definitions (`.asmdef`) are used for each source folder and for tests:

- `Assets/UnityInputSyncerCore/UnityInputSyncerCore.asmdef`
- `Assets/UnityInputSyncerClient/UnityInputSyncerClient.asmdef`
- `Assets/UnityInputSyncerUTPServer/UnityInputSyncerUTPServer.asmdef`
- `Assets/Tests/EditMode/EditModeTests.asmdef` — Edit Mode tests (editor only)
- `Assets/Tests/PlayMode/PlayModeTests.asmdef` — Play Mode tests

## Testing

Tests use Unity Test Framework (v1.6.0). Run from terminal via `make`:

```bash
make test          # run all tests (edit mode + play mode)
make test-edit     # edit mode tests only
make test-play     # play mode tests only
```

Or via **Window > General > Test Runner** in the Unity Editor.

- **Edit Mode tests** (`Assets/Tests/EditMode/`) — Pure logic tests that run in the editor without entering Play Mode.
- **Play Mode tests** (`Assets/Tests/PlayMode/`) — Tests that run inside Play Mode, useful for async/coroutine-based code.

## Dedicated Server

A ready-made server scene is at `Assets/Scenes/DedicatedServerScene.unity`. It contains a single GameObject with `DedicatedServerBootstrap`, which starts `InputSyncerServer` automatically.

**Building:** `make build-server` produces a headless macOS server build in `Builds/Server/`.

**Configuration:** All options can be set via Inspector or overridden with environment variables at runtime:

| Env Var                                  | Type   | Default | Description                               |
| ---------------------------------------- | ------ | ------- | ----------------------------------------- |
| `INPUT_SYNCER_PORT`                      | ushort | 7777    | Server listen port                        |
| `INPUT_SYNCER_MAX_PLAYERS`               | int    | 2       | Max connected players                     |
| `INPUT_SYNCER_AUTO_START_WHEN_FULL`      | bool   | true    | Start match when lobby is full            |
| `INPUT_SYNCER_STEP_INTERVAL`             | float  | 0.1     | Seconds between step broadcasts           |
| `INPUT_SYNCER_ALLOW_LATE_JOIN`           | bool   | false   | Allow joining after match start           |
| `INPUT_SYNCER_SEND_HISTORY_ON_LATE_JOIN` | bool   | true    | Send step history to late joiners         |
| `INPUT_SYNCER_HEARTBEAT_TIMEOUT`         | float  | 15      | Seconds before disconnecting idle clients |
| `INPUT_SYNCER_ADMIN_PORT`                | ushort | 8080    | Admin HTTP listen port                    |
| `INPUT_SYNCER_ADMIN_AUTH_TOKEN`          | string | ""      | Bearer token (empty = no auth)            |

Bool values accept `true`/`false` or `1`/`0`. The `Server` property on `DedicatedServerBootstrap` is public for server-side simulation access.

## Conventions

- Transport-layer ticking uses `PlayerLoopHook` (injected into Unity's player loop), not MonoBehaviour `Update()`. Only use MonoBehaviour for scene-bound components.
- `IClientDriver` is an abstract class, not a C# interface. New transport implementations should extend it.
- The wire protocol for UTP is a custom binary format: `[1-byte type][variable header][length-prefixed payload]`. JSON events encode event name + JSON body; binary events encode int event ID + raw bytes.
- Mock mode (`InputSyncerClientOptions.Mock = true`) runs a local tick loop for development without a server.

## Remaining Steps

Tracked steps to complete the project. Update this list as requirements are added or completed.

- [x] **Step 1: Create a Ready-Made Dedicated Server Scene** — `Assets/Scenes/DedicatedServerScene.unity` with `DedicatedServerBootstrap` component. _(Requirement #6)_
- [x] **Step 2: Add Environment Variable Configuration for the Server** — `DedicatedServerBootstrap` reads `INPUT_SYNCER_*` env vars in `Awake()` to override Inspector defaults. _(Requirement #6)_
- [x] **Step 3: Implement Binary Data Deserialization on the Client** — `UTPClientDriver.GetData<T>(NativeArray<byte>)` throws `NotImplementedException`. Binary events can be sent but not received/deserialized. Need to implement `INativeArraySerializable` deserialization path. _(Requirement #8)_
- [x] **Step 4: Implement Socket.IO Binary Event Support (or document limitation)** — Socket.IO binary events are intentionally unsupported; methods throw `NotSupportedException` with clear messages. _(Requirement #8)_
- [x] **Step 5: Add Server-Side Simulation Example** — `ServerSimulationExample` (server-side) and `ServerSimulationClientExample` (client-side) demonstrate authoritative server simulation with shared `MoveInput`/`SimulationGameState` data contracts. _(Requirement #7)_
- [x] **Step 6: Fill Test Gaps** — Added 17 tests: reconnection flows (5 state + 2 integration), mock mode edge cases (3 edit + 2 play), binary deserialization (3), env var config (2).
- [x] **Step 7: Multi-Instance Server Pool** — Create a server instance pool that manages multiple `InputSyncerServer` instances, each on a different port. The pool should handle instance lifecycle (create, destroy, recycle) and track instance state (idle, in-match, full). _(Requirement #9)_
- [x] **Step 8: Admin HTTP Controller with Auth** — Embedded HTTP server with token-based auth (`AdminHttpServer` + `AdminController`). Endpoints: POST/GET/DELETE `/api/instances`, GET `/api/stats`. Auth via `Authorization: Bearer <token>` header. `AdminPoolOperations` bridges HTTP thread to main thread via `UnityThreadDispatcher`. _(Requirement #10)_
- [x] **Step 9: Monitoring Endpoint** — Enhanced `GET /api/stats` to include per-instance details (`currentStep`, `uptimeSeconds`) and process-level resource usage (`managedMemoryBytes`, `workingSetBytes`, `processorCount`). Null fields omitted for backward compatibility. _(Requirement #11)_
- [x] **Step 10: Multi-Instance Dedicated Server Scene** — `MultiInstanceServerBootstrap` MonoBehaviour wires together `InputSyncerServerPool`, `AdminPoolOperations`, `AdminController`, and `AdminHttpServer`. Configurable via 11 environment variables (base port, max instances, admin port, auth token, and per-instance server defaults). Scene `MultiInstanceServerScene.unity` must be created manually in Unity Editor. _(Requirements #9, #10, #11)_

### Bug Fixes & Hardening

- [x] **Step 11: Fix Mock Mode Timing Bug** — Replaced `DateTime.UtcNow.Millisecond` with `System.Diagnostics.Stopwatch` in `RunMockInterval()` for monotonic elapsed-time measurement. **Files:** `InputSyncerClient.cs`.
- [x] **Step 12: Remove Reflection from `GetInputsForStep`** — Replaced reflection-based `GetType().GetProperty("index")` with direct type checks: `JObject` indexer and `BaseInputData.index` field access. **Files:** `InputSyncerState.cs`.
- [x] **Step 13: Add Client Disconnect & Implement `IDisposable`** — Added `DisconnectAsync()`, updated `Dispose()` to disconnect the driver, declared `: IDisposable` on `InputSyncerClient`. **Files:** `InputSyncerClient.cs`.
- [x] **Step 14: Enforce Max Player Limit on Server** — Added `MaxPlayers` check in the `"join"` protocol handler. Rejects with `content-error` event (reason: `"match-full"`) without disconnecting the client. **Files:** `InputSyncerServer.cs`.
- [x] **Step 15: Add Client Connection State Events** — Added `OnConnected`, `OnReconnected`, `OnError`, `OnDisconnected` properties on `InputSyncerClient`, wired from `IClientDriver` delegate fields in constructor. **Files:** `InputSyncerClient.cs`.
- [ ] **Step 16: Guard Port Overflow in Server Pool** — `InputSyncerServerPool.AllocatePort()` increments `nextSequentialPort` (a `ushort`) without bounds checking. After enough allocations it silently wraps to 0, which is an invalid port. **Fix:** Add an overflow check — throw `InvalidOperationException` if `nextSequentialPort` would exceed `ushort.MaxValue` and no recycled ports are available. **Files:** `InputSyncerServerPool.cs:142–155`.

### Server Hardening

- [ ] **Step 17: Add HTTP Body Size Limit** — `AdminHttpServer.HandleContext()` reads the entire request body via `StreamReader.ReadToEndAsync()` with no size cap. A single large request could exhaust server memory. **Fix:** Wrap the input stream in a length-limited reader (e.g., read into a buffer capped at 1 MB) and return `413 Payload Too Large` if exceeded. **Files:** `AdminHttpServer.cs:85–110`.
- [ ] **Step 18: Validate Admin Instance Creation Parameters** — `AdminCreateInstanceRequest` fields (`maxPlayers`, `stepIntervalSeconds`, `heartbeatTimeout`, etc.) are passed through without range validation. Negative or zero values can create broken instances. **Fix:** In `AdminController` or `AdminPoolOperations`, validate that `maxPlayers >= 1`, `stepIntervalSeconds > 0`, `heartbeatTimeout > 0` before creating the instance. Return `400 Bad Request` with specific error messages for invalid fields. **Files:** `AdminController.cs`, `AdminPoolOperations.cs`.
- [ ] **Step 19: Add Idle Instance Timeout & Graceful Match Cleanup** — When a match finishes, the server broadcasts the finish event but doesn't disconnect players or schedule cleanup. Idle instances (no players, no activity) can linger indefinitely, leaking resources. **Fix:** Add a configurable `IdleTimeoutSeconds` to `InputSyncerServerPoolOptions`. In the pool's tick or a periodic check, destroy instances that have been in `Finished` or `Idle` state longer than the timeout. Optionally disconnect remaining players on match finish after a grace period. **Files:** `InputSyncerServerPool.cs`, `InputSyncerServerPoolOptions.cs`, `ServerInstance.cs`.

### Client Features

- [ ] **Step 20: Add Client Latency / Ping Measurement** — There is no way for game code to measure round-trip time to the server. This is needed for connection quality indicators, prediction tuning, and lag compensation. **Fix:** Leverage the existing heartbeat ping/pong in `UTPSocketClient` — measure the time between sending a ping and receiving the pong, expose as `Latency` (or `RttMs`) on `IClientDriver` and surface it through `InputSyncerClient`. For `SocketIODriver`, use Socket.IO's built-in ping if available, or implement a custom ping event. **Files:** `IClientDriver.cs`, `UTPClientDriver.cs`, `SocketIODriver.cs`, `InputSyncerClient.cs`.

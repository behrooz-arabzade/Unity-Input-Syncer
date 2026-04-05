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

The **shippable library** is a Unity Package Manager package under `Packages/com.github.behrooz-arabzade.unity-input-syncer/` (embedded in this repo via `file:` in `Packages/manifest.json`). Tests and template content remain under `Assets/`.

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
- **Samples** — Driver examples and Tic Tac Toe ship as optional UPM samples under `Samples~/ClientExamples` and `Samples~/ServerExamples` (import via Package Manager).

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

**Consuming as a library:** Other Unity projects install `com.github.behrooz-arabzade.unity-input-syncer` from Git using `?path=/Packages/com.github.behrooz-arabzade.unity-input-syncer` and a `#tag` or commit SHA (see **Getting Started → Installation** in `DOCUMENTATION.md`). This repo uses `file:com.github.behrooz-arabzade.unity-input-syncer` in `Packages/manifest.json` for local development.

Assembly definitions (`.asmdef`) are used for each source folder and for tests:

- `Packages/com.github.behrooz-arabzade.unity-input-syncer/UnityInputSyncerCore/UnityInputSyncerCore.asmdef`
- `Packages/com.github.behrooz-arabzade.unity-input-syncer/UnityInputSyncerClient/UnityInputSyncerClient.asmdef`
- `Packages/com.github.behrooz-arabzade.unity-input-syncer/UnityInputSyncerUTPServer/UnityInputSyncerUTPServer.asmdef`
- `Packages/com.github.behrooz-arabzade.unity-input-syncer/SyncSimulation/SyncSimulation.asmdef`
- `Packages/com.github.behrooz-arabzade.unity-input-syncer/Editor/UnityInputSyncer.Editor.asmdef` — Editor tools (`BuildServer`, Socket.IO window)
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

A ready-made server scene is at `Packages/com.github.behrooz-arabzade.unity-input-syncer/Scenes/DedicatedServerScene.unity`. It contains a single GameObject with `DedicatedServerBootstrap`, which starts `InputSyncerServer` automatically.

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
| `INPUT_SYNCER_IDLE_TIMEOUT`              | float  | 0       | Seconds before destroying idle instances  |

Bool values accept `true`/`false` or `1`/`0`. The `Server` property on `DedicatedServerBootstrap` is public for server-side simulation access.

**Multi-instance pool (`MultiInstanceServerBootstrap`):** also supports `INPUT_SYNCER_BASE_PORT`, `INPUT_SYNCER_MAX_INSTANCES`, `INPUT_SYNCER_AUTO_RECYCLE`, `INPUT_SYNCER_MAX_INSTANCE_LIFETIME`, and other pool/server options documented in `DOCUMENTATION.md`. For admin API client hints: `INPUT_SYNCER_PUBLIC_HOST` (hostname/IP, no scheme; fills `serverUrl` / `clientConnection.host` as `host:port`). Optional: `INPUT_SYNCER_ADMIN_REQUIRE_MATCH_USER_DATA` — when true, `POST /api/instances` must include non-empty `matchData` and/or `users`.

## Socket.IO server (NestJS) — single process vs multi-core cluster

The TypeScript server under `Servers/UnityInputSyncerSocketIOServer/` (repository root) can run in two modes:

| Mode | Entry | Behavior |
|------|--------|----------|
| Single process | `node dist/main.js` / `npm run start:prod` | One Node process; all match instances share one CPU-bound event loop. |
| Multi-core (one machine) | `node dist/cluster-primary.js` / `npm run start:cluster` | A **primary** listens on `INPUT_SYNCER_PORT` and spawns **workers**; each worker is a full Nest app with its own in-memory pool on `127.0.0.1` at `INPUT_SYNCER_INTERNAL_PORT_BASE`, `BASE+1`, … New instances are assigned to the least-loaded worker; WebSocket and admin traffic for a given `instanceId` are routed to that worker. |

**Cluster environment variables:**

| Variable | Description |
|----------|-------------|
| `INPUT_SYNCER_WORKER_COUNT` | Number of workers (default `max(1, logical CPU count − 1)`). |
| `INPUT_SYNCER_INTERNAL_PORT_BASE` | First worker HTTP port (default `INPUT_SYNCER_PORT + 1`). Must be greater than the public port. |
| `INPUT_SYNCER_BIND` | Used by workers when forked (set to `127.0.0.1` by the primary). For `main.js` alone, optional bind address. |

Workers receive a generated `INPUT_SYNCER_INTERNAL_SECRET` for `GET /api/internal/pool-meta` and `GET /api/internal/instance/:id/exists` (not exposed through the primary’s public port). If a **worker process crashes**, its matches are lost; the primary clears routing for that worker and restarts it after a short delay. The Unity **Socket.IO Server** window can enable “Multi-core cluster” to launch `dist/cluster-primary.js` instead of `dist/main.js`.

**Admin client URL / match payloads:** set `INPUT_SYNCER_PUBLIC_CLIENT_SOCKET_IO_URL` (e.g. `https://game.example.com`) so create-instance responses include `serverUrl` and `clientConnection.socketIoUrl` for Unity clients. Optional `INPUT_SYNCER_ADMIN_REQUIRE_MATCH_USER_DATA` matches the Unity multi-instance server behavior.

## Conventions

- Transport-layer ticking uses `PlayerLoopHook` (injected into Unity's player loop), not MonoBehaviour `Update()`. Only use MonoBehaviour for scene-bound components.
- `IClientDriver` is an abstract class, not a C# interface. New transport implementations should extend it.
- The wire protocol for UTP is a custom binary format: `[1-byte type][variable header][length-prefixed payload]`. JSON events encode event name + JSON body; binary events encode int event ID + raw bytes.
- Mock mode (`InputSyncerClientOptions.Mock = true`) runs a local tick loop for development without a server.

## TODO

### Priority: Critical

- [ ] **Create `MultiInstanceServerScene.unity`** — The scene referenced by `BuildServer.cs` does not exist. Without it, `make build-multi-server` fails and the multi-instance server cannot be built. Must be created manually in Unity Editor with a single GameObject containing the `MultiInstanceServerBootstrap` component. **Files:** `Packages/com.github.behrooz-arabzade.unity-input-syncer/Scenes/MultiInstanceServerScene.unity`.

### Priority: High

- [ ] **Add Server-Side Input Validation** — The server broadcasts all received inputs without validating structure or size. A malicious client could send oversized or malformed input payloads that get broadcast to all clients. Add size limits and schema validation in `InputSyncerServer` before broadcasting. **Files:** `InputSyncerServer.cs`.
- [ ] **Add Resync Timeout on Client** — `InputSyncerState` requests all steps on a missed step (`request-all-steps`), but has no timeout or retry limit if the server never responds. The client could hang indefinitely. Add a configurable timeout with retry count and a failure callback. **Files:** `InputSyncerState.cs`, `InputSyncerClient.cs`.
- [ ] **Add Match End Mechanism on Server** — `InputSyncerServer` handles match start and step broadcasting but has no explicit match end. If all clients disconnect, the instance stays in `InMatch` state until idle timeout (if configured) cleans it up. Add explicit match finish detection when all players leave and transition state to `Finished`. **Files:** `InputSyncerServer.cs`, `ServerInstance.cs`.

### Priority: Medium

- [ ] **Log Silent Join Rejection** — `InputSyncerServer` returns silently when a player sends "join" but isn't in the Players dict. Add a `Debug.LogWarning` so this condition is visible in server logs rather than silently masking connection issues. **Files:** `InputSyncerServer.cs`.
- [ ] **Add Admin HTTP Rate Limiting** — The admin HTTP endpoint has authentication but no rate limiting. Repeated requests could stress the pool. Add a simple per-IP request rate limiter to `AdminHttpServer`. **Files:** `AdminHttpServer.cs`.

### Priority: Low

- [ ] **Remove Unused `SampleScene.unity`** — Leftover from the Unity project template, serves no purpose. **Files:** `Assets/Scenes/SampleScene.unity`.
- [ ] **Expose Latency Support Flag on Driver** — `LatencyMs` returns -1 for Socket.IO clients since it's only implemented for UTP. Consumers have no way to check at runtime whether latency measurement is supported without checking the driver type. Add a `bool SupportsLatency` property to `IClientDriver`. **Files:** `IClientDriver.cs`, `UTPClientDriver.cs`, `SocketIODriver.cs`, `InputSyncerClient.cs`.

### Future Features

- [ ] **JWT-Authenticated Instance Creation** — When an admin creates an instance via the Admin HTTP API, they provide a list of user IDs and optional match data in the request body. The server creates the instance, stores the user/match data, generates a JWT token per user, and returns the tokens in the response. Clients must present their JWT token when connecting to the instance for authentication. This replaces the current open-connection model with a secure, invitation-based flow. **Files:** `AdminController.cs`, `AdminHttpServer.cs`, `InputSyncerServer.cs`, `InputSyncerServerOptions.cs`, `ServerInstance.cs`, `UTPSocketServer.cs`.
- [ ] **Spectator Mode** — Allow spectator clients to connect to a match instance and receive step broadcasts (inputs) without being able to send gameplay inputs. Spectators are not counted toward `MaxPlayers`. Optionally, spectators can send non-gameplay inputs (e.g., cheers, reactions) on a separate channel that gets broadcast but doesn't affect simulation. Add configurable `MaxSpectators` (0 = disabled) to server options with a corresponding `INPUT_SYNCER_MAX_SPECTATORS` env var. Spectators connect with a distinct role flag and receive a filtered event stream. **Files:** `InputSyncerServer.cs`, `InputSyncerServerOptions.cs`, `DedicatedServerBootstrap.cs`, `InputSyncerClient.cs`, `InputSyncerClientOptions.cs`, `IClientDriver.cs`.

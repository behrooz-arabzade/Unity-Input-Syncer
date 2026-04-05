# Unity Input Syncer

**Deterministic lockstep input synchronization for Unity 6** — collect player inputs, batch them into numbered steps on a server, and replay the same ordered inputs on every client so simulations stay in sync without shipping full game state over the network.

[![Unity](https://img.shields.io/badge/Unity-6000.3+-black?logo=unity)](https://unity.com/releases)
[![.NET](https://img.shields.io/badge/C%23-Unity%20scripting-512BD4?logo=csharp)](https://docs.unity3d.com/Manual/scripting.html)

---

## Why this project?

| Approach       | What Unity Input Syncer does                                                                                             |
| -------------- | ------------------------------------------------------------------------------------------------------------------------ |
| **Lockstep**   | Syncs **inputs**, not transforms or physics state — clients agree on step _N_ and apply the same inputs.                 |
| **Transports** | **UDP** via Unity Transport (`UTPClientDriver`) or **WebSocket** via Socket.IO (`SocketIODriver`) behind one client API. |
| **Servers**    | **C#** headless dedicated server from a Unity scene, or a **NestJS** reference server with a matching admin API.         |
| **Dev UX**     | **Mock mode** for offline ticks; optional **ECS** helpers (`SyncSimulation`) for prediction and rollback.                |

---

## Quick install (other Unity projects)

Add this package to your project’s **`Packages/manifest.json`** dependencies. Pin a **Git tag** or **commit SHA** after `#` so upgrades are deliberate (match the `version` field in the package’s `package.json`).

```json
"com.github.behrooz-arabzade.unity-input-syncer": "https://github.com/behrooz-arabzade/Unity-Input-Syncer.git?path=/Packages/com.github.behrooz-arabzade.unity-input-syncer#1.0.0"
```

Unity will pull nested dependencies (Unity Transport, Newtonsoft JSON, Socket.IO Unity client, Entities for the ECS module).

**Optional samples** (driver examples, Tic Tac Toe, server snippets): **Window → Package Manager** → **Unity Input Syncer** → **Samples** → Import.

Full setup, APIs, admin HTTP workflows, and environment variables are in **[DOCUMENTATION.md](DOCUMENTATION.md)**.

---

## Repository layout

| Path                                                                                                                   | Purpose                                                                                                  |
| ---------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------- |
| [`Packages/com.github.behrooz-arabzade.unity-input-syncer/`](Packages/com.github.behrooz-arabzade.unity-input-syncer/) | UPM package: Core, Client, UTP server, SyncSimulation, editor tools, dedicated server scene, `Samples~`. |
| [`Servers/UnityInputSyncerSocketIOServer/`](Servers/UnityInputSyncerSocketIOServer/)                                   | Reference **NestJS + Socket.IO** server (`npm ci`, `npm run build`, `npm run start:prod`).               |
| [`Assets/`](Assets/)                                                                                                   | Template project assets, tests, and demos not shipped as part of the minimal package install.            |

This repo is both a **consumable library** (via UPM) and a **development sandbox** for maintainers.

---

## Requirements

- **Unity 6** — `6000.3.0f1` or newer recommended ([Unity downloads](https://unity.com/releases)).
- **Node.js 18+** — only if you run the NestJS Socket.IO server locally.

---

## Developing in this repository

Clone and open the project in Unity. The embedded package is wired with:

```json
"com.github.behrooz-arabzade.unity-input-syncer": "file:com.github.behrooz-arabzade.unity-input-syncer"
```

in [`Packages/manifest.json`](Packages/manifest.json).

### Tests and server builds (macOS)

```bash
make test              # Edit Mode + Play Mode (Unity Test Framework)
make test-edit
make test-play
make build-server      # Headless UTP dedicated server → Builds/Server/
```

Adjust `UNITY` in the [`Makefile`](Makefile) if your Editor lives outside the default Hub path.

---

## Documentation and AI context

- **[DOCUMENTATION.md](DOCUMENTATION.md)** — Architecture, wire protocol, admin API, match access, ECS layer, and getting started.
- **[CLAUDE.md](CLAUDE.md)** — High-level repo guidance for contributors and tooling.

---

## Contributing

Contributions are welcome: bug reports, docs improvements, and focused pull requests.

1. Open an issue for larger changes so design can be agreed early.
2. Keep diffs scoped to one concern; match existing code style and assembly layout.
3. Run **`make test`** (or the Test Runner in Unity) before submitting when your change touches runtime logic.

---

## Acknowledgments

- [Unity Transport](https://docs.unity3d.com/Packages/com.unity.transport@latest) — UDP networking for the UTP path.
- [SocketIOUnity](https://github.com/itisnajim/SocketIOUnity) — Socket.IO client for Unity.
- [Newtonsoft.Json](https://docs.unity3d.com/Packages/com.unity.nuget.newtonsoft-json@latest) — JSON used across the protocol and tooling.

---

<p align="center">
  <sub>Repository: <a href="https://github.com/behrooz-arabzade/Unity-Input-Syncer">github.com/behrooz-arabzade/Unity-Input-Syncer</a></sub>
</p>

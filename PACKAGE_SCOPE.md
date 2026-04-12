# Package scope — Unity Input Syncer

## In scope

- **Client drivers** (Socket.IO, UTP) and **`InputSyncerClient`**: connect, join match, send **opaque** player inputs, receive **numbered steps**.
- **Dedicated server** scenes and **admin HTTP** for pool lifecycle (UTP) and parity features on the **NestJS** reference server.
- **`SyncSimulation`**: dedicated **Entities `World`**, **rollback** snapshots, **prediction** options, **`JsonInputEventElement`** bridge from synced steps into simulation.

## Out of scope

- **Gameplay rules** (grid, combat, AI, loot, waves).
- **Nakama** (use Nakama for accounts, matchmaking, storage; feed **instance seed** / party metadata into your sim bootstrap yourself).

This package answers: *“same inputs, same step order, same tick”* — not *“what does a spell do on a tile”*.

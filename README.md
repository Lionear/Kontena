# Kontena

**One UI. Any engine.** A cross-platform desktop app to manage containers through a single,
fast, backend-agnostic interface — regardless of which runtime engine powers them underneath.

Docker and Podman at launch; Apple's native `container` (macOS 26) planned. Kubernetes / Swarm
orchestration comes later.

> Status: early scaffold. Built with **.NET 10** + **Avalonia** (MVVM).

## Why

Developers get locked into one container tool and its quirks. Kontena treats the backend as an
implementation detail: switch engines without switching apps or relearning anything. The engine
is chosen through a single **Container Engine Abstraction Layer (CEAL)** that every backend
adapter implements.

## Solution layout

```
Kontena.slnx
├─ src/
│  ├─ Kontena.Core             # Engine-neutral domain models & product identity (no UI)
│  ├─ Kontena.Engines          # CEAL contract, engine registry, providers (no UI)
│  ├─ Kontena.Sdk              # Extension SDK: IEnginePlugin + manifest (no UI)
│  ├─ Kontena.Adapters.Docker  # Docker + Podman adapter & providers      (no UI)
│  ├─ Kontena.Adapters.Podman  # (reserved) dedicated Podman adapter       (no UI)
│  └─ Kontena.App              # Avalonia desktop UI (MVVM)
└─ tests/
   ├─ Kontena.Core.Tests
   ├─ Kontena.Engines.Tests
   └─ Kontena.Adapters.Docker.Tests   # integration (skips without Docker)
```

Core / Engines / Adapters carry **no** Avalonia reference — the whole abstraction layer is
unit-testable without the UI.

## Extensibility

Kontena is provider-based. Every backend registers as an `IEngineProvider` with the
`EngineRegistry`, which probes providers for availability — the app hard-codes nothing.
Built-in providers cover Docker, Podman (reusing the Docker-compatible adapter), and an
in-memory Fake for development.

To add a new backend you implement two interfaces — `IContainerEngine` (the CEAL) and
`IEngineProvider` — and expose them through `IEnginePlugin` from **`Kontena.Sdk`**. A future
plugin loader (KON-49) will discover SDK plugins from external assemblies and register their
providers at runtime, which is the foundation for a store of installable adapters (KON-51).

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Build & run

```bash
# restore + build everything
dotnet build Kontena.slnx

# run the desktop app
dotnet run --project src/Kontena.App

# run the tests
dotnet test Kontena.slnx
```

## Design

Own visual identity — dark-first "control plane" look, Lucide icons. Palette, typography and
component rules live in the design system; mockups for the MVP + Phase 2 screens exist as
self-contained HTML.

## Tracking

Work is tracked in YouTrack project **KON**. First sprint: scaffold (KON-19) → CEAL contract
(KON-20) → Docker adapter (KON-27).

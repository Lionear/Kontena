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
│  ├─ Kontena.Sdk                    # The extension contract: CEAL, OAL, neutral models,
│  │                                 # tool seam, IEnginePlugin. References nothing. (MIT)
│  ├─ Kontena.Core                   # The app's own side: settings, channels, updates  (no UI)
│  ├─ Kontena.Core.Orchestration     # Host-side cluster logic: port forwards, rendering (no UI)
│  ├─ Kontena.Engines                # Backend registry and probing                     (no UI)
│  ├─ Kontena.Adapters.Docker        # Docker adapter & providers                       (no UI)
│  ├─ Kontena.Adapters.Podman        # Podman providers, reusing the Docker adapter     (no UI)
│  ├─ Kontena.Adapters.Kubernetes    # Kubernetes adapter (OAL)                         (no UI)
│  ├─ Kontena.Adapters.LocalClusters # kind / minikube provisioners                     (no UI)
│  └─ Kontena.App                    # Avalonia desktop UI (MVVM)
└─ tests/                            # one suite per project, plus
   └─ Kontena.Sdk.Tests              # guards the extension boundary itself
```

Everything but `Kontena.App` carries **no** Avalonia reference — the whole abstraction layer is
unit-testable without the UI. The dependencies run one way: adapters depend on `Kontena.Sdk` and
nothing else, and `Kontena.Core` depends on the SDK rather than the reverse.

## Extensibility

Kontena is provider-based. Every backend registers as an `IBackendProvider` with the
`BackendRegistry`, which probes providers for availability — the app hard-codes nothing.
Built-in providers cover Docker, Podman (reusing the Docker-compatible adapter), Kubernetes, and
in-memory Fakes for development.

To add a new backend you implement two interfaces — `IContainerEngine` (the CEAL) or
`IClusterEngine` (the OAL), plus `IBackendProvider` — and expose them through `IEnginePlugin`. All
of it comes from **`Kontena.Sdk`**, which is the only project an adapter references and is MIT
licensed for exactly that reason. A future plugin loader (KON-49) will discover SDK plugins from
external assemblies and register their providers at runtime, which is the foundation for a store of
installable adapters (KON-51).

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

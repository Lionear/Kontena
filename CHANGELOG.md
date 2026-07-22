# Changelog

All notable changes to Kontena are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project adheres to
[Semantic Versioning](https://semver.org/). Add finished work under `## [Unreleased]`; releasing a
`v<semver>` tag rolls that section into a dated version heading — see
[CONTRIBUTING.md](CONTRIBUTING.md#changelog).

## [Unreleased]

### Added

- **BuildKit build output** — the Build flow now drives the engine's `build` CLI with BuildKit
  (parallel stages, per-step cache hits, fine-grained progress) instead of the classic builder.
  The build context is read directly from disk, so `.dockerignore` is honoured natively and large
  contexts no longer inflate an in-memory tar. Recently used build contexts are remembered and
  offered as quick-picks. (KON-60)
- **Run modal recipes** — a curated, data-driven catalog for popular images (postgres, mysql,
  mariadb, mongo, redis, rabbitmq, nginx, …) pre-fills the *required* environment variables that
  image metadata can't express, plus a suggested name and default ports/volumes. Required-but-empty
  variables are flagged and block Run with an inline reason. (KON-58)
- **Compose: bring projects up from a file** — a "New project" flow that picks a compose file and
  streams `up` output live over the CEAL (driving the engine's Compose CLI), plus per-project
  **Down** (stop & remove containers and the project network) and **aggregated logs** (a combined,
  colour-per-service stream). (KON-59)
- **Clickable ports in Inspect** — the container Inspect tab now lists published port mappings, and
  published TCP ports open in the browser with one click. (KON-63)
- **Kubernetes clusters, alongside container engines** — Kontena now speaks to clusters as well as
  engines. The backend switcher groups what it finds into *Container engines* and
  *Clusters · Orchestrators*; picking a cluster swaps the whole UI into cluster mode, with a
  Kubernetes resource tree in the sidebar and a namespace picker in the command bar. Clusters are
  always an explicit choice — auto-connect and first-run onboarding stay engine-only. (KON-66, KON-67)
- **Cluster resource browsers** — Overview (health rollup and a nodes table), Nodes (cards with
  CPU/memory gauges), Namespaces, Workloads, Pods and Services, all filtered by the namespace
  picker. (KON-73)
- **Pod detail** — open a pod for tabbed **Overview / Logs / Shell / Events / YAML**, a live
  CPU/memory strip where a metrics-server is available, and a container picker that retargets the
  logs and shell. The interactive shell is the same terminal used for containers. (KON-70)
- **Workload actions** — **Scale** a Deployment/StatefulSet/ReplicaSet from a stepper dialog,
  **restart a rollout** behind a confirmation, and **port-forward** from the Services grid or the
  pod header. Actions are offered only where the workload kind and the cluster's capabilities
  support them. (KON-71)
- **Port forwards keep running** — a forward no longer belongs to the dialog that started it, so
  closing that window leaves the tunnel up. A new **Port forwards** entry in the cluster sidebar
  lists everything running, badged with a count, with the local address to copy, an Open button for
  web workloads, and Stop (or Stop all). Forwards are torn down when you switch backend or quit.
  (KON-97)
- **Kustomize and Helm as manifest sources** — the Apply page now takes more than typed YAML.
  Pick **Kustomize** and point at an overlay directory to build it (via `kustomize`, or `kubectl
  kustomize` when that is all you have); pick **Helm** and render a chart with values files, `--set`
  overrides and a release name. Either way the result lands in the editor as ordinary manifests and
  takes the same route as anything else: dry-run, plan, diff, apply. Build and lint findings — a
  missing base, a values key a template needs, a resource declared twice — are reported before the
  cluster is asked anything, and the exact command that ran is shown so it can be repeated in a
  terminal. Charts can be picked from Helm's own repositories, which can be searched, refreshed and
  added from the page. Plugins stay off for kustomize builds: a preview must not be able to execute
  arbitrary code. (KON-88, KON-89)
- **The apply plan is filterable, and leads with what changes** — a chart can render dozens of
  resources of which a handful actually differ, so the rollup beside the plan became the filter:
  each outcome is a chip that switches its rows on and off, and on a long plan the no-ops start
  hidden (with a count, so a filtered plan never reads as the whole plan). Rows are ordered by what
  needs attention — failed, then configure, then create, then unchanged — rather than by the order
  the documents happened to be rendered in.
- **Secrets are masked in diffs** — a Secret's values are replaced by a digest of themselves, so a
  rotated secret still reads as changed while the credential stays out of the diff.
- **Declarative apply, with a dry-run first** — a new *Apply manifest* page: paste or edit a
  manifest bundle, run a server-side dry-run to see a per-resource plan (create / configure /
  no change) with a unified diff, then apply. Editing the manifest invalidates the plan, so what you
  apply is always what you reviewed. The pod YAML tab became a live editor with Revert/Apply, and
  pods can be deleted from the grid behind a confirmation. (KON-69)

## [0.1.0] - 2026-07-20

### Added

- **Backend-agnostic container management** — one UI over the Container Engine Abstraction Layer
  (CEAL), with a live Docker adapter (via Docker.DotNet) and a Podman adapter, a provider-based engine
  registry, and a backend switcher that swaps engines without leaving the app.
- **Containers, images, volumes and networks** — multi-page navigation with live, event-driven
  refresh (no polling) and in-place row reconciliation, plus prune for stopped containers, unused
  images and dangling volumes.
- **Container detail** — a per-container page with live **logs** (level colouring, filter, follow),
  live **stats**, an interactive **terminal** (attached exec with a PTY, rendered with Exclr8.Terminal),
  and a structured **inspect** view (state, command, environment, mounts, networks, labels).
- **Run & pull flows** — a Run modal (image, name, ports, environment, volumes, network, restart
  policy) with a live command preview, auto-pull of missing images, and metadata pre-fill of exposed
  ports and declared volumes; a Pull-image dialog with streaming progress; quick-start templates from
  the empty state.
- **First-run onboarding** — a full-window wizard that detects the container engines on your machine,
  lets you pick a default, and links to a guided Podman install when none is present.
- **Activity timeline** — a live, filterable feed of engine events (create / start / stop / pull /
  remove and more), straight off the event stream with no polling.
- **Settings** — light / dark / system theme applied live plus a top-bar quick-toggle, a persisted
  settings store, a default-engine picker, a compact-density option for tighter list rows, and a
  configurable terminal font (family, size, ligatures).
- **Graceful states** — an empty state with quick-start, a no-match note, and an engine-down state
  with reconnect and available-engine fallback.
- **Refined look** — a crisp vector app mark used throughout, and theme-aware surfaces that hold up in
  both light and dark.

[Unreleased]: https://github.com/Lionear/Kontena/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/Lionear/Kontena/releases/tag/v0.1.0

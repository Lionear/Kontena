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

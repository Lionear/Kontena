# Changelog

All notable changes to Kontena are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project adheres to
[Semantic Versioning](https://semver.org/). Add finished work under `## [Unreleased]`; releasing a
`v<semver>` tag rolls that section into a dated version heading — see
[CONTRIBUTING.md](CONTRIBUTING.md#changelog).

## [Unreleased]

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
- **Settings** — light / dark / system theme applied live, a persisted settings store, a default-engine
  picker, and configurable terminal font (family, size, ligatures).
- **Graceful states** — an empty state with quick-start, a no-match note, and an engine-down state
  with reconnect and available-engine fallback.

<!--
Add finished work above under the right heading (Added / Changed / Fixed / Removed).
Keep bullets user-facing. Never write a version heading by hand — a v-tag release rolls this section.
-->

[Unreleased]: https://github.com/Lionear/Kontena/commits/main

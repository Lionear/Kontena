# Changelog

All notable changes to Kontena are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project adheres to
[Semantic Versioning](https://semver.org/). Add finished work under `## [Unreleased]`; releasing a
`v<semver>` tag rolls that section into a dated version heading — see
[CONTRIBUTING.md](CONTRIBUTING.md#changelog).

## [Unreleased]

_Nothing yet._

## [0.2.0] - 2026-07-26

### Added

- **Nightly builds** — `develop` is published every night as a rolling `nightly` prerelease
  (Windows .zip, Linux .AppImage, macOS .zip), so what is finished between two releases can be run
  without building it yourself. A night in which nothing was merged publishes nothing, rather than a
  fresh version number on an identical build. (KON-108)
- **Nodes left behind by an upgrade say so** — every node's kubelet is now compared against the
  apiserver it is registered with, following the published Kubernetes version skew policy. A kubelet
  further behind than the control plane allows (three minor versions, or two below apiserver 1.28)
  gets a warning on its node card, and a kubelet *newer* than the apiserver — never supported in any
  configuration — is flagged as an error. The Nodes cards carry the chip; the cluster overview marks
  the version column. It is a comparison of two numbers Kontena already reads, so it needs no network
  and cannot go stale. Whether the release itself is still supported upstream is a separate question
  and is deliberately not answered here. (KON-95)
- **Pause a port forward instead of stopping it** — closing a tunnel is usually temporary: something
  else needs that local port for a minute. **Pause** closes it and genuinely hands the port back,
  but keeps the row, and **Resume** puts it straight back on the same local port — no retracing the
  service and ports to get the address you already handed to other things. It reads as your decision,
  not a failure: a paused forward is worded and coloured differently from one that dropped. Stop
  still means done with it. (KON-106)
- **Port forwards are offered back on your next visit** — the tunnels themselves cannot survive the
  app closing (a forward *is* a local listener in this process), but the intent behind them now does.
  What was on the Port forwards list when you left a cluster comes back when you return to it, as
  rows that are *not open*, with **Reopen** per row and **Reopen all** in the header. Nothing opens
  by itself: a tunnel that reconnects to production because the app started is a surprise, and the
  local port may since have been taken. Remembered per backend — a forward means nothing on another
  cluster — and a forward you **Stop** is gone for good, which is how you say you are done with it.
  (KON-105)
- **A port forward that falls over says so immediately** — the tunnel itself now reports when it
  ends: the row flips to *Dropped* while you are looking at it, with the reason (the pod was
  replaced, the cluster refused the connection) in a tooltip, instead of only correcting itself the
  next time you open the page. A dropped forward keeps its place on the list — losing the local port
  silently would be worse — stops counting towards the sidebar badge, and offers **Reconnect** to
  reopen it on that same local port. The local listener is released when the tunnel dies, so nothing
  is left accepting connections it can never serve. (KON-102)
- **Kontena reopens what you were on** — engine or cluster, whichever it was. Settings › Engines
  replaces the old *default engine* dropdown with a single **On launch** choice: continue where you
  left off, always open one named backend (clusters included), or take the first engine that
  answers. An existing default carries over as a pinned choice rather than being reinterpreted.
  (KON-98)
- **A backend that isn't there says so** — the engine-down screen became a backend-down screen. It
  names what it could not open and why, in terms that fit what it is: an apiserver that did not
  answer reads differently from an expired token, a rejected certificate or a socket that is simply
  stopped. A remembered backend that no longer exists — a kube-context removed, an engine
  uninstalled — is reported as gone and forgotten, instead of offering a reconnect that can never
  succeed. Both container engines and clusters are offered as somewhere to go from there. (KON-98)
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
- **"Launch Kontena at login" actually does something now.** Until now the switch only remembered its
  own position: nothing was ever written, so nothing ever started. Kontena now registers itself the way
  each platform expects — a startup entry your desktop reads on Linux, a per-user Run entry on Windows
  (never asking for admin), a Login Item on macOS — and removes it again when you switch it off. It
  shows the truth rather than our record of it, so switching the entry off in GNOME's startup settings,
  Windows' Task Manager or macOS' Login Items is reflected here instead of quietly disagreeing, and the
  switch snaps back if the registration did not take. Where Kontena cannot work out a path that will
  still be valid after an update — a copy run from a build directory, say — the row is not offered at
  all rather than promising a login that would do nothing. (KON-103)
- **Kontena updates itself.** An installed copy now checks for a new version on launch, fetches it
  in the background and offers a restart — no more going back to the releases page to find out you
  were three versions behind. A toast announces it once; after that the update waits as an entry at
  the bottom of the sidebar, which follows along (`Downloading… 62%`, then `Restart to update`)
  rather than repeating itself. The card shows what is new, and **your containers keep running —
  only the app restarts**. Nothing is applied behind your back: the download is verified before it
  counts as ready, and restarting is your call, including *Install on next launch* if now is a bad
  moment. Settings → Updates picks the channel — **Stable** for tagged releases, **Preview** for what
  has been promoted for the next release, **Nightly** for the rolling build cut from `develop` — and
  can turn the background download off. A copy that cannot replace itself (unpacked from an archive,
  or installed by your distribution) says so plainly and points at the releases page instead of
  offering a button that would do nothing. (KON-110)
- **Sign in to a registry and pull private images.** Every pull used to go out anonymously, so a private
  image simply could not be fetched — and the error you got named the image, not the missing login.
  Settings › Registries takes a registry, a username and a password or token, **checks it with the
  registry before saving it**, and keeps the secret in your system keychain. Logins you already made with
  `docker login` are picked up as they are — including the credential-helper setups where the password
  lives outside the config file — and listed as coming from your engine config, so a pull that works
  without you signing in here is not a mystery. Kontena never writes to those files: its own logins go to
  the keychain, and signing out takes the secret with it. (KON-114)
- **Put a container on a network you already made.** Creating a network was possible, using it was not: a
  network could only be chosen while *creating* a container, so an existing one had to be re-run to join
  it. The plug icon on a network row now opens its attachments — what is on it, with **Detach**, and a
  picker to **Attach** anything that is not. Stopped containers can be attached too; it takes effect when
  they next start. `host` and `none` are left out, because those are modes the engine provides rather
  than networks you join. (KON-115)
"Add engine or cluster…" in the switcher now opens a wizard that ends in a connection Kontena has actually made. It shows what was already found on this machine, then walks through a remote engine over SSH or TLS, or a kubeconfig and the contexts in it. The last step is the connection itself — you see which part failed, in the transport's own words — and nothing is stored until it succeeds. Kubeconfig files outside the default location can now be added, so a downloaded cluster config no longer has to be copied into `~/.kube`.
Backends can be given a name of your own. A kube-context is listed under whatever its cluster calls it, which is routinely something like `gke_myproject-prod_europe-west4_cluster-1`. Settings › Engines now has a name field per backend — leave it empty to go back to the original — and the add wizard offers one for each cluster as you add it. The new name is used everywhere the old one was: the switcher, the title, the engine list and the "cannot reach it" messages.
A kubeconfig you added can be removed again. `Settings › Engines › Kubeconfigs` lists the files Kontena reads contexts from, with the default one shown as always read. Removing a file stops Kontena reading it and takes its clusters, their names and their choices with it — the file itself is left alone. The cluster list now names the file each context came from, which is the only thing telling two `default` contexts apart.
A remote engine can be changed. **Edit** loads it back into the form, and saving keeps it under the same identity — so the name you gave it, its keychain entry, its remembered port forwards and a launch pin all survive a corrected hostname. Removing and re-adding lost every one of them, silently, which was the only way to fix a typo before.
- **Connect an engine on another host.** Kontena managed the engine on your own machine and nothing else;
  now a remote Docker appears in the switcher like a local one, with the same pages and the same actions.
  Two ways in, added under Settings › Engines: **SSH**, which forwards the remote socket using the keys,
  agent and `ssh_config` you already have — nothing to generate or open up — and **TCP with TLS**, pointed
  at the `ca.pem`/`cert.pem`/`key.pem` directory an existing Docker TLS setup already uses. *Test
  connection* really connects before anything is saved, and reports what the host said rather than a
  generic failure. A TCP endpoint without certificates is refused unless you state outright that you want
  it: an unauthenticated engine port hands control of that machine to anyone who can reach it. (KON-46)
- **Credentials go in your system keychain.** Kontena can now store secrets where the operating system
  keeps them — the Secret Service on Linux, Credential Manager on Windows, the login Keychain on macOS —
  instead of in a file of its own. They show up in your own keychain tool under a readable name, so you
  can inspect and revoke them without Kontena's help. There is no fallback on purpose: if no keychain is
  reachable, Kontena says so in Settings › About and stores nothing, rather than writing a password
  somewhere it should not be. This is the groundwork for logging in to private registries. (KON-52)
- **Look inside a volume.** A volume used to be a name, a size and a mountpoint you could not open — to
  see what was actually in it you started a container with it mounted and poked around by hand.
  **Browse** on a volume row opens its contents: directories first, with sizes and how long ago each
  entry changed, and clicking a directory goes in. It is read-only, and deliberately so; nothing here
  writes, moves or deletes. Kontena reads the volume by mounting it into a container that is **created
  but never started**, so no image needs a shell and nothing of yours runs. Very large directories are
  listed up to a limit and say so, rather than going quiet for a minute. (KON-90)
- **Create a volume without running a container first.** Volumes could be listed and deleted, but the
  only way to *get* one was to let some earlier container create it as a side effect — so a named
  volume you wanted to mount had to be conjured up by running something you did not want. **New
  volume** on the Volumes page asks for a name and a driver, and the volume is then there to mount from
  the Run container dialog. A name that is already taken or invalid is reported in the dialog with what
  you typed still in it. (KON-91)
- **Create a network from the Networks page.** Networks could be listed and removed but not made, so
  putting two containers on a network of your own meant creating it outside Kontena first. **New
  network** asks for a name, a driver and — optionally — a subnet; left empty, the engine picks one and
  the list then shows what it chose. Only drivers that can actually be created are offered: `host` and
  `none` are the engine's own and cannot be made, and `overlay` needs Swarm. A subnet that is not valid
  CIDR is caught before the request goes out, since the daemon's own message for it is considerably
  less clear. (KON-92)

### Changed

- **Changelog entries are files now.** A finished change adds `changelog.d/<ticket>.<category>.md`
  instead of editing `CHANGELOG.md`, and the release folds them all into `[Unreleased]` before
  cutting the notes. Nothing changes about what a release reads like — this is about the branches on
  the way there no longer conflicting on the same one line of a shared list. (KON-104)
- **A dropped port forward shows up in the sidebar.** The badge counts tunnels that are running, which
  is right — and meant that when your last one fell over it went from `1` to nothing, reading as "there
  is nothing here" at the exact moment something wanted your attention. A dropped tunnel now puts a
  small amber marker on the Port forwards item, next to (not instead of) the count. Only dropped:
  paused and remembered rows are states you chose, and dressing them up as a problem would make the
  marker meaningless. (KON-107)
Clusters now appear in the switcher because you chose them, not because they were found. Local engines are still detected and added automatically — they are on your machine and there are only ever a few. A kubeconfig is different: it collects clusters over time, often other people's, and listing all of them puts production one click from a scratch cluster. New contexts are announced in the switcher instead of added, `Settings › Engines › Clusters` is where you tick and untick, and a cluster you did not choose is never contacted. **Existing installations keep every cluster they already had** — nothing disappears on update.
A nightly or preview download now follows its own channel. Installing one of the rolling builds and starting it without an existing configuration used to land on Stable, so the first thing Kontena offered was a move off the build you had just deliberately chosen. The channel is read from the build's own version, and Settings says so while nothing has been picked. A channel you did choose still wins over everything — an install never drifts onto a rolling stream by itself, which is the rule this leaves untouched.

### Fixed

- **The Updates settings read as one column again.** The explanatory lines under *Release channel* and
  *Download updates automatically* sat indented from their own headings, drifting further right the
  more they wrapped — a width cap on a wrapping line leaves it centred in the row rather than aligned
  with everything above it. (KON-110)
- **The Networks page shows which containers are attached.** It always said "— none", whatever was on the
  network: the engine's network *list* does not include attached containers — not even when asked
  verbosely — and only inspecting each network reveals them. So the column has been empty since it was
  added, and looked like an answer. (KON-115)
Settings no longer lose changes made elsewhere in the app. Configured remote engines could disappear on the next backend switch, because each part of Kontena saved its own full copy of the settings file and so reverted whatever another part had stored since. Registry logins, port forwards and remembered build contexts were open to the same loss.
"Add engine or cluster…" in the backend switcher now opens Settings › Engines, where a remote engine is configured. It previously closed the switcher and did nothing else.
Destructive buttons and warning blocks are red again in the dark theme. The colour behind them was written in a way that parsed as opaque olive, which affected the prune buttons on Containers, Images and Volumes as well as the port-forward warning.
A prerelease git tag no longer publishes a release. `v0.2.0-rc.1` was accepted as a release tag, which sent a release candidate to everyone on the stable channel, took over GitHub's *latest release*, and archived the pending changelog under the candidate's version — leaving the real release with empty notes. Only `vMAJOR.MINOR.PATCH` starts a release build now; anything else is refused with a message pointing at the preview and nightly channels, which exist for builds ahead of stable.
- **Nothing is deleted without asking.** Removing a volume, a container, an image, a network, a
  Compose project, a remote engine, an added kubeconfig or a registry login all went straight through
  on a single click. The volume case was the worst of them: the delete was forced, so one click threw
  away everything stored in it with nothing in between. All of them now put a confirmation in front,
  and the confirmation says what goes away and whether it comes back — that a container is running and
  will be killed, which container has the volume mounted, that an image simply has to be pulled again.
  Signing out of a registry and dropping a kubeconfig are asked about too but not dressed up as data
  loss, because neither touches a file; a dialog that cries wolf is one people learn to click away, and
  then it no longer works for the volume either. The confirm button of a destructive dialog is finally
  red as well — it had been rendering in the same green as Save. (KON-126)

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

[Unreleased]: https://github.com/Lionear/Kontena/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/Lionear/Kontena/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/Lionear/Kontena/releases/tag/v0.1.0

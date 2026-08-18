# Changelog

All notable changes to Kontena are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project adheres to
[Semantic Versioning](https://semver.org/). Add finished work under `## [Unreleased]`; releasing a
`v<semver>` tag rolls that section into a dated version heading — see
[CONTRIBUTING.md](CONTRIBUTING.md#changelog).

## [Unreleased]

_Nothing yet._

## [0.4.0] - 2026-08-17

### Added

- **The nerdctl plugin now shows live stats and activity, builds images, brings Compose projects up, and
  pulls, tags and removes images.** Container CPU and memory are sampled every couple of seconds, and the
  activity feed follows containerd's own event stream — which reports containers and images, but never
  volumes or networks, because containerd has no events for those. Building is only offered when a
  buildkitd is actually reachable: `nerdctl build` exists whether or not it can work, so Kontena looks for
  the socket rather than promising a build that fails a few seconds later. Compose reports what nerdctl
  reports, without its log formatting. Opening a terminal in a container stays unavailable on this
  backend — driving nerdctl means starting a process and reading its output, with no way to type into it —
  as does browsing a volume's contents, and there is still no way to install this plugin; that comes later.
- **The nerdctl plugin is now downloadable.** Every release carries a
  `kontena-plugin-nerdctl-<version>.zip` next to the app: unzip it into
  `%APPDATA%\Lionear\Kontena\plugins\nerdctl\` on Windows or `~/.config/Lionear/Kontena/plugins/nerdctl/`
  on Linux and macOS, start Kontena, and approve it when asked. It adds one backend per containerd
  namespace and needs nerdctl already on the machine — it does not install one. Deliberately not
  bundled with the app: downloading, unpacking and approving is the same path the plugin store will
  take later, and shipping it in the box would prove none of it.
- **Kontena can now create, start, stop, restart, pause, resume and remove containers on containerd via
  the nerdctl plugin**, and create and remove volumes and networks, and prune unused containers, images
  and volumes to reclaim disk space — all of it was read-only before. Creating a volume with a driver
  other than nerdctl's built-in one now fails with a clear error instead of silently creating a default
  volume anyway, since nerdctl has no way to honour that choice. Prune reports how many items it
  removed, but not how much space came back — nerdctl itself doesn't report that. Attaching or detaching
  a running container from a network isn't possible at all through this backend; nerdctl has no command
  for it. Building images, Compose, exec-into-container and live stats/events are still out of reach, and
  there is still no way to install this plugin — that comes later.
- **Kontena can now show containers, images, networks and volumes from containerd via the nerdctl plugin.** Each containerd namespace appears as its own entry in the backend switcher, so if you work in the `k8s.io` namespace with Kubernetes tooling, you see those resources right there instead of an empty list in the default namespace. The Docker integration's namespace is filtered out to keep things clear if you have both Docker and containerd running.
- **Kontena now shows the alerts firing on your cluster.** A new **Alerts** page sits under Overview
  and reads whatever Alertmanager and Prometheus your cluster already runs — Kontena finds them
  itself, over the API server, with nothing to configure and no port-forward to keep alive. The
  sidebar carries a red count of what is firing and unmuted, the one badge in that list allowed to be
  loud; pending and silenced alerts are deliberately left out of it, because a number that counts
  things you have already decided about is a number you learn to ignore.
- Alerts are **grouped by name rather than listed flat**, so twelve replicas of one broken Deployment
  read as one problem with twelve instances instead of twelve problems. The group header says how
  long it has been firing, how many instances there are and which receiver it routed to; the rows
  underneath name the pod, node or certificate involved. **Firing, pending and silenced are separate
  sections**, because they are different questions: go and look, not yet and maybe never, and someone
  already decided.
- **When no Alertmanager answers, the page says where it looked** — every label and service name it
  tried, and in which namespaces — instead of showing an empty list. A cluster running one under a
  name Kontena does not know is a gap worth seeing. If the search was refused rather than coming up
  empty, it says that too, since only one of those is something you can grant. There is an **Install
  with Helm** button that hands off to the existing apply page with the kube-prometheus-stack chart
  filled in; Kontena ships no copy of that chart, because owning it would mean owning its upgrades.
- **Alerts open into a detail drawer** — the same shape as a pod's, so nothing new to learn. Instead
  of a wall of key-values, the footer is a set of jumps to what the alert's own labels already point
  at: the **Pod**, its **Logs**, the **Runbook** the rule's annotations named, and the **graph in
  Prometheus** Alertmanager recorded when the alert fired. Reading the same alert in Slack gets you
  the labels; this gets you to the object.
- **Silence an alert from its detail, with the expiry already filled in.** A silence is imperative
  and time-boxed on purpose — it is never a manifest, and it always ends, because a mute with no end
  is a rule someone deleted without saying so. The confirmation names exactly what will be muted and
  until when before Alertmanager is ever told. The Silenced section on the Alerts page gets the same
  **Expire** action, for ending one without opening the alert it belongs to first.
- **Alert rules can be written in Kontena.** *New rule* on the Alerts page opens an editor for one
  alerting rule — name, expression, `for`, severity, labels and annotations — and shows the
  `PrometheusRule` it composes, byte for byte, next to the form. No `managed-by` label, no timestamp,
  nothing Kontena adds for itself, so the manifest you read is the object that gets applied. Applying
  goes through the ordinary apply page: server-side dry-run, then the diff, then apply, the same
  review a pasted manifest gets.
- **The rule editor says whether the rule will actually be picked up.** The namespace field reads this
  cluster's Prometheus and marks each namespace *watched* or *not watched* from its
  `ruleNamespaceSelector`, in amber where a rule would apply cleanly and then be ignored — the quieter
  and more dangerous of the two failures. Free text stays allowed, since a namespace can be created
  after the rule is written. And the label the Prometheus' `ruleSelector` requires (`release:
  kube-prometheus-stack` on a kube-prometheus-stack cluster) is filled in on the object and marked as
  not yours to drop: leaving it off is the most common way a hand-written rule silently does nothing.
  Where the Prometheus cannot be read, the field says that rather than guessing.
- **Roll out a Kubernetes cluster on machines you already own.** Settings › Roll out a cluster walks
  from a distribution to a running cluster: list the machines and give each one a role, say how the
  rollout gets in, let Kontena check every machine, then install. It drives `k0sctl`, so one config
  describes the whole cluster and what comes out ships Autopilot — it can upgrade itself later.
  Already have a `k0sctl.yaml`? Import it instead of typing the hosts again.
- **Your SSH key is never stored, only the path to it.** The same arrangement as a remote engine over
  SSH. There is no password field anywhere in the flow: a password Kontena would have to hold in
  order to reach five machines is exactly the thing not worth holding.
- **The machines are checked before anything is installed.** Reachable, sudo without a password
  prompt, the ports Kubernetes needs, swap off, clocks in step, and no two machines claiming the same
  hostname, MAC or `product_uuid` — the classic result of cloning a VM. Every answer says *why*, and
  "could not be checked" is its own answer rather than being counted as a pass. What goes wrong here
  would otherwise go wrong halfway through the install, and then there is a half-built cluster on
  your machines. Swap can be turned off from the report; the clock cannot, because choosing a time
  source is yours to make.
- **A rollout that stops tells you where it stopped and what to do next.** Progress per machine,
  `k0sctl`'s own output as it arrives, and three ways on: try that machine again, continue without
  it, or read what the tool said about it. **Nothing is rolled back** — undoing a half-finished
  install would also take out the machines that worked — and the screen says so, along with what
  state the stopped machine is left in. A rollout runs from your computer, so closing Kontena stops
  it; you are told that before it happens, and the next launch offers to carry on from the machines
  that were already up.
- **Kontena can now fetch kubectl for you.** Settings › Tools offers kubectl the same
  buttons kind and minikube already had: download a verified copy, see when a newer release is out,
  hand your system install over to Kontena, or remove Kontena's copy again. The binary comes from the
  Kubernetes project's own `dl.k8s.io` and is checked against the digest published beside it before it
  is ever runnable — as everywhere else, a publisher without a per-file checksum gets no download
  button at all.
- **kubectl older than 1.27 now reads as out of date rather than fine.** That is the version where
  `kubectl kustomize` carries kustomize v5, which Kontena falls back to for rendering overlays when
  kustomize itself is missing. The row says what an older one costs instead of leaving you to find out
  from a rendered manifest.
- **Kontena can now load engine plugins from your own plugins folder.** A backend used to have to be
  built into Kontena to exist at all, which meant every engine anyone wanted had to ship to everyone.
  Drop a plugin into `plugins/` inside your Kontena configuration folder and it is offered in the
  switcher alongside Docker and Podman. Nothing runs unasked: the first time Kontena finds a plugin it
  shows you what the plugin says about itself — name, publisher, version and where it came from — and
  loads it only if you allow it. An update is asked about again, because it is different code than the
  code you agreed to. A plugin that fails to load is reported and skipped; it cannot stop Kontena from
  starting.
- **Manifest Studio is now a plugin you can install.** It adds three pages to the sidebar — Editor,
  Plan & apply, and Source control — for writing Kubernetes manifests over a folder or Git repository:
  completion and validation come from the OpenAPI of the cluster you have open, including its custom
  resources, and fall back to bundled schemas for the last three Kubernetes minors when no cluster is
  connected. Plan and apply reuse Kontena's own server-side dry-run and diff, so what you see before
  applying is what the API server says, not a local guess. It does not ship in the box: download
  `kontena-plugin-manifest-studio-*.zip` from the release, unzip it into its own folder under
  `~/.config/Lionear/Kontena/plugins/` (or `%APPDATA%\Lionear\Kontena\plugins\` on Windows), and
  approve it when Kontena asks. The approval dialog lists what the plugin says it will do — read it;
  a plugin runs inside Kontena with the access you have, and until signed builds land that list is a
  claim by its author rather than a limit Kontena enforces. Everything a plugin contributes carries a
  `plugin` badge in the sidebar, so it is always clear which parts of the window are not Kontena's own.
- **Build images and look inside a volume on Apple `container`.** Builds run through the runtime's own
  BuildKit builder with the step-by-step output you get everywhere else, and a volume's contents can be
  browsed from the Volumes page.
- Browsing here starts a small container for a moment — Apple's runtime offers no way to read a volume
  without one — and it is removed again immediately. That completes the backend: everything Kontena
  offers for Docker and Podman now works here too, apart from what this runtime genuinely lacks.
- **Pull, tag and inspect images on Apple `container`.** Pulling reports the runtime's own progress as it
  goes, an image can be given a second name or removed, and the Run dialog pre-fills an image's
  environment variables.
- Its ports and volumes are not pre-filled: Apple's runtime does not report what an image declares
  there, so those are typed by hand rather than guessed. A private-registry login is refused on this
  backend — the runtime can only use one by keeping your password in its own store, and Kontena keeps
  registry secrets in your keychain. Public registries pull normally.
- **Logs, a terminal and live usage for Apple `container`.** A container's log streams as it is written,
  the terminal opens a real shell inside the container — one you can type in, that resizes with the
  window and passes Ctrl-C through — and CPU and memory are sampled every couple of seconds.
- The log is one stream: Apple's runtime writes a container's stderr to the same channel as its stdout,
  so Kontena shows every line the same way rather than colouring some of them on a guess. The first CPU
  reading of a session is empty, because a percentage only exists between two samples.
- **Run a container, make a volume or a network, and reclaim space on Apple `container`.** The Run
  dialog, the create dialogs and the prune and remove actions all work on this backend now, instead of
  quietly doing nothing.
- Two things this runtime will not do, and now says so instead of pretending: it has no restart policy,
  so asking for one is refused rather than accepted and forgotten; and a volume or network something is
  still using cannot be removed, which now arrives as an explanation rather than a row that stays put.
- **Apple `container` is now a backend.** On macOS with Apple's native runtime installed, it appears in
  the switcher alongside Docker and Podman: containers, images, volumes and networks are listed, a
  container's detail page reads its command, environment, mounts and addresses, and containers can be
  started, stopped, restarted and deleted. It stays out of sight on Windows and Linux, where the runtime
  cannot exist.
- Logs, terminal, stats, image pulls and builds are not wired up for this backend yet, and the runtime
  itself has no pause, no Compose and no event stream — Kontena hides what it cannot offer rather than
  showing buttons that fail.
- **Kontena now recognizes the most common reason Podman is unreachable, and offers to fix it.** On a
  fresh rootless install, `podman ps` works from a terminal while Kontena finds nothing — the CLI
  talks to storage directly, but Kontena needs the API socket that `podman.socket` opens, and that
  unit is often present but never enabled. When that's the case, the down card shows the exact
  command with **Copy** and **Run it for me** buttons; the fix needs no elevation, since it manages a
  user unit.
- **Sort the cluster resource lists by column.** Click a column header on any Kubernetes list — Pods,
  Services, Workloads, Namespaces, Ingresses, Volume claims, Volumes, Storage classes, Events — to sort
  by it; click again to reverse direction.
- **Filter Pods by phase.** A dropdown on the Pods page narrows the list to Running, Pending, Succeeded
  or Failed, alongside the existing search box.
- **"Controlled by" on the pod detail page is now a link.** Click through to the Deployment,
  StatefulSet, DaemonSet, Job or CronJob that owns the pod.
- **Delete a workload, a service or an ingress from its own page.** Deleting any of them used to mean
  going to **Resources**, finding the kind in the list and doing it there — or reaching for `kubectl`.
  Every row on Workloads, Services and Ingresses now has a **Delete** next to the actions it already
  had, behind the same confirm every destructive action gets. The confirm says what this particular
  delete costs rather than a generic warning: a StatefulSet leaves its volume claims behind, a CronJob
  takes its schedule with it, a service leaves the pods running but stops anything reaching them by
  name — and gives up a LoadBalancer's external address for good — while an ingress only closes the
  way in from outside.
- **Delete from a detail page, not only from the list.** A workload, service, pod, config map or
  secret can now be deleted from the page that describes it — which is where the decision is usually
  made, since reading the detail is what tells you it can go. The confirm says the same thing it says
  on the list row, and the page closes behind it rather than staying up describing something that is
  no longer there. Nodes and namespaces deliberately get no such button.
- **The Workloads page has a Pods card.** It summarised Deployments, StatefulSets, DaemonSets, Jobs and
  CronJobs, but not the pods those produce — even though Pods sits in the same section of the sidebar
  and the page invites you to pick a kind. The card carries the split by phase, in the same colours
  the Pods list already gives each one, and opens that list.
- **The pod detail now charts CPU and memory over time, not just their current value.** A sparkline sits behind the live readout in the header for a glance, and a new Metrics tab gives both measures a full chart with a time range and a crosshair that reads out the sample you point at. Because `metrics-server` keeps no history of its own, Kontena charts what it sampled while the pod page was open — roughly the last 15 minutes; the longer ranges stay greyed out until a history source such as Prometheus is available.
- **Where a cluster runs Prometheus, the pod usage charts now reach back hours and days instead of minutes.** Kontena finds it by itself — no address to configure — and reads it through the apiserver, so there is no port-forward to keep open and no second login. The 1h, 6h, 24h and 7d ranges light up as soon as it answers; on a cluster without one they stay disabled and the charts keep to the last 15 minutes Kontena sampled itself.
- **The usage charts are no longer only on pods.** Containers get CPU and memory over time in their Stats tab, with a sparkline behind the live figures; nodes get a Metrics tab with CPU, memory and disk; and deployments, statefulsets, daemonsets and namespaces get one that sums everything they own. Where the cluster runs Prometheus, the workload and namespace charts reach back hours and days, and a workload's history follows the pods a rollout replaced instead of stopping at the ones running now. Node charts stay on the last 15 minutes Kontena sampled itself.
- **macOS gets a .dmg, and it is now the recommended download.** Mount it, drag Kontena to Applications,
  and open it once with right-click → Open. The .pkg stays in the release for anyone who prefers it, but
  macOS refuses to run an unsigned installer package and there is nothing you can clear to change that —
  which is exactly why the disk image exists.
- **Usage charts now show gaps as gaps, mark the limit, and cover the whole cluster.** Points are placed by when they were measured, so a stretch where nothing was recorded reads as a break in the line instead of a straight stretch implying steady load. Memory charts draw the container's limit — and node charts the node's allocatable — as a dashed rule, turning "654 MB" into "654 MB against a 1 GB ceiling". The cluster overview has charts of its own, and node pages now reach back hours and days through node-exporter, with a note where that measures memory slightly differently from the kubelet.
- **Migrate a container to another engine, with its configuration and the contents of its named
  volumes.** Pick a target engine on a container row and Kontena shows the plan before it touches
  anything: what comes along, what does not, and what stops the migration entirely. The source is
  stopped first — a volume copied out of a running container is a torn copy — and then left exactly
  where it was; it is never removed. The new container is created stopped, so starting it is your
  move. Volume contents travel as a tar and are unpacked inside a container, so file ownership
  survives the trip; a volume that already exists on the target and holds data is left alone unless
  you tick it. What cannot come along is spelled out rather than summarised — a restart policy the
  target does not have, every network beyond the first, name resolution between containers, and the
  settings Kontena does not read at all (health check, capabilities, devices, ulimits, read-only root
  filesystem). A container that is one of several services in a Compose project is refused outright
  when the target has no name resolution: it would start and then fail on its first connection to a
  sibling.
- **Kontena now tells you when a backend runs a release nobody maintains any more.** A Docker daemon
  that went out of support a year ago used to look exactly like a current one. Backends whose release
  line has been dropped by its publisher now carry an "Unsupported" pill in the switcher, with the
  line and the date it ended behind it; a supported release that is behind on patches says which newer
  one exists, without a warning. This works for container engines and clusters alike.
  The support dates come from the publishers' own release calendars, not from a list inside Kontena —
  Kontena is not the vendor of Docker, containerd, Podman or Kubernetes and should not be what decides
  when their releases stop being supported. Looking them up asks for a product's whole calendar and
  compares it on your machine, so nothing is ever told which versions you run. Answers are kept for a
  day, offline the last answer stands, and a backend nobody publishes a calendar for is left alone
  rather than guessed at.
- **The cluster overview says how much the cluster has, not only how much it is using.** Two tiles
  join the counts at the top of the page — **Max CPUs** and **Max Memory**, the allocatable capacity
  added up over the Ready nodes — and the node table below them gains a **Memory** column beside its
  CPU one, in the same "used / capacity" form. Both read the nodes themselves rather than a metrics
  source, so the ceiling is there even on a cluster with no metrics-server.
- **Images can be tagged and pushed, not just pulled.** Every image row has a **Tag and push** action:
  give the image the name its destination expects — `ghcr.io/me/app:1.2` — and send it there. The dialog
  names the registry that reference resolves to before you press anything, and **Tag only** stops after
  the name is added, without uploading. Pushing a name the image does not carry yet tags it first, so the
  two steps are one action rather than a thing to remember. The login is the one you already stored under
  **Settings → Registries**, exactly as a pull uses it — nothing new to configure, and no second place
  keeping your password. A registry with no stored login is pushed to anonymously, which most of them
  refuse; the error then says which registry refused and where the account for it goes, instead of only
  "unauthorized". Tagging adds a name and never removes one, so the reference you started from stays.
- On the containerd (nerdctl) and Apple `container` backends, a push to a registry Kontena has a login
  for is refused rather than sent unauthenticated — the same limit their pulls already have, for the same
  reason: neither can be handed a credential for one operation without storing it somewhere Kontena does
  not control. Pushing to a registry that accepts anonymous writes, such as a local one, works there.
- **Kontena can keep a diagnostic log.** A new switch under Settings › Diagnostics writes what you did
  — the action, the resource, and the engine or cluster it ran against — together with a memory reading
  every half minute, to `diagnostics.log` beside your settings file. It is off by default. Turning it on
  is for the session after something went wrong: the previous run's log is kept alongside it as
  `.prev`, so a crash and the launch that follows it are both still on disk when you go looking.
  Credentials, tokens, kubeconfig and manifest content, secret values and environment variables are
  never written, and anything credential-shaped that reaches a line by another route is stripped before
  it is saved. The developer trace (`KONTENA_TRACE=1`) is unchanged and still goes to stderr; this is
  the same set of marks with a switch you can reach and a file you can send on.
- **A pod's Overview tab now says what the pod runs and what it reads.** The image is on the tab you
  land on instead of only in the container table below it, next to the pod's labels — the ones every
  service selector matches against. Underneath, a **Config & secrets** section lists every ConfigMap
  and Secret the pod reaches and how it reaches each one: mounted as a volume, read as environment,
  or used to pull images. Open one to see its keys. A ConfigMap's values are simply there, a Secret's
  stay masked behind an eye button, and pressing that eye a second time drops the value rather than
  folding it away — nothing is kept in the app once it is off screen. All of it comes from the pod's
  own spec, so the section costs no extra request until you ask for an object's keys.
- **The Alerts page refreshes itself now.** Alertmanager offers no watch stream, unlike the API server
  behind every other cluster page, so the list only moved when you opened it or pressed refresh —
  which on a "what is wrong right now" screen means clicking to find out whether anything has changed.
  It re-reads every 30 seconds by default. The interval is yours to set under **Settings › General ›
  Alerts**, and **Off** is the first entry in the same picker rather than a separate switch, so there
  is nothing that can end up on at no interval.
- **Only while you are looking at it.** The refresh starts when you open the Alerts page and stops the
  moment you leave it. Kontena never polls a cluster nobody has on screen, and switching clusters or
  pages does not leave a timer running behind you.
- **A refresh that fails says so.** The notice above the list now carries the cadence and when the
  alerts you are reading were actually read; if a refresh fails it turns amber and names the failure,
  keeping the alerts it already had rather than going blank — an empty list would read as an
  all-clear, and a silently stale one is worse than either.
- **A managed cluster is now measured against its own provider's support window.** GKE, EKS and AKS
  each keep a Kubernetes release alive on their own schedule, and none of them matches upstream's —
  so a cluster judged by upstream's dates would be called unsupported while its provider was still
  supporting it, sometimes a month early. Kontena now asks about the calendar belonging to whatever
  the cluster says it is. Anything that is plain Kubernetes — kind, minikube, k3s, kubeadm — takes the
  upstream calendar, which for kind and minikube is not an approximation but the exact answer.
  This completes the version health question that in-cluster version skew answered the other half of:
  skew says whether a cluster's own parts agree with each other, this says whether anyone is still
  fixing the release it runs.

### Changed

- **The first-run wizard no longer advertises a runtime your machine can never run.** The "Apple
  container · Coming soon" row was a full-size engine row on every platform, so on a machine with one
  detected engine a third of the list was a roadmap item — and on Linux and Windows it announced a
  native macOS runtime that will never arrive there. It now appears only on macOS. (KON-337)
- **The switcher no longer lists an engine that isn't installed.** On a machine with only Docker,
  Podman used to sit there permanently as a row saying "Not connected" that could not be clicked, and
  the other way round. An engine that *is* installed but stopped still appears — that is the row you
  open the switcher for — and so does the one Kontena would start on, even if it has since
  disappeared. Settings › Engines is unchanged and still lists everything, including what is not
  installed.
- **The external tools have moved out of Local clusters into Settings › Tools.** kubectl and helm are
  needed for every cluster — a remote GKE or EKS context just as much as one built here — so looking
  them up on a page named after this machine was the wrong place to have to go. The new section lists
  every tool Kontena drives, grouped by what you need it for: **Working with clusters** (kubectl,
  helm, kustomize), **Clusters on this machine** (kind, minikube) and **Container engines** (podman).
  Each row still says whether it is here, out of date or missing, with its version, install hint,
  documentation link and, where Kontena can fetch it, its own copy.
- **helm, kustomize and podman are visible for the first time.** They were already known to Kontena —
  the Helm screens drive helm, and kind and minikube can run their nodes on podman — but nothing said
  whether they were installed, so their install hints reached nobody.
- **Local clusters keeps one line:** whether a cluster can be built here, and a button to Tools. No
  second tool list to drift out of step with the first.
- **Node, namespace, workload and service details open over the list instead of replacing it.** They
  used to be a page swap, so reading what one node was doing meant losing the list you were reading it
  from — and coming back rebuilt it, at the top, with your filter gone. The detail now slides in from
  the right over a dimmed list; **Escape**, the ✕ or a click beside it closes it, and the list is
  exactly where you left it. Opening a pod from one of these still leaves for the pod's own page;
  that page follows into the drawer later.
- **Container and pod details open over their list too.** The drawer now holds every detail Kontena
  has: a container's logs, stats and terminal, and a pod's logs, shell and events, all without taking
  away the list you opened them from. Opening a pod from a node, a namespace, a workload or a service
  swaps what the drawer holds rather than leaving the page. **Escape** or the ✕ closes it, and the
  button beside them opens the same detail as a full page when you want the width.
- **The Run dialog no longer offers a restart policy where the engine has none.** Apple's `container`
  runtime cannot restart a container automatically, so on that backend the field is gone rather than
  present and ignored — and the command preview stops showing a flag that does not exist there. Docker,
  Podman and nerdctl are unchanged.
- **Open a secret or config map like anything else in the cluster.** Clicking one used to unfold its
  keys inside the list row and that was all it could ever do. It now opens the same detail every other
  Kubernetes object gets — with the full page and the separate window that come with it — holding the
  keys, the events, the live manifest, and a new **Used by** tab listing the pods that actually read
  it: mounted as a volume, read as environment, or used to pull images. The list row's expander is
  gone, so there is one place a value can appear rather than two. Values still behave exactly as
  before: fetched only when asked for, dropped again when hidden, and copied without ever going on
  screen.
- **The first-run wizard no longer announces Apple `container` as a planned backend.** It is a real one
  now, so the promise row is gone and the runtime shows up like any other engine — detected where it is
  installed, absent where it is not.
- **Opening a cluster and switching namespace are a lot quicker.** The sidebar's counts and the
  overview page used to be fetched one resource at a time, each waiting for the one before it —
  eighteen trips to the cluster in a row before anything appeared. They now go out together, so the
  wait is roughly that of the single slowest one instead of all of them added up. The difference is
  biggest where it was worst: a remote cluster, or a kubeconfig whose credentials are produced by a
  helper command.
- **The cluster overview and the Workloads dashboard follow the cluster.** They were the last two
  pages showing a single snapshot, taken the moment you opened them and kept until you navigated away
  and came back — so the landing page of a cluster was the most out-of-date thing in the app. A node
  going NotReady, a deployment appearing, a pod dying: all of it now shows up while you are looking at
  it, and both pages say so plainly if they ever stop keeping up.
- **Config maps, secrets and events keep up with the cluster.** They were the last three lists that
  froze at the moment you opened them, so a config map someone had just edited, or the event
  explaining what happened next, was already outside the page you were looking at. Events is the one
  this matters most for: you open that feed *because* something is wrong, and a feed that has stopped
  moving reads exactly like a cluster that has settled down. All three now follow the cluster the way
  every other list already did, and say so plainly when they cannot.
- **Pods sits directly under Deployments in the sidebar.** It used to sit at the foot of the Workloads
  section with every other kind — StatefulSets, DaemonSets, Jobs, CronJobs — in between, which is a
  long way from the entry whose pods you were almost certainly after. In a namespace without
  Deployments it stays where it was.
- **The cluster sidebar no longer carries counts.** Filling those numbers meant listing every pod,
  service, config map, secret, event, ingress, claim, volume and storage class in the cluster — before
  every navigation, and again on every change the open page saw. Measured on a 72-pod cluster: 20 MB
  read per round, and the window stopped responding for 150–330 ms of it, every few seconds. Opening a
  cluster is now 2 seconds faster and the app stays responsive while it follows one. The namespace
  picker and the per-kind Workloads submenu still keep up with the cluster; they just no longer put a
  number next to anything.
- **Kontena starts about a third faster.** The window now appears in roughly a second instead of a
  second and a half, and the app is ready to use in 2.3 seconds where it took 3.6. Nothing was
  removed to get there: the build now ships compiled native code beside the app's own, so the first
  run of everything — drawing the window, reading your kubeconfigs, contacting the first backend —
  no longer waits for it to be compiled on your machine. The download is about 14 MB larger for it.
- **The support verdict now sits where the version already is, not only in the switcher.** Knowing
  that a release has been dropped was only useful if you opened the backend dropdown — which is where
  you go to switch between backends, not where you work. Anyone running a single engine could go
  months without seeing it. The sidebar now shows a warning beside the version of the backend you have
  open, and the cluster overview shows one beside the cluster version in its header; both explain
  which line and since when on hover. On the cluster overview this sits alongside the per-node skew
  warning that was already there, deliberately unmerged: skew asks whether a cluster's own parts agree
  with each other, support asks whether anyone still repairs the release it runs, and only one of the
  two can be wrong at a time.
- **A newer patch on a supported release is now shown.** The release calendars always carried it and
  nothing displayed it. A backend that is a few patches behind on a line that is still maintained gets
  a quiet "Update" marker in the switcher naming the newer release — grey, not amber, because being
  behind on patches is worth mentioning and is not a warning. A release its publisher has dropped
  never gets it: suggesting the newest build of something unsupported is advice about the wrong
  problem.
- **The namespace picker in the command bar now keeps its width, and you can type in it to find a
  namespace.** It used to size itself to whichever namespace was selected, so switching from `default`
  to something like `ingress-nginx-controller-system` widened it and shoved the refresh and theme
  buttons along with it — the bar moved every time you switched. It is now as wide as it is, whatever
  is selected, with the full name in a tooltip when it does not fit. Clicking it still opens the whole
  list with "All namespaces" at the top; typing narrows that list to the namespaces containing what you
  typed, anywhere in the name. Text you leave half-typed is thrown away when you click elsewhere: the
  namespace only changes when you actually pick one.
- **Apply manifest shows the whole bundle, however big it is.** A chart like
  `kube-prometheus-stack` renders 5.2 MB across 82,000 lines, and the editor used to show the first
  512 KB of it and say so, because the plain text box it was built on lays out every line it is
  given. It is now a real editor that only lays out the lines on screen: the whole bundle is there,
  it is editable rather than read-only past a size, it has line numbers, and reading it in no longer
  holds up the window — a bundle that size is parsed in the background while the page stays live.
- **A resource's YAML gets the same editor the Apply page has.** Opening the YAML of one object —
  from a detail drawer, the pod page, or the edit dialog — used a plain text box, which lays out
  every line it is given. Most manifests are small; a CRD's own manifest is fourteen thousand lines,
  and that one took seconds to open. It is now the editor from Apply manifest: only the lines on
  screen are laid out, big documents are read in the background instead of holding up the window,
  and there are line numbers to navigate by. A backend that cannot apply still shows the manifest,
  read-only, exactly as before.
- **The sidebar now shows where its nav starts scrolling.** With enough entries the list ran out of room
  above the Activity/Settings block and was simply cut off there, which read as a menu that happened to
  end — so the entries below it went unfound. The nav now fades out into that block, and the fade is only
  visible while there is something underneath it.
- **Opening a cluster page no longer downloads the cluster's workloads to redraw the sidebar.** Every
  navigation asked for every Deployment, StatefulSet, DaemonSet, Job and CronJob in full — cluster-wide
  — to decide which per-kind entries the Workloads submenu has, plus a fresh namespace list for the
  picker. Six of the seven list calls behind a click on Deployments were for the sidebar, and the two
  most expensive of them (Jobs, CronJobs) put nothing on screen at all: on a cluster that runs CronJobs,
  the finished Jobs are the largest list anywhere in the product. The submenu now asks which kinds
  exist rather than which objects, one object per kind, and the namespace picker follows a watch
  instead of being re-read behind every click — so it also notices a new namespace while you are
  standing still, which the old refresh never did. Measured on a 400-Job cluster: a sidebar refresh
  went from 1.3 MB to 14 KB, and a click on Deployments from 2.8 MB to 133 KB.

### Removed

- **SDK: `ResourceEvent.Manifest` is gone.** A watch event now carries what happened and to which
  resource, and nothing else. Nothing ever read the manifest, and filling it cost every adapter a full
  YAML serialisation per event — so it was a bill every adapter paid and no caller could rely on.
  Anything that needs a manifest should read it when it needs it, where the answer is current at the
  moment of asking rather than at the moment of an event.

### Fixed

- **The first-run wizard shows the clusters in your kubeconfig.** With a kubeconfig and no local
  engine it reported "no engines detected" and offered the Podman install guide — sending you off to
  install software you do not need, past clusters that were ready to use. The contexts in your
  kubeconfig are now listed on the wizard itself: tick the ones you want in the switcher and continue,
  engine or no engine. Ticking one off is remembered as an answer, so a cluster you did not want is
  not offered again on every launch. (KON-336)
- **The wizard's Skip and Continue stay put.** They sat at the bottom of a card that grows with
  whatever was found, so a machine with enough to show pushed them off the screen. The card now
  scrolls its own middle and keeps the buttons in view. (KON-336)
- **Skipping the first-run wizard is no longer a decision you are stuck with.** Skipping it left
  Kontena picking the first engine that answered, every launch, without ever asking again — invisible
  with one engine, a silent choice with two. **Set up again**, next to Reconnect on the engine-down
  card, brings the wizard back: Reconnect retries what the app decided for you, this is where you
  decide again. (KON-333)
- **The first-run wizard notices an engine you have just started.** It asks you to start the engine it
  reports as not running, and then could not see you do it: the row stayed grey until Kontena was
  restarted. A **Rescan** probes again in place. (KON-333)
- **The first-run wizard can start a stopped Podman for you.** Kontena already knew the one case with
  a specific answer — Podman installed, its user socket unit never enabled, which is why `podman ps`
  works from a terminal while Kontena finds nothing — but only offered the fix on the engine-down
  card, after the wizard had already been left behind. It is now offered on the wizard itself, with
  the command shown and copyable for anyone who would rather run it themselves, and the engine list
  refreshed in place afterwards. (KON-335)
- **A remote engine on Settings › Engines now points at where you can edit or remove it.** It appears
  twice on that page: once in the detected-engines inventory at the top, and once under Remote
  engines further down, which is the one carrying Edit and Remove. Clicking the first one and finding
  no way to remove it read as "removing a remote is impossible". The inventory row now has a "Manage
  below" link that scrolls to its own entry and marks it briefly. The inventory itself stays
  action-free — you do not remove Docker from a list of what was detected.
- **The in-app updater no longer hits GitHub's anonymous API limit.** Checking for updates used to
  ask `api.github.com` for the release list, which anonymously allows only 60 requests an hour per
  IP — shared or corporate networks could run out of budget and see "could not check for updates"
  for no reason a user could see. It now reads the same release feed straight off the release's own
  assets, on every channel (stable, preview, nightly), with no request limit at all.
- **A visible window edge in non-maximized mode.** The app window's border could disappear into the
  desktop background depending on your compositor, making it hard to tell where it ends. It now
  always draws a thin edge — dark on light theme, light on dark theme.
- **Settings no longer waits on an engine that isn't installed.** Kontena checks Docker and Podman
  whether or not you have them, and on Windows an engine that isn't there could take seconds to say
  so — seconds you spent looking at a Settings page that hadn't caught up with the change you just
  made, or at a launch screen. Each check now gives up after two seconds and reports the backend as
  not connected, which is what the slow answer was going to say anyway.
- **A loading indicator while a cluster list fetches.** A large cluster's Pods, Services, Workloads and
  other lists could take a real, visible while to load, and the page gave no sign anything was
  happening. It now shows a thin progress bar for the duration of the fetch.
- **Stop a port forward from where you started it.** The pod and service detail pages now show a Stop
  button once a forward is running, instead of only on the Port forwards page.
- **Restarting a workload no longer closes its detail drawer.** Clicking Restart from a workload's
  own detail page used to rebuild the whole page under it, closing the drawer you clicked it from.
  It now refreshes just the pods tab in place.
- **Back now unwinds nested detail drawers correctly.** Opening a Pod from inside a Deployment's
  detail (or any other detail-to-detail hop) used to mean the mouse/keyboard Back button skipped
  past the Deployment entirely, landing wherever you were before opening the Deployments list at
  all. It now closes back to the Deployment first, same as the ✕ and Escape.
- **A remote engine is no longer cut off before it can answer.** Every backend got the same two
  seconds to respond to a connection check — right for a socket on this machine, impossible for a host
  across a network, where the connection and its authentication alone cost longer than that. The result
  was Kontena contradicting itself on one screen: Test connection in Settings said "Connected" about
  the exact engine the switcher listed as unreachable and refused to open. Each backend now sets its
  own deadline, and remote engines get ten seconds — the budget their connection was always working to.
- **Retry a backend that didn't connect, without restarting Kontena.** A backend was checked once at
  launch and never again, so an engine that was still starting — or a remote waiting on you to approve
  a key in your SSH agent — stayed dead for the rest of the session, with a row in the switcher that
  did nothing when clicked. Clicking an unreachable backend now asks it again and opens it if it
  answers, and Settings › Engines has a Retry on every detected engine that is down and a Retry/Test on
  every saved remote — so a stored connection can be tried without going through Edit and Save.
- **A cluster in the cloud no longer shows up as "Not connected" just because it answered slowly.**
  Kube-contexts used to get the two seconds meant for a local socket, which a managed cluster in some
  region — often with an auth plugin that has to start first — routinely misses. They now get ten,
  the same as a remote engine. Clusters running on your own machine (kind, k3d, minikube) keep the
  short deadline, so a stopped local cluster still does not hold up the rest of the list.
- **The counts in the sidebar keep up with the lists beside them.** A deployment applied with
  `kubectl`, or a container started from the terminal, already appeared in the list on screen — but
  the number on the sidebar entry next to it stayed at the old total until you switched namespace or
  backend. Both now follow the same stream the list does, so the row and the count no longer
  contradict each other.
- **The namespace picker keeps up with the cluster.** It was filled once, when you opened the
  cluster, so a namespace created after that could not be selected at all — even though the list
  right next to it was already showing what was in there. It now follows the cluster, and if the
  namespace you had filtered on is deleted, the filter falls back to All namespaces instead of
  leaving you on an empty screen with no visible reason for it.
- **Jobs and CronJobs no longer claim the cluster hung up on them.** Both pages said *"The cluster
  closed the update stream"* the moment you opened them, on every cluster. Nothing had closed
  anything: Kontena was asking for a kind it had never taught itself to follow, and then reporting its
  own gap as the cluster's fault. They follow the cluster properly now, like the other lists.
- **The macOS build no longer arrives damaged.** Kontena's app bundle shipped with a signature that
  promised a seal it did not have, which macOS reports as *"Kontena is damaged and can't be opened"* —
  the one refusal there is no way to click past. The bundle is now sealed, so both the installer and the
  portable build behave like any other app from an unidentified developer: open it once through
  right-click → Open, or clear quarantine first.
- **macOS now identifies Kontena as `app.kontena.Kontena`**, the domain the app already used for your
  keychain entries and its login item, instead of a made-up `com.Lionear.Kontena`. If you had an older
  macOS build running, macOS treats this as a new app: grant it permissions again, and check Login Items
  if you had start-on-login switched on.
- The macOS line in the release notes was pointing at the wrong file. Clearing quarantine after running
  the .pkg cannot help, because macOS checks the installer package before it unpacks anything — so the
  notes now say to clear it on what you actually downloaded.
- **Clusters you untick in the setup wizard now stay unticked.** Probing again — including the rescan
  the wizard does for you after starting an engine — used to rebuild the screen from your saved
  settings, and since nothing is saved until you press Continue, every cluster came back ticked. Untick
  two of four, let Kontena start Podman for you, and all four ended up in the switcher. Skipping the
  wizard is also honest again: it means "not now", and the next launch no longer reads it as "yes, add
  every cluster in my kubeconfig". Upgrades still keep the clusters they had.
- **Kontena now starts Apple's `container` service itself instead of telling you to.** With the
  apiserver stopped, every command failed with an XPC connection error and the CLI's advice to go and
  run `container system start` in a terminal — a step Kontena had no reason to leave to you. It now
  runs that command when it recognises the stopped service and tries the failed command once more.
  There is no password to type: the service lives in your own launchd domain and starting one that is
  already running does nothing. Kontena declines the offer to install a default kernel rather than
  downloading one behind your back, so on a machine that has never had one the start still fails —
  and then the CLI's own message, manual command included, reaches you exactly as before. It is tried
  once, never in a loop.
- **An open cluster page no longer freezes the window every time the cluster moves.** A live page
  follows several kinds at once, and the events from all of them were being unpacked on the same
  thread that draws the window — in runs, because they arrive in whatever the connection buffered. On
  a 72-pod cluster that froze the window for 150–220 ms at a time, several times a minute, while
  nothing on screen was changing. Unpacking now happens off the drawing thread, and the redraw it
  leads to is unchanged: still one per settled burst, still on the thread that owns what it rebuilds.
- **Following a cluster reads less of it.** Every watch event used to be handed a full YAML copy of
  the object that moved — built for each event, on every kind every open page follows, and never read
  by anything. And the landing page asked the cluster for its name, version and distribution on every
  reload, which on Kubernetes is a `/version` call plus a full node listing, for a heading that was
  already on screen. Node usage and node disk figures now come from one round of kubelet requests
  instead of two identical ones back to back. Together: a third fewer requests per refresh and a third
  less memory churned, on a page that refreshes itself every one to five seconds.
- **On macOS, Kontena is called Kontena in the menu bar.** It used to say "Avalonia Application" next
  to the Apple logo, and in **About …**, **Hide …** and **Quit …** — the one place the app's name is
  not taken from the bundle.
- **One unreachable backend no longer holds up the whole app.** An engine or cluster on a network you
  are not on — a work VPN you left, a host that is off — takes its full deadline to give up, and
  startup waited for every backend before showing you anything. With one such engine configured,
  Kontena took 13 seconds to become usable; it now takes 5, and the backend you are actually opening
  is waited for as long as it needs. Whatever was still out joins the switcher when it answers.
- **Private registries work again when Kontena is started from the Dock on macOS.** Docker keeps your
  registry logins in a credential helper, and Kontena looked for that helper only where the shell
  would — which a program launched from Finder or the Dock does not inherit. Every pull from a private
  registry went out anonymous and came back looking like a rejected password. Kontena now looks in the
  places those helpers are actually installed.
- **Kontena connects to the Docker engine `DOCKER_HOST` points at.** It used to say Docker was
  installed on the strength of that variable and then talk to `/var/run/docker.sock` regardless — so
  Colima, OrbStack, Rancher Desktop, and a Docker Desktop whose socket prompt you declined all showed
  up as an engine that could not be reached. A value Kontena cannot connect to now says which ones it
  can, instead of quietly using a different engine than the one you named; for `ssh://` it points you
  at adding a remote engine, which tunnels the connection for you.
- **The macOS build reports its version the way macOS expects.** Finder and the About panel showed a
  four-part number like `0.4.0.0`, one component more than Apple's format allows. They now show
  `0.4.0`, with the full build version — pre-release suffix and all — kept where it belongs.
- **Kontena can go full screen on macOS.** It could not before: Kontena draws its own title bar, which
  takes the green button with it, and that button is what macOS routes ⌃⌘F through — so neither the
  mouse nor the keyboard had a way in, and neither did Split View or Stage Manager. The button between
  minimise and close now enters and leaves full screen, and ⌃⌘F does the same.
- **A full-screen shortcut on Windows and Linux.** `F11` fills the screen with Kontena and puts it
  back. Rebindable in Settings, like every other shortcut.
- **A TLS Docker endpoint with a `ca.pem` connects again.** Kontena read the CA file with the loader that also expects a private key inside it, so every standard `DOCKER_CERT_PATH` directory — where `ca.pem` is a certificate and nothing else — failed on the way in, with a `CryptographicException` about key contents that had nothing to do with your key.
- **Migrating a container to Apple `container` works.** Two things stopped it. A container's network
  came along by name, and the name of Docker's default network — `bridge` — means nothing to another
  engine, so the migration failed with "network bridge not found"; a network the target does not have
  is now dropped with a line saying so, and the container lands on the target's own default network.
  And an image with no build for your Mac's processor, such as SQL Server, was created for the Mac's
  own architecture and refused with "platform linux/arm64"; Kontena now asks for the architecture the
  image actually carries, which runs it emulated. That second fix applies to every container you
  create on Apple `container`, not only migrated ones.
- **A migrated container keeps its published ports, even when it was stopped.** The ports came from
  the container list, which reports what an engine has bound right now — nothing at all for a
  container that is not running. A stopped container therefore arrived on the other engine with no
  ports published and nothing saying so. They now come from the container's own configuration, which
  says what it was created to publish whether it runs or not.
- **A clearer icon for migrating a container.** The button used the same icon as an external link.
- **Switching to another update channel works in both directions.** Moving from preview to nightly —
  or back to stable — did nothing: the updater kept reporting that you were on the newest release. The
  channel is part of the version number, and `0.4.0-nightly.…` sorts *below* `0.4.0-preview.…`, so the
  updater read a deliberate switch as a downgrade and refused it. Reinstalling was the only way out.
  A channel you pick yourself is now followed wherever it leads, and the card says you are switching to
  it rather than claiming a newer version. A feed rolling backwards on your *own* channel is still
  refused.
- **Sorting a list no longer duplicates its rows.** Clicking a column header on a cluster list turned
  three rows into five, a second click into seven, and nothing brought them back — not a refresh, not
  sorting again. The list could add, remove and replace a row but not move one, so a sort put each row
  at its new position and left the old copy standing below it. Rows are now moved into place, which
  also keeps them the same rows: sorting no longer loses your selection or your scroll position.
- **Opening a Kubernetes cluster does a third of the work it used to.** The shell filled the namespace
  picker, built the landing page, and only then selected a namespace — and selecting one reads the
  cluster's workload kinds and rebuilds the page, because which page Workloads is depends on them. So
  every cluster you opened listed its namespaces six times and built the overview twice, throwing the
  first one away along with the seven watch streams it had just opened. It now reads what a page is
  built from before building one: two namespace lists instead of six, one overview instead of two.
- **The cluster says when it is fetching, everywhere it fetches.** The Overview, the Workloads
  dashboard, Config maps and Secrets all read the cluster on arrival and gave no sign of it — an
  unfinished read draws as a cluster with no nodes, no pods and nothing needing your attention, which
  is indistinguishable from the real thing. They now show the same thin progress bar the other lists
  do. Picking a namespace gets one too: that reads the cluster before it can build the page, so until
  now the click looked ignored rather than slow. And the wait while a cluster opens no longer calls
  itself "Connecting to your container engine…" — it names what is actually being opened.
- **Acting on a cluster list no longer throws away the search that found the row.** Searching
  Deployments down to a single result and then clicking Restart or Scale rebuilt the page with an
  empty search box, so the list you were working in came back showing everything. The term now
  survives any action that reloads the page it was fired from — restarting, scaling, deleting,
  draining a node, editing a manifest — on every cluster page. Navigating somewhere else still
  clears it, as before.
- **Charts that ship their own CRDs now apply.** Applying
  `prometheus/kube-prometheus-stack` with **Include CRDs** reported eighty failures out of a hundred
  and thirty-two resources: the chart renders ten CRDs together with the fifty `PrometheusRule`,
  `ServiceMonitor` and operator resources that use them, and a dry-run creates neither — so the
  cluster had never heard of those kinds when it was asked to check them. Namespaces and CRDs are now
  applied ahead of the rest of the bundle, and Kontena waits for the API server to start serving the
  new kinds before it continues. Resources a preview genuinely cannot reach are listed as **not
  previewed**, with the reason, instead of counted as failures — so the plan is honest and **Apply**
  is no longer blocked by them.

- **A chart's `# Source:` comments are no longer read as broken manifests.** Ten of
  kube-prometheus-stack's resources failed with "the document is empty" because `helm template`
  writes a comment header for each file it renders, and a `---` can leave one stranded on its own.

- **A big render no longer freezes the editor.** kube-prometheus-stack renders 5.2 MB across
  82,000 lines, four fifths of it CRD schema, and handing all of it to the editor stalled the window
  for over six seconds. Per-resource diffs stop at 400 lines with a count of what was left out,
  instead of rendering a fourteen-thousand-line CRD schema nobody reads.
- **Apply manifest says what it is doing while it does it.** A dry-run or an apply showed nothing at
  all until every document had an outcome, so a chart's worth of resources was a page that looked
  hung: over a second reading the bundle, a hundred round trips, and up to thirty seconds waiting for
  its own CRDs to be served — in silence. It now counts through the bundle ("Applying 47 of 132") and
  names what it is waiting for ("Waiting for the cluster to serve ServiceMonitor (12/30s)"). Reading
  the bundle moved off the UI thread as well; that part really was a frozen window, not just an
  uninformative one.
- **Activity no longer appears on a cluster, where it did nothing.** It replays a container engine's
  event stream, and a cluster has none to give it, so the entry opened a page that stayed empty for the
  whole session. On a cluster, System → Events answers the same question about the same cluster; the
  entry is back the moment a container engine is open again.
- **A cluster's overview stops re-reading the whole cluster.** Its five tiles are integers, and four
  of them were read by fetching every pod, workload, service and namespace on the cluster and calling
  `.Count` — on every settled watch burst, which on a busy cluster never stops arriving. The pods were
  fetched twice per redraw, the second time for a per-node column this page does not even draw. The
  tiles now ask the API server for the number instead of the objects, the node table no longer pays
  for the pod counts it does not show, and a redraw that costs more than the window it settles for
  waits that long before the next one. Measured on a 2,000-pod cluster with `KONTENA_TRACE=1`: the
  window froze 33 times in a minute, up to 900 ms at a time, and now does not freeze at all — one
  redraw costs 25 ms where the page used to be reloading more or less continuously.
- **"Install with Helm" on Alerts now adds the chart's repository too.** The hand-off filled the apply
  page in with `prometheus-community/kube-prometheus-stack` and stopped there, so the render failed on a
  chart helm had never heard of and you had to find the repository URL yourself before the offer was
  usable. It now adds `prometheus-community` along with the chart — harmless if you already have it —
  and if that add fails, the page says so instead of leaving you with a reference that will not resolve.
- **A plugin can be uninstalled or updated on Windows without closing Kontena first.** Loading a
  plugin kept its assembly file open for as long as the app ran, and Windows refuses to delete or
  replace a file that is open — so removing a plugin, or dropping a newer build over it, failed with
  "access denied" until Kontena was shut down. The loader now reads the assembly into memory instead
  of holding the file, on every platform. Linux and macOS never showed the problem, because they
  allow an open file to be deleted.
- **Custom resources on Resources now show their YAML.** Opening one used to answer with a placeholder
  — "Dragonfly is not a kind this adapter can read yet" — because the manifest panel could only read the
  dozen kinds Kontena models by hand, while the list it was opened from is generic. It now reads any kind
  the cluster serves, rendered by the API server itself, so a custom resource shows the same YAML as
  `kubectl get -o yaml` and a newly installed operator's kinds work without waiting for Kontena to learn
  about them.

### Security

- **Allowing a plugin now allows that build of it, not just its name.** Consent was recorded as `<id>@<version>`, so a dll swapped in the plugins folder inherited the answer given for the one it replaced and ran inside Kontena with your access, unasked. The assembly's SHA-256 is recorded with the answer and checked on every scan, and a plugin whose code changed since you allowed it is asked about again — with a prompt that says so, rather than one that reads as if it were new. Plugins allowed by an older version of Kontena are asked about once more, because there is no record of which bytes those answers were about.
- **Updates now have to be signed by us, not just served over HTTPS.** Kontena checked that an update
  came from github.com and nothing more, so anyone who could write a file to a release could have had
  an ordinary-looking update card install their code. Every release now carries a signature that only
  our release key can produce, and your copy checks it before it downloads anything — on Windows,
  macOS and Linux alike. An update that does not verify is refused and nothing on the machine changes.
  The downloads on the releases page are still unsigned as far as Windows and macOS are concerned;
  that needs a certificate from each of them and is tracked separately.
- **The SSH tunnel no longer puts its socket in a directory other users on the machine can reach.** Without `XDG_RUNTIME_DIR` — a headless session, cron, some sandboxes — the local end of the forward used to land in a fixed `/tmp/kontena` created with the default permissions, which any other user of the machine could have created first and owned. That socket carries a remote host's Docker API, so it is worth more than it looks. Kontena now makes a directory of its own for the run, readable only by you. The staging directory a container migration copies volume contents through is owner-only for the same reason.
- **Adding a kubeconfig says so when connecting would run a program on your machine.** A context can
  fetch its login token by starting a command — `gke-gcloud-auth-plugin`, `aws eks get-token`, or
  anything else the file names — and the wizard used to start it the moment the file was read, as part
  of checking which contexts are reachable. That is the ordinary path for EKS and GKE, but a kubeconfig
  is not always your own: they get forwarded, pasted out of a ticket, pulled from a repo, and "I am
  adding a cluster to have a look" is not an action anyone expects code execution behind. The command
  and its arguments are now shown before the first connection, and nothing reaches that context — not
  the reachability check, not the connection test — until you say to run it. The other contexts in the
  same file are unaffected, so this is a question rather than a wall. Your answer is remembered per
  command, the way plugin consent is: the same context naming a different command asks again.
- **Pointing Kontena at a Docker daemon's `ca.pem` now actually pins that CA.** The check returned early whenever the platform was already happy with the server's certificate, which is a different and weaker claim — a certificate for that host from any CA your machine happens to trust would have passed, and ruling exactly that out is why you name your own CA. Every certificate now goes through the chain built against `ca.pem`.

## [0.3.0] - 2026-08-01

### Added

- **Settings › Local clusters tells you what is needed to build a cluster here.** Kontena drives kind
  or minikube to create a Kubernetes cluster on your own machine, and neither ships with Kontena. The
  new page says which of them is installed and at what version, and offers the one thing that will
  actually get you the missing one: on a machine with a package manager, the install command — run for
  you, with that package manager's own output in view, or copied if you would rather do it yourself.
  A version too old to work with is called out for what it costs rather than simply refused, so you
  can carry on if it suits you.
- **Where no package manager can help, Kontena can fetch the tool itself.** That path is deliberately
  narrow: the download comes from the publisher's own release, is checked against their published
  SHA-256 when it arrives **and again before every run**, lands in one folder of Kontena's own, and is
  never added to your PATH. A copy that does not match its checksum is discarded rather than kept, and
  the page is explicit that these copies are Kontena's to update because no package manager is
  watching them. (KON-109)
- **A diagnosis block that says why a container or pod is not running.** Crash loops, images that
  cannot be pulled, OOM kills, pods no node will take and failing probes are now explained in one
  place, in plain language, with the exit codes, limits and the cluster's own message printed
  underneath so the conclusion can be checked. Each explanation ends where it points: the previous
  run's logs, the events, or the manifest. Nothing recognisable, no block — it never guesses.
- **The logs of the previous run.** A pod-detail **Previous run** toggle shows the output of the run
  that ended instead of the one starting, which on a crash-looping container is the only place the
  reason still exists. It appears only where there was an earlier run.
- **A container that never started is explained too.** A command that is not in the image leaves the
  container *Created* with no exit code and no log — the runtime's own message is now shown, with a
  link to the command it tried to run.
- **Kontena now says when a newer release of kind or minikube exists — and can take one over.** It
  used to only complain about a tool that was too old to work with, which meant a perfectly usable but
  years-old kind read as fine. Now the tooling page carries a quiet line when the publisher has moved
  on: a line, never a colour, because a tool one release behind still does its job. The answer is
  looked up in the background and kept for a day, so opening the page never waits on the network and
  offline it simply says nothing.
- **"Let Kontena manage it" makes Kontena's copy the one that runs**, even when the tool is also
  installed by a package manager. Until now a system install always won, so fetching a newer kind
  changed nothing at all — the download sat there unused. It stays your choice and it is never made
  for you: the row always says which copy is in use, and handing it back is one click. Giving it back
  is not a delete; the copy stays where it is.
- **Compose projects now sit under one row in the Containers list.** A stack used to arrive as three
  or four unrelated rows scattered through the list, and you had to know the naming convention to see
  that they belonged together — which is how the report that started this reads. A project starts as
  a single closed row that says how much of the stack is up (`3 of 4 running`) and carries the
  project's own start, stop, restart and down; open it when you want the containers themselves. It
  deliberately shows no image, ports or CPU: a sum of four containers' CPU is either a lie or
  meaningless.
- **Searching reaches inside a collapsed project.** A match on a container opens the group it lives
  in, so a hit is never hidden behind a chevron, and clearing the search puts every group back the way
  you left it. Searching the project's own name shows the whole stack.
- **Grouping can be turned off**, from the button in the list header, and the choice is remembered per
  backend — a machine full of stacks and one with none are different rooms. The Projects page is
  unchanged and still the place to operate a project as a whole; the group row links to it.
- **Workloads and Services have a detail page, and it can reach their pods.** Both were dead ends: a
  workload row offered Scale and Restart, a service row offered Port forward, and neither could be
  opened. Getting from a Deployment to the pods it controls meant going to the Pods list and filtering
  by hand. Both now open onto a page carrying what the object is, the pods that belong to it, its
  recent events and its manifest — and each pod in that list opens its own detail, with Back returning
  to where you came from rather than to a list you never opened. (KON-166, KON-167)
- **The workload page shows the replica breakdown rather than one fraction.** Desired, ready, up to
  date and available are four different numbers, and "3/3" cannot tell you that three pods are ready
  while only two carry the current revision — which is the difference between a finished rollout and a
  stuck one. Labels, selector and update strategy are there too, and a CronJob shows its schedule
  instead of replicas it does not have. (KON-166)
- **The service page answers "why is nothing arriving".** The full port table — name, port, target
  port, node port, protocol — rather than the one column the list had room for, the selector, and the
  pods that selector reaches right now, with a count of how many of them are actually ready. An empty
  result says which kind of empty it is: a selector matching nothing, a service with no selector at
  all, a workload scaled to zero, or a CronJob, whose pods belong to the Jobs it creates and never to
  the CronJob itself. (KON-167)
- **Init containers are visible, and their logs are reachable.** Kontena showed a pod's app containers
  and nothing else, so a pod held up by an init container offered no way to look at the one container
  that could explain it. Init containers now appear in the pod's container list, marked as such and in
  the order they run, and the log and shell picker offers them like any other. Opening a pod that is
  stuck selects the container doing the sticking rather than the first in the list. A finished init
  container is described as completed rather than "not ready" — finishing is the whole of its job — and
  its shell says why it cannot be opened instead of offering a terminal that attaches to nothing.
  Ephemeral debug containers come along on the same footing. (KON-168)
- **A pod working through its init containers says so.** The status column read "Pending" both for a
  pod that had just been scheduled and for one wedged on its first init container. It now reads
  `Init:1/2` while they progress and `Init:CrashLoopBackOff` when they are not going to, the same way
  `kubectl` puts it. Restart counts include init containers too; a pod that has retried its migration
  four times no longer reports zero. (KON-168)
- **A terminal on this machine, already pointed at the cluster you are looking at.** Cluster mode gets a
  Terminal page that opens your own shell with the right kubecontext already set and `k` aliased to
  `kubectl`, so the first command can be the one you meant instead of `kubectl config use-context`. The
  namespace follows the picker, so the shell starts where the rest of the window is looking. Your
  kubeconfig is not touched and nothing is copied out of it: Kontena writes a one-line overlay naming
  the context and puts it in front of the files already in play through `KUBECONFIG`, which leaves every
  other shell you have open exactly as it was — and takes the overlay with it when the window closes.
  The shell is a real pseudo-terminal rather than a pair of pipes, which is the difference between a
  terminal and something that looks like one: prompt, history, colour, `Ctrl-C` and resize all work,
  and every line of output starts at the left margin instead of where the previous one happened to end.
  The shell keeps running when you go and look at something else: leaving the page and coming back finds
  it where you left it, screen and all, so a build or a `kubectl logs -f` survives a detour through the
  pod list. Each cluster has its own, Reconnect gives you a fresh one, and a shell you exit is not handed
  back. Your own startup files are kept — the generated rcfile sources them first, so the prompt and aliases
  you are used to are still there. bash, sh, zsh, fish, PowerShell and cmd each get the alias the way
  that shell actually supports; a shell Kontena does not recognise opens without one rather than with a
  guessed flag that would stop it opening at all.
- **The keyboard does something now.** There was not one shortcut in Kontena before this, which meant
  a modal could only be dismissed by finding its Cancel button. Escape closes the open dialog, Enter
  runs its primary action where it has one, `Ctrl`/`Cmd`+`F` puts the cursor in the search box and
  `Ctrl`/`Cmd`+`R` reloads the page. Both modifiers are accepted everywhere, so the one your hands
  reach for is the one that works. Where a page has nothing to search, the shortcut for it declines
  rather than focusing a box that takes nothing. (KON-172)
- **Enter is offered only where there is one obvious answer.** A confirmation has a single primary
  button and no place to type, so Enter presses it; a dialog with a text area or several equal
  buttons is left alone rather than guessing. Holding Enter on a confirmation still only fires it
  once. (KON-172)
- **Back goes back.** There were four back buttons before this and none of them was navigation: each
  jumped to one fixed place, so a pod opened from a workload only returned to that workload because
  someone had wired that particular route by hand — and Settings, Activity and About had no way back
  at all. There is now a single Back in the command bar that returns to wherever you actually came
  from, naming it in its tooltip, along with `Alt`+`←` and the mouse's back button. The four
  page-level arrows are gone: two identical arrows above one another read as a mistake, and the one
  in the command bar is always in the same place. It works from the first navigation of a session,
  including on a cluster. (KON-173, KON-263)
- **Back cannot lead somewhere that no longer exists.** Deleting a pod removes it from the trail, so
  Back never lands on the page of something you have just thrown away. Switching backend clears the
  trail entirely — a different engine or cluster is a different set of pages, not a previous one.
  (KON-173)
- **Workloads opens on a summary instead of a list.** Since the sidebar split Workloads per kind,
  clicking it opened the group rather than loading every kind at once — which left the page beside it
  with nothing on it. It now shows a card per kind, and each card is the way into that kind: the same
  place the sidebar's sub-entry goes, drawn as content. No card carries a bare number; each one sits
  next to the split between what is running, what is rolling out and what is failing, because "3
  Deployments" is a figure the sidebar already gave you and it does not say whether any of them work.
  Where a cluster runs only one kind there is no summary and no submenu — the list is still the most
  the page could say. (KON-174)
- **When something is wrong, the page says so first, and says why.** A short line above the cards
  reports how many workloads are not running as intended and how long the worst has been that way,
  and a table below names them with the actual reason — the pod in CrashLoopBackOff, the init
  container that will not finish, the image that cannot be pulled, or that no pods were created at
  all. That is the reading the status column cannot give: "Degraded" is the fact you already had.
  When everything is at its desired count there is no banner at all, deliberately not a green one — a
  bar that says all is well is a line you learn to skip, and once you skip it you skip the red one
  too. (KON-174)
- **Nodes have a detail page.** Everything the card had no room for: every condition the kubelet
  reports rather than only the failing ones, the OS image, the internal address, allocatable capacity
  against what is in use, and the pods running there — from every namespace, because that is where a
  node's pods actually are. Cordon and drain are on it, and the cordoned state is stated in full
  instead of compressed into one word that cannot tell a node someone is working on from one that was
  forgotten.
- **Namespaces have a detail page.** What is in it, by kind, each a way through to the list scoped to
  that namespace — and a kind with nothing in it is not offered as a link, because clicking through to
  an empty page is a promise the row already answered. Config maps and secrets are counted with the
  rest, so a namespace holding nothing but credentials is not mistaken for an empty one — while what
  Kubernetes puts in every namespace by itself, `kube-root-ca.crt` and service-account tokens, is
  shown but left out of that judgement, since counting it would mean no namespace ever is empty. A
  namespace with nothing in it at all says so outright: that is the answer to "can this go", and a
  column of zeroes makes you count them yourself. One whose contents Kontena is not allowed to read
  says nothing rather than claiming to be empty. A namespace stuck in Terminating says what that
  usually means. (KON-197, KON-275)
- **More than one terminal per cluster.** The Terminal page now opens as many shells as you want, each on
  its own tab, each kept running while you are somewhere else. A tab strip appears from the second one
  on — with a single terminal it would only repeat what the page title says. Closing a tab ends its
  shell, because a terminal with no tab is one nobody can reach again, and closing the third of four
  leaves you on its neighbour rather than jumping back to the first. A new terminal opens on whatever the
  namespace picker says at that moment, so two tabs can sit in different namespaces; each names its own
  rather than the picker's, since the namespace of a running shell was fixed when it started. The
  sidebar shows how many are open once there is more than one.
- **A terminal can move into a window of its own.** *Open in window* lifts the selected terminal out of
  the app and puts it beside it, with Kontena's own title bar rather than the window manager's, so it
  still looks like part of the app. The shell is not restarted or handed over — it is the same session
  with a different view attached, so whatever was running keeps running. Closing that window puts the
  terminal back on the Terminal page, whichever way you close it: navigating away has never ended a
  shell, and a window button that quietly killed a running build would be the one exception nobody
  expects. Closing its tab instead takes the window with it. While a terminal is in its own window the
  page says so in its place, with a way to bring it back — it moves rather than mirrors, because two
  views of one terminal would have to agree on a size the window and the page do not share.
- **Ingresses have a page.** What is reachable from outside the cluster, through which ingress class,
  and at which address — with every host and path rule, and the service each one routes to, on hover.
  Hosts covered by TLS are named rather than reduced to a tick, because an ingress serving three hosts
  with one certificate between them is exactly the case that a tick would hide. An ingress with no
  routing rules at all is called out instead of shown as a blank cell: that is a real mistake, and
  nothing else on the row would say so.
- **Volume claims have a page.** Name, status, the volume it bound to, capacity, storage class and
  access modes. A claim still Pending is the reason a pod will not start, so it says so and points at
  the storage class — the field that answers it, in almost every case. Capacity is stated the way
  Kubernetes states it: a claim asking for `20Gi` reads as `20Gi`, so the column agrees with the
  `kubectl get pvc` it is likely to be read beside. (KON-247)
- **Events have a page of their own.** Everything that happened in the selected namespace, newest
  first, with the reason, the object, the message in the reporter's own words, and how many times it
  fired. Until now these were only readable from inside a pod or an object — which meant you could
  only find an event once you had already found the thing that was broken, and that is the wrong way
  round for the question this data answers.
- **The object an event is about is a way in, not just a name.** Where Kontena has a page for that
  kind, the name opens it. Events outlive the objects they describe, so a pod that has since been
  replaced says so plainly rather than leaving a link that does nothing.
- **A "warnings only" toggle, off by default.** Hiding the normal events also hides the rollout that
  completed right after the warning, and that sequence is often the whole answer. The sidebar carries
  the number of warnings and nothing when there are none — a badge counting every event would be lit
  on every cluster, always, and would mean nothing. (KON-248)
- **Config maps and secrets have pages.** Both were browsable before through the generic resource
  browser, as raw YAML — which for a secret means base64: unreadable and fully exposed at the same
  time, the worst of both. These pages list the keys and their sizes, and open to show them.
- **A secret value is never on screen unless you asked for it, and does not stay there.** Listing
  secrets carries their keys and sizes and nothing else. Show fetches one value, from the cluster,
  at that moment. Hide drops it — pressing Show again asks the cluster again, because a value kept
  in memory after being shown once is the exact state this page exists to avoid.
- **Base64 is decoded, because base64 is transport and not protection.** Showing you the encoded
  form would only be asking you to decode it yourself. Values that are not text — a TLS key, a
  keystore — are not drawn as characters at all; they report their size and copy as base64, the form
  every other tool takes them back in.
- **Copying and revealing are separate.** A password goes into a terminal far more often than onto a
  screen someone else can see, so Copy fetches the value straight to the clipboard without ever
  putting it on the page.
- **Config maps have none of this, deliberately.** They hold nothing worth hiding, and asking someone
  to press Show on a `LOG_LEVEL` of `info` teaches them to press it without reading — which is the
  habit the secrets page depends on them not having. (KON-249)
- **The cluster lists follow the cluster.** Pods, services, per-kind workloads, nodes, namespaces,
  ingresses, volume claims, volumes and storage classes were all list-plus-refresh before: never
  wrong, always old — and old exactly while a rollout is happening, which is when you are looking at
  them. Now a change in the cluster redraws the list on its own, without the scroll jumping or the
  page flickering: a row that did not change is left exactly where it was, and only the row that
  actually changed is redrawn. (KON-250, KON-277)
- **A list that has stopped following says so.** A cluster that does not support watching, a stream
  the apiserver closed, a watch that failed — each says which, in a line above the table. A list that
  quietly stops moving looks exactly like a cluster where nothing is happening, and those two want
  opposite reactions from you.
- **The all-workloads page still updates on refresh, and says why.** It shows five kinds at once and
  there is no single thing to follow; a single kind's page follows the cluster on its own. (KON-250)
- **Nodes can be taken out of service and put back.** Cordon stops new pods landing on a node and
  leaves the running ones alone; the same button reads Uncordon on a node that already is. Cordoning
  asks first and uncordoning does not — they are not opposites in what they risk, and a confirm on
  the harmless one is what teaches people to dismiss the other without reading.
- **Drain moves the work off, and says what it could not move.** It cordons first, so nothing new
  lands while it runs, then asks the cluster to evict each pod — through the eviction API, which is
  what consults PodDisruptionBudgets. A budget refusing is reported as a refusal, naming the budget
  in the cluster's own words, because that is a true statement about your rules rather than a failure
  of the drain.
- **What it leaves alone, it says so about.** Pods managed by a DaemonSet, static pods whose
  definition is a file on that node, pods nothing owns, and pods holding local scratch storage whose
  contents would be lost. The last of those can be included, as its own separate question — it is the
  only thing on that dialog that destroys anything.
- **A drain that stops part way rolls nothing back.** The node stays cordoned, which is the safe
  place for it to be, and the summary says so rather than leaving you to notice a week later that the
  cluster has been running one node short. (KON-251)
- **The manifest of an object you are looking at can be edited and applied.** Kontena could already
  fetch a live manifest and apply one, and the two were only ever a single act on pod detail. Now
  every detail page's YAML tab is an editor — and config maps and secrets, which have no detail page,
  get the same editor as a dialog from their row.
- **Check before you apply.** A server-side dry-run reports what the change *would* do, in the future
  tense, using the apiserver's own validation rather than a guess. It is a button rather than an
  automatic step, because a check that always happens is a check nobody reads.
- **YAML rather than a field editor, most deliberately for secrets.** An editor that base64-encodes
  behind your back is pleasanter and riskier: you cannot see what is actually going to the cluster.
  The manifest is what the cluster stores.
- **After applying, the editor shows what the cluster now holds** — not what you typed. Defaulting,
  admission webhooks and other controllers all get a say, and those are not the same thing. (KON-252)
- **Config maps and secrets can be deleted.** Always behind a confirm, and the confirm says the part
  that is easy to misjudge: nothing breaks now, because a running pod keeps the values it started
  with — but the next pod that tries to mount it will not start, which may be days later and will not
  look connected to this by then. It also says plainly that Kontena keeps no copy. (KON-253)
- **Volumes and storage classes have pages, and the three storage screens point at each other.** A
  bound claim names a volume that could not be looked at anywhere; now the claim links to it, the
  volume links back, and both link to the class that provisions them. That is the point of these
  screens — two more lists on their own would be worth far less.
- **A volume that is Released with a Retain policy says what that costs.** Its claim is gone and its
  data was kept, nothing will bind to it again, and it is storage you are still paying for. Every
  other phase either resolves itself or is already being looked at; this one waits quietly.
- **Storage classes answer the question the claims page raises.** A Pending claim points at its
  class, and the class says when it provisions — `WaitForFirstConsumer` is written as "when a pod
  needs it", with the note that a claim sitting on Pending is then not a fault at all. It is the
  single most common reason to believe storage is broken when it is working exactly as designed. A
  class with nothing to provision volumes is flagged; so is the default. (KON-254)
- **Point a remote engine at a specific SSH key.** Leaving it empty still uses your agent and
  `ssh_config`, which is right for most people — but if your `ssh_config` pins `IdentityAgent` to a
  password manager, keys outside it were invisible to Kontena with no way to say otherwise. A named
  key is now the only one offered, so a host that limits authentication attempts cannot refuse you
  before your key is tried.
- **Connect to a host that only takes passwords.** The password is stored in your system keychain,
  never in your settings file and never on a command line where other processes could read it. A key
  is still the better route where you have one, and the form says so.
- **Browse to the key instead of typing its path.** The picker opens on `~/.ssh`, which is hidden on
  Unix and awkward to reach otherwise, and Kontena tells you if you picked the `.pub` half — ssh would
  have reported that as a rejected key, which reads as a problem on the host rather than on your side.
- **Browse any kind the cluster serves, custom ones included.** A new Resources page in cluster mode lists
  everything the API server offers — ConfigMaps, Secrets, Ingresses, PVCs, RBAC, and every CustomResource
  an operator installed — and shows each with the columns that kind's own author declared. Kontena asks
  the cluster what it serves rather than shipping a list, so a resource type that arrived with cert-manager
  or the Prometheus operator appears without Kontena having heard of it, grouped under *Custom resources*
  because that is the half of a cluster there was no screen for. The listing is the same one `kubectl get`
  prints, so the columns match what you would see in a terminal against the same cluster instead of being a
  second opinion. Every row opens its manifest, and can be deleted where the cluster says that is allowed —
  and only there. Long listings stop at 500 rows and say so rather than pretending that is all there is.
Settings › Local clusters can now build a cluster, not just check whether the tooling is there: name it, pick a Kubernetes version and how many nodes, publish host ports and prepare the first node for an ingress controller. kind's own output streams while it works, a failure keeps that output on screen with a plain reading of what went wrong, and the finished cluster appears in the backend switcher without anything to tick.
minikube joins kind as a local-cluster provisioner: pick either one when creating a cluster, set CPUs, memory and the driver where the tool supports them, and stop a cluster you are not using and start it again later without rebuilding it. The form shows exactly what the chosen tool can honour and nothing else.
Local Kubernetes clusters can be created and deleted with `kind`, through a new `IClusterProvisioner` seam: pick a name, a Kubernetes version, how many nodes, which host ports to publish and whether the nodes should be ready for an ingress controller. The tool's own output streams while it works, and the cluster appears in the switcher by itself — kind writes its kubeconfig context, which Kontena already discovers.
- **Kontena can install metrics-server into a cluster that has none.** The Nodes page already explained
  why the CPU and memory gauges were empty; now it offers to fix it. It applies the upstream
  metrics-server manifest (v0.9.0, embedded in the app rather than downloaded), waits for it to answer
  and then draws the gauges. On kind and minikube — whose kubelet serves a self-signed certificate — it
  adds `--kubelet-insecure-tls`, which is the difference between a working install and a pod that never
  becomes ready. The confirmation names the release, the image and every kind that lands in the
  cluster.

### Changed

- **A new primary palette: emerald on grey.** The brand green is a cleaner emerald, and the panel,
  sidebar, console and divider shades are derived from the background rather than picked alongside
  it, so they share its cast instead of looking washed out next to it. Every surface kept the
  brightness it had, so nothing changed in how near or far a panel reads. Alert blocks stay
  deliberately warm against the cool ground: they are meant to interrupt, not to agree with the page.
  (KON-130, KON-245)
- **Two colours that could not carry their own button labels were corrected.** In the light theme the
  primary button's white label sat on its fill at 3.38:1 and the destructive button's at 3.91:1 —
  both below the readability floor the rest of the palette is held to, on the two buttons most worth
  reading before you press them. The light green and red are deeper now; both labels clear the floor,
  and both colours also clear it as text, so neither needs the exemption it used to carry. The
  palette's contrast check now covers label-on-fill as well as text-on-background, which is the pair
  it had never looked at and where both defects had been sitting. (KON-130)
- **Kontena has a new mark.** The three flat squares are replaced by the shapes that read as a K —
  teal, indigo and violet, the same three colours the interface already uses. It arrives everywhere
  the old one appeared at once: the window and taskbar icon on every platform, and the mark drawn
  beside the app name in the title bar, on the welcome screen and on the About screen. That last one
  is vector rather than a bitmap, so it stays sharp on a high-DPI screen at any size. At the top of
  the sidebar the mark and the name beside it are large enough to read as the brand rather than as
  one more navigation icon, with a thin rule separating them from the engine switcher underneath.
  (KON-131, KON-133)
- **The window has its own title bar.** Instead of a system bar with the word "Kontena" in it, the
  mark and the name sit centred in a bar Kontena draws itself, so the brand is above the window as
  well as inside it. It still behaves like a title bar: drag it to move the window, double-click it
  to maximise and restore, and minimise, maximise and close sit on the right, where they behave the
  same way on every platform Kontena runs on. Two quick clicks on one of those three buttons still
  only do what that button says. (KON-134, KON-138, KON-195)
- **About is its own screen now.** It used to be the last category inside Settings, which is not
  where anyone looks for it. It sits at the bottom of the sidebar next to Activity and Settings, the
  mark on it is large enough to actually be the mark, and a **Quick actions** panel beside it links
  to the activity log, the releases page, the website and the issue tracker. (KON-135)
- **The Kubernetes versions on offer now come from the tool you picked.** They used to be one list of
  four, shared by kind and minikube and last updated by hand — which cannot be right for both, because
  the tools disagree about what exists: kind boots v1.36.1 today and minikube has never heard of it.
  minikube is now asked what it supports, so its list stays right across updates and names the version
  it would pick by itself. kind gets a maintained list, because its node images are published per
  release and there is nothing to enumerate — and next to it, a **node image** field for anything the
  list does not cover.
- **Port mappings read as `8086→8086`** — the stray colon in front of the host port is gone. It was a
  leftover from `docker ps`'s `0.0.0.0:8086->8086/tcp`, where it separates the address from the port;
  without that address in front of it, it separated nothing. Changed everywhere a mapping is written:
  the Containers grid, the Projects page, the detail header and the Inspect tab. (KON-158)
- **A confirmation that destroys something now lists what goes.** Taking a Compose project down used
  to be one paragraph you had to read carefully to work out what you were losing; it now names the
  project in the title and itemises it — how many containers and which services, how many networks and
  which ones — with the sentence left to say what survives. Destructive confirmations also carry a
  warning mark now; the ones that destroy nothing, like signing out of a registry, deliberately do not.
- The list only ever shows what is actually removed. Taking a project down does not touch volumes or
  images, so they are not in it — a dialog that over-promises is believed less the next time.
- **Workloads splits per kind in the sidebar.** One list held Deployments, StatefulSets, DaemonSets,
  Jobs and CronJobs together, which meant its columns could only be what all five have in common — so
  a CronJob's schedule, the field you opened the page for, had nowhere to go. Workloads now expands
  into an entry per kind, each with its own count, and each kind's page drops the column that just
  repeats its own heading and shows what that kind actually has instead. Workloads itself still opens
  everything, as before. (KON-169)
- **Only kinds that exist appear.** A cluster running Deployments and nothing else gets no submenu at
  all rather than four permanently empty rows, and the entries stay in a fixed order so the sidebar
  does not rearrange itself under the pointer when a Job finishes. The sidebar scrolls now, which it
  needed once its height stopped being fixed. (KON-169)
- **Keyboard shortcuts can be changed, and you can see what they are.** Settings › General › Keyboard
  lists every shortcut Kontena has, what it does and the keys it answers to. Change one by pressing
  the combination you want rather than spelling it out; a combination another shortcut already has is
  refused by name instead of quietly taking it over, and the keys a terminal needs to interrupt, end
  or suspend what is running stay with the terminal. Any shortcut goes back to its default on its own,
  and all of them go back at once. Changes take effect immediately — nothing needs restarting.
- **One default per platform, and only the keys that platform uses.** Shortcuts previously registered
  the Ctrl and Cmd variant side by side, so `Ctrl+F` also worked on macOS where it is not the
  convention. Each platform now gets its own default, and anyone who prefers the other can set it.
- **The shortcut is shown where the button is.** Back and Refresh name their keys in the tooltip, so
  a shortcut can be discovered by using the app rather than by reading the source.
- **`Kontena.Sdk` is now the contract it claimed to be.** `CONTRIBUTING.md` told anyone writing a
  backend to implement the abstraction layer and reference only `Kontena.Sdk`, the MIT extension
  package — while the SDK was two interfaces stacked on top of `Kontena.Core` and `Kontena.Engines`,
  every adapter referenced those two directly, and nothing in the tree referenced the SDK at all. The
  contract surface has moved into the SDK: the CEAL and the OAL, the engine-neutral and cluster models,
  the error types, the tool seam, `IBackendProvider` and `IEnginePlugin`. `Kontena.Sdk` now references
  no other project, `Kontena.Core` keeps only the app's own side of the line — settings, release
  channels, update checks — and depends on the SDK rather than being depended on. All four adapters
  reference nothing but the SDK. The licence split rests on the same fact: MIT on the SDK is what lets
  a third party write and sell a backend, and that only holds while the SDK compiles against nothing
  the Commons Clause covers. `Kontena.Sdk.Tests` reads the project files and fails the build if any of
  it drifts back, because a rule external contributors are held to should not be one only a reviewer
  can check.
- **The sidebar is grouped.** On a cluster the ten entries now sit under **Cluster**, **Workloads**,
  **Network** and **System** instead of in one long list; on a container engine the five stay as they
  were, because five entries do not need dividing. The count badge only appears where there is
  something to count, and the title bar says which backend you are looking at.
- **The workload kinds are always in view.** They used to hide behind a chevron on the entry above
  them; with the sidebar grouped, that entry repeated the word its own heading already carried. The
  kinds are ordinary entries now, and the row above them is named for the page it opens.
- **Corners and hover states are consistent across the app.** Every rounded corner now comes from one
  of three sizes instead of the nine values that had accumulated, and hovering a sidebar item or a
  button fades over 180ms rather than switching instantly. Lists and tables deliberately do not
  animate — motion on a dense grid reads as lag.
- **Nightly and preview versions lost the date, and gained a build date you can actually see.**
  A nightly was called `0.3.0-nightly.20260731.44` — long enough that the part telling two of them
  apart was the last thing you read, and the date never ordered anything anyway. They are now
  `0.3.0-nightly.44`, and the day the build was made is shown next to the version instead — in
  About, in Settings › Updates, and in the update card, where "how old is the build I am on?" is the
  question the version used to be answering badly. A build you made yourself has no such date and no
  longer pretends to.
- **Backends wear their own logo instead of a letter.** The switcher pill, the switcher itself, the
  first-run screen, Settings › Engines, the activity log, the container rows and the Run footer all
  showed a one-letter badge — `D`, `P`, `K` — and every one of them painted that letter Docker blue,
  including Podman's, whose plate was already violet. Each backend now declares its own mark and colour,
  so what you see is the Docker whale, the Podman seal or the Kubernetes helm, and a backend Kontena
  learns about later brings its logo with it rather than needing a case in the app. Two entries keep a
  letter on purpose: the demo backends, which are not a product, and a remote engine, where the `R` is
  the only thing distinguishing it from the local Docker in the same list. The mark is measured against
  the theme rather than trusted to it — a fixed brand colour is dark-on-dark or light-on-light in one of
  the two themes, and Podman's violet on dark sat at 2.0:1 — so it keeps the brand's hue and shifts only
  as far in lightness as it must to stay legible. The Apple container row also stops rendering as an
  empty box on Windows and Linux, where the glyph it used only exists on Apple's own systems.

### Fixed

- **Tools installed by Homebrew are found again when Kontena is opened from the Dock or Finder.** An
  app launched that way inherits a minimal environment — no shell profile is read — so `/opt/homebrew/bin`
  and `/usr/local/bin` are simply absent from its `PATH`, and helm or kustomize would be reported as
  not installed while sitting right there. The same app started from a terminal found them, which made
  it look like the render was broken rather than the lookup. Kontena now also looks in the places
  package managers install to, on every platform, so "not installed" no longer depends on how you
  started the app. (KON-129)
- **Windows shows the new icon straight after an update, not whenever it feels like it.** Windows
  remembers an application's icon by where the program lives, and Kontena updates in place — the
  path never changes — so an update that changes the icon could leave the old one on the Start-menu
  shortcut for days. Kontena now tells Windows to forget the icon it had cached, as part of applying
  the update. A shortcut you pinned to the taskbar keeps its own copy that nothing but unpinning and
  re-pinning will shift; that one is still yours to do. (KON-132)
- **Settings, Activity and About now open when no engine is reachable.** They were listed in the
  sidebar the whole time, but clicking one left the "can't reach a container engine" screen in place,
  so it looked like Kontena had ignored you. These are the three pages that matter most at that
  moment — Settings is where the engine list, a remote or a kubeconfig gets fixed, Activity shows
  what happened just before it broke, and About has the version and the link to report it. Leaving
  the page brings the connection screen back. (KON-137)
- **A running minikube cluster shows its state again, and can be stopped.** `minikube profile list`
  calls a healthy profile `OK`, not `Running` — so every cluster it reported landed in an unknown
  state, which left the row without a state and without its Stop button. Being able to stop a cluster
  and start it later is the one thing minikube adds over kind, so it was the wrong thing to lose.
- **The cluster overview no longer promises pages that are already there.** A note at the bottom said
  the resource browsers — nodes, namespaces, workloads, pods, services and the YAML apply/dry-run
  flow — were still to come. They shipped, and the nav has led to them for a while; the note was
  honest when it was written and had quietly turned into the opposite.
- **Pod tabs line up with container tabs again** — the tab strip on pod detail floated above the
  header's bottom edge, so the active tab's underline sat in mid-air instead of on the rule, and the
  tabs carried no icons where the container tabs do. Both views now use the same chrome. (KON-156)
- **Kontena no longer fails to start over a timestamp that was never set.** East of UTC — anywhere on
  CET, CEST or further — a container, image or cluster event carrying an empty creation time would
  crash the startup, and the app opened on "Can't reach a container engine" with a .NET error under
  it, while Docker sat in the same list marked Reachable. Nothing was ever unreachable. A timestamp an
  engine has not set now reads as exactly that, and every real one is unchanged.
- **The nightly and preview channels no longer go missing while a new build is published.** Both were
  deleted and rebuilt on every run, which left three to four minutes a night in which the updater
  found nothing and told you to check your connection. The new build is now staged in full before the
  channel is switched over, so the gap is about a second.
- **A failed update check says which failure it was.** Every network error read "Could not reach the
  update server. Check your connection" — including a rate limit (wait), a channel that is mid-publish
  (nothing to do), and a proxy refusing the secure connection (not your router). The status code that
  tells these apart was already there and is now used.
- **Search works on a Kubernetes cluster.** It never had: the box accepted what you typed and did
  nothing with it, because no cluster page was wired to receive it — so a search that found everything
  and a search that found nothing looked identical. Nodes, namespaces, workloads, pods and services
  now filter as you type, on more than the name: a pod can be found by the node it sits on or by the
  state it is stuck in, a node by its role or condition, a service by its type or its ports. Matching
  is case-insensitive, stays inside the namespace you have picked, and survives a refresh — reloading
  under an active search no longer quietly shows everything again. (KON-164)
- **The search box says what it searches, and switches off where there is nothing to.** It used to
  read "Search containers, images, volumes…" on a Kubernetes cluster, naming three things that do not
  exist there; each page now names its own. On pages that are not lists — the cluster overview, apply
  manifest, the workloads summary — the box is dimmed and disabled rather than silently ignoring you.
  A search that turns up nothing now says so, instead of leaving a blank page that reads as one that
  failed to load. (KON-164)
- **Typing in the search box no longer stutters.** Every keystroke rebuilt the whole list — and
  rebuilding a row means building its buttons and icons again, including for rows that were already
  on screen and still matched. Two things now: a burst of keystrokes costs one update instead of one
  per letter, and that update only removes and inserts the rows that actually changed. Clearing the
  box is exempt from the wait, because there is no next keystroke coming to make waiting worth it.
  (KON-164)
- **Log views open at the newest line instead of the oldest.** Container logs, pod logs, compose logs
  and the build and compose-up consoles all used to open at the top, so the first thing you saw was
  the beginning of a log you opened to see the end of. They now start at the tail and stay there.
- **Follow works on the compose logs.** The button was there and did nothing until the next line
  arrived — which on a stopped stack never came.
- **Scrolling up no longer fights you.** Reading something back switches following off, and scrolling
  back to the bottom switches it on again, the way `docker logs -f` behaves in a terminal. The Follow
  button still does both by hand.
- **Port forwarding from a pod offers the pod's own ports.** It used to propose `80 → 80` for every
  pod in the cluster — not a reading of anything, just a fallback, presented with the same confidence
  as a port Kontena had actually looked up. The ports a pod's containers declare are now read and
  offered as choices, labelled with the container they belong to so that two containers publishing
  8080 are not two identical rows. Where nothing is declared, the field starts empty and says so
  rather than inventing a number. (KON-170)
- **The suggested local port is one you can actually bind.** It was set equal to the remote port, so
  forwarding anything below 1024 proposed a port that needs root on Linux and macOS and failed every
  time. Well-known ports now shift by the usual convention — 80 becomes 8080, 443 becomes 8443 — and a
  local port already in use by another of your forwards is stepped over rather than offered. Ports you
  set yourself are left alone. (KON-170)
- **Searching the Images, Networks, Volumes and Projects pages no longer rebuilds the whole list.**
  Those four still cleared and refilled the table on every keystroke, which makes the list throw away
  and rebuild every row on screen — the same lag the cluster pages lost two releases ago, and most
  noticeable on a host with a lot of images or volumes. They now share one list page with the cluster
  side, which also gives them what they were missing: "nothing matched that search" now reads
  differently from "there is nothing here yet".
- **Containers started by DataTray are named properly.** SQL Explorer was renamed to DataTray, and its
  containers are labelled with the new name — which Kontena did not know, so the container list called
  them "Datatray". Containers created before the rename still show as "SQL Explorer", because the label
  they were started with never changes.
- **Light mode: the YAML tab is no longer a black plate, and the search placeholder can be read.** The
  console colour stayed dark in both themes on purpose — a terminal that turns white is a surprise —
  but the manifest views and the command previews borrowed it, so in light mode they were dark panels
  with dark text. Those now use a code surface that follows the theme, while the terminal and the log
  views stay dark. The search box's placeholder took its colour from the base theme rather than from
  Kontena's palette, which left it almost invisible on light.
- **Logs behind a tab open on their last line, not their first.** Pod and container logs sit on a tab
  that is built up front and only shown when you pick it, so every line arrived before the list had
  been laid out even once — and the tail-following added last release had nothing to scroll. Becoming
  visible now counts as its own moment, so the view opens where the log is happening.
- **A container with several ports no longer runs its port list into the state next to it.** In the
  pod's containers table and on the Services page, the ports cell filled its column to the last pixel,
  so as soon as the list was long enough to be cut off the ellipsis sat against the next column and read
  as `8443/TCP …Running` — one value, apparently. The cells that can be cut off now keep a gap, and
  hovering the ports shows the full list one port per line, so a fourth port is still readable without
  making the column wide enough for a case most rows do not have. Long image names get the same
  treatment.
- **Switching namespace now picks the right Workloads page.** Whether Workloads shows the dashboard or
  the plain list depends on how many kinds the namespace runs, and that was decided from the counts of
  the namespace you had just left — so one kind to several left you in the table, and several to one
  gave you a dashboard of a single card. The counts are read first now. A namespace switch while on a
  per-kind page (Jobs, DaemonSets) also no longer lands on an empty list of a kind the new namespace
  does not run, and no longer falls back to Overview.
- **Enter and Escape work in the terminal again.** Both are shortcuts for the dialog that is open —
  confirm and close — but they were claimed whether or not one was, so a shell never saw them: `ls`
  needed Ctrl+Enter, and Escape never reached vim. They are only taken while a dialog is actually up.
- **Container log lines now show when they were written, not when Kontena read them.** The Docker
  adapter asked the engine for no timestamps and stamped every line with the current time, so opening
  the logs of a container that had been running for months showed forty lines from four different days
  all at the same millisecond. The engine's own timestamps are used now — the Kubernetes side already
  did this, and both go through one parser instead of two that had drifted apart.
- **Row dividers are visible again on the cluster pages.** Ten lists and detail panels asked for a
  divider colour that was never defined, so the hairlines between their rows were not drawn at all —
  in either theme. The Add backend dialog's footer had the same problem with its background.
- **Remote engines over SSH now work on Windows.** The tunnel listened on a unix socket at this end,
  which Windows has no way to do — and the Windows path it was given was misread as an address, so the
  attempt failed with `Bad local forwarding specification 'C:\Users\…'` before it began. Windows now
  forwards over a port on `127.0.0.1` instead. The remote end is unchanged, and so is every other
  platform.
- **Reviewing a host's fingerprint now works on every host you can reach.** Kontena fetched the key
  with `ssh-keyscan`, which is a different client from the `ssh` it connects with — so a host the two
  disagreed about (`choose_kex: unsupported KEX method …`) had no fingerprint to show and no way
  forward. It now asks `ssh` itself, so wherever a connection is possible the question can be
  answered. Your `known_hosts` is still only written when you say yes.
- **No more console window when connecting to a remote engine on Windows.** Kontena has no console of
  its own, so Windows opened one for the `ssh` it starts — a black window sitting next to the app for
  as long as the connection lasted. The same was true of the helper that reads registry logins from
  Docker's config.
- **Kontena now tells you which build you are on.** About, Settings and the update card all showed
  `0.3.0` no matter which build was running, because the number they read cannot hold a prerelease
  part — so every nightly and preview claimed to be the release it was built ahead of, and two
  nightlies from the same day were indistinguishable. They now show the full version,
  `0.3.0-nightly.20260731.44`, and the update card reads it from the installed package itself, which
  is the same number the updater compares against when deciding there is something newer.
- **Screen readers can now name the icon buttons.** Every row action and every button in the command
  bar is an icon with no text, so assistive software announced twenty-eight of them as "button" and
  nothing else — leaving no way to tell restart from remove except by counting positions. Each one now
  reports what its tooltip says. The status dots and the glyphs inside the buttons are marked as
  decoration, so they are no longer announced as nameless elements sitting between you and the label
  that already says the same thing. (KON-56)
- **Secondary text is readable again.** The muted grey used for timestamps, column headings and
  status lines — "Up 2 hours", "Exited (0)" — sat at 2.7:1 against the surfaces behind it, well under
  the 4.5:1 that WCAG asks for body text, in both themes. Amber in the light theme cleared no
  threshold at all. All three are corrected to the nearest shade that passes, keeping their hue, so
  nothing looks different beyond being legible. A test now measures every text colour in both themes
  and fails the build if a new one drops below. (KON-56)

### Security

- **A remote engine can no longer carry a value that SSH reads as one of its own options.** A host or
  user beginning with `-` was passed to `ssh` as typed, where `-oProxyCommand=…` is a command SSH runs
  under your account, and a remote socket path containing `:` could change which address the tunnel
  actually forwarded to. Both are refused now, in the one place every path goes through — the add
  wizard, the Settings form, and a remote loaded from a settings file that came from somewhere else.
  The form says which value is the problem while you are typing it, instead of only greying out the
  button, and a stored remote that Kontena will not use now says why in Settings › Engines rather
  than sitting there as "not reachable".
- **A Helm field can no longer carry a value that helm reads as one of its own options.** A chart,
  release name, repository name, repository URL or chart search term beginning with `-` was passed to
  `helm` as typed, where `--kubeconfig=…` or `--ca-file=…` would have silently moved the render to
  another cluster or another certificate authority. Those are refused now, along with a repository
  URL on any scheme other than `http`, `https` or `oci`. The chart browser says why a search returned
  nothing instead of blaming a stale index.
- **A credential helper named in `config.json` is only run when its name is a plain word.** The
  helper comes from `credsStore` or `credHelpers` in a file other programs write, and a name
  containing a separator would have been started as a path relative to the working directory instead
  of being looked up on `PATH`. Names outside letters, digits, `_` and `-` are now refused, which
  reads as what it already was: no credential from there.
- **The CI workflow now asks for read access only.** It was the one workflow without a `permissions`
  block, so its `GITHUB_TOKEN` inherited the repository default — write, potentially — for a job that
  only builds and tests.
- **The workflows now pin every GitHub Action to a commit SHA instead of a version tag.** A tag can
  be moved, and the release job runs with `contents: write` and publishes the releases the in-app
  updater installs — so a compromised action repository would have reached users through the app's
  own update chain. Dependabot keeps the pins current from now on.
- **`settings.json` is written for its owner only.** On Linux the usual umask left it readable by
  every other local account. There are no secrets in it — those live in the keychain — but the remote
  engine hosts and usernames, the registry usernames and the kubeconfig paths Kontena reads are worth
  something to whoever else has an account on the machine. The config directory is created the same
  way, and a file left wide open by an earlier version is narrowed the next time it is saved.

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

[Unreleased]: https://github.com/Lionear/Kontena/compare/v0.4.0...HEAD
[0.4.0]: https://github.com/Lionear/Kontena/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/Lionear/Kontena/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/Lionear/Kontena/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/Lionear/Kontena/releases/tag/v0.1.0

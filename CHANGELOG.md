# Changelog

All notable changes to Kontena are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project adheres to
[Semantic Versioning](https://semver.org/). Add finished work under `## [Unreleased]`; releasing a
`v<semver>` tag rolls that section into a dated version heading — see
[CONTRIBUTING.md](CONTRIBUTING.md#changelog).

## [Unreleased]

### Changed

- **The first-run wizard no longer advertises a runtime your machine can never run.** The "Apple
  container · Coming soon" row was a full-size engine row on every platform, so on a machine with one
  detected engine a third of the list was a roadmap item — and on Linux and Windows it announced a
  native macOS runtime that will never arrive there. It now appears only on macOS. (KON-337)

### Fixed

- **Switching to another update channel works in both directions.** Moving from preview to nightly —
  or back to stable — did nothing: the updater kept reporting that you were on the newest release. The
  channel is part of the version number, and `0.4.0-nightly.…` sorts *below* `0.4.0-preview.…`, so the
  updater read a deliberate switch as a downgrade and refused it. Reinstalling was the only way out.
  A channel you pick yourself is now followed wherever it leads, and the card says you are switching to
  it rather than claiming a newer version. A feed rolling backwards on your *own* channel is still
  refused. (KON-372)
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

[Unreleased]: https://github.com/Lionear/Kontena/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/Lionear/Kontena/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/Lionear/Kontena/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/Lionear/Kontena/releases/tag/v0.1.0

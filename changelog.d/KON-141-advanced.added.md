- **The nerdctl plugin now shows live stats and activity, builds images, brings Compose projects up, and
  pulls, tags and removes images.** Container CPU and memory are sampled every couple of seconds, and the
  activity feed follows containerd's own event stream — which reports containers and images, but never
  volumes or networks, because containerd has no events for those. Building is only offered when a
  buildkitd is actually reachable: `nerdctl build` exists whether or not it can work, so Kontena looks for
  the socket rather than promising a build that fails a few seconds later. Compose reports what nerdctl
  reports, without its log formatting. Opening a terminal in a container stays unavailable on this
  backend — driving nerdctl means starting a process and reading its output, with no way to type into it —
  as does browsing a volume's contents, and there is still no way to install this plugin; that comes later.

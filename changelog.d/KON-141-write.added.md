- **Kontena can now create, start, stop, restart, pause, resume and remove containers on containerd via
  the nerdctl plugin**, and create and remove volumes and networks, and prune unused containers, images
  and volumes to reclaim disk space — all of it was read-only before. Creating a volume with a driver
  other than nerdctl's built-in one now fails with a clear error instead of silently creating a default
  volume anyway, since nerdctl has no way to honour that choice. Prune reports how many items it
  removed, but not how much space came back — nerdctl itself doesn't report that. Attaching or detaching
  a running container from a network isn't possible at all through this backend; nerdctl has no command
  for it. Building images, Compose, exec-into-container and live stats/events are still out of reach, and
  there is still no way to install this plugin — that comes later.

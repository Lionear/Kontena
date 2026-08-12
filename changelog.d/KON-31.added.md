- **Apple `container` is now a backend.** On macOS with Apple's native runtime installed, it appears in
  the switcher alongside Docker and Podman: containers, images, volumes and networks are listed, a
  container's detail page reads its command, environment, mounts and addresses, and containers can be
  started, stopped, restarted and deleted. It stays out of sight on Windows and Linux, where the runtime
  cannot exist.
- Logs, terminal, stats, image pulls and builds are not wired up for this backend yet, and the runtime
  itself has no pause, no Compose and no event stream — Kontena hides what it cannot offer rather than
  showing buttons that fail.

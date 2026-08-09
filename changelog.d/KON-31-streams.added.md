- **Logs, a terminal and live usage for Apple `container`.** A container's log streams as it is written,
  the terminal opens a real shell inside the container — one you can type in, that resizes with the
  window and passes Ctrl-C through — and CPU and memory are sampled every couple of seconds.
- The log is one stream: Apple's runtime writes a container's stderr to the same channel as its stdout,
  so Kontena shows every line the same way rather than colouring some of them on a guess. The first CPU
  reading of a session is empty, because a percentage only exists between two samples.

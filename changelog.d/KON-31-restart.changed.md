- **The Run dialog no longer offers a restart policy where the engine has none.** Apple's `container`
  runtime cannot restart a container automatically, so on that backend the field is gone rather than
  present and ignored — and the command preview stops showing a flag that does not exist there. Docker,
  Podman and nerdctl are unchanged.

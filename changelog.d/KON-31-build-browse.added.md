- **Build images and look inside a volume on Apple `container`.** Builds run through the runtime's own
  BuildKit builder with the step-by-step output you get everywhere else, and a volume's contents can be
  browsed from the Volumes page.
- Browsing here starts a small container for a moment — Apple's runtime offers no way to read a volume
  without one — and it is removed again immediately. That completes the backend: everything Kontena
  offers for Docker and Podman now works here too, apart from what this runtime genuinely lacks.

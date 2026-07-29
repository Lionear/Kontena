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

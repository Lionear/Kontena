- **Custom resources on Resources now show their YAML.** Opening one used to answer with a placeholder
  — "Dragonfly is not a kind this adapter can read yet" — because the manifest panel could only read the
  dozen kinds Kontena models by hand, while the list it was opened from is generic. It now reads any kind
  the cluster serves, rendered by the API server itself, so a custom resource shows the same YAML as
  `kubectl get -o yaml` and a newly installed operator's kinds work without waiting for Kontena to learn
  about them.

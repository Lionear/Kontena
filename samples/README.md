# Samples

Small, deliberately boring manifests for exercising Kontena's declarative flow against a real
cluster. Nothing here is meant for production — they are shaped to make each step of
*render → dry-run → plan → apply* visible.

| Path | What it exercises |
| --- | --- |
| `nginx-deployment.yaml` | The plain apply flow (KON-69, KON-86): paste or load, dry-run, apply. |
| `kustomize/` | The Kustomize source (KON-88): a base plus a `prod` overlay. |
| `helm/guestbook/` | The Helm source (KON-89): a chart, a values file, and `--set` on top. |

## Kustomize

Open **Apply manifest → Kustomize**, browse to `samples/kustomize/overlays/prod`, and press
**Build**. The overlay renames everything with a `prod-` prefix, scales to three replicas and pins
a newer image; the base knows nothing about any of that. Then run the dry-run: overlay mistakes and
cluster complaints land in the same plan.

Point it at `samples/kustomize/base` instead to see what the overlay actually changed.

## Helm

Open **Apply manifest → Helm**, browse to `samples/helm/guestbook`, give it a release name, and
press **Render**.

Values precedence is easiest to see with all three layers at once:

- `values.yaml` in the chart sets `replicaCount: 1`
- adding `values-prod.yaml` under **Values** raises it to `3` and sets a `message`
- a `replicaCount=5` line in **Set** beats both

`message` is empty by default; setting it renders an extra ConfigMap, so the plan grows by one
resource without anything else changing — a quick way to watch a diff that is genuinely additive.

Nothing here reaches a cluster on its own: rendering is local, and the dry-run that follows
persists nothing.

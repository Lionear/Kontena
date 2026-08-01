# Bundled OpenAPI schemas (KON-289)

Offline fallback for `SchemaIndex` when no cluster is connected (Plan §3): "gebundelde upstream-set
per minor". These are real Kubernetes OpenAPI v3 documents, not fabricated — downloaded from the
upstream `kubernetes/kubernetes` repository and trimmed to just `components.schemas` (the only part
`OpenApiV3Document` ever reads; `paths` and everything else make up the bulk of the original file and
are never parsed).

## Source

Fetched 2026-08-01 from:

```
https://raw.githubusercontent.com/kubernetes/kubernetes/release-<minor>/api/openapi-spec/v3/<group>_openapi.json
```

for `<minor>` ∈ `1.34`, `1.35`, `1.36` — the three most recent stable minors as of that date (`1.37`
did not exist yet). Apache License 2.0 (Kubernetes' own); see `THIRD-PARTY-NOTICES.md`.

## Groups bundled

Eight groups per minor, the ones an authoring tool actually needs — not the full API surface (which
includes every built-in controller/admission/scheduling type nobody hand-writes a manifest for):

| File | Group/Version | Typical kinds |
|---|---|---|
| `core_v1.json` | `` / `v1` | Pod, Service, ConfigMap, Secret, Namespace, PVC, ServiceAccount |
| `apps_v1.json` | `apps/v1` | Deployment, StatefulSet, DaemonSet, ReplicaSet |
| `batch_v1.json` | `batch/v1` | Job, CronJob |
| `networking_v1.json` | `networking.k8s.io/v1` | Ingress, NetworkPolicy |
| `rbac_v1.json` | `rbac.authorization.k8s.io/v1` | Role, RoleBinding, ClusterRole, ClusterRoleBinding |
| `autoscaling_v2.json` | `autoscaling/v2` | HorizontalPodAutoscaler |
| `policy_v1.json` | `policy/v1` | PodDisruptionBudget |
| `storage_v1.json` | `storage.k8s.io/v1` | StorageClass |

Add another group by dropping a same-shape trimmed file next to these and registering it in
`BundledSchemas.FileNames`.

## Measured size (Plan §11 — measure, do not guess)

16.1 MB raw → 3.99 MB trimmed to `components.schemas` only (24.8%) → 0.79 MB gzip-compressed. The
embedded resources add the trimmed, uncompressed 3.99 MB to the assembly; actual installer/download
impact is closer to the gzip figure once Velopack packages it.

## What this fallback cannot know

A custom resource is never in here — there is no way to bundle what an operator installs. An unknown
kind without a cluster connection reports as unverifiable (a `?`), not wrong, same as always. There is
also no deprecation signal offline: whether a served `apiVersion` still exists on any given real
cluster is something only that cluster can answer (`ClusterEngineSchemaSource`), never this fallback.

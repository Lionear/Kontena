- **Browse any kind the cluster serves, custom ones included.** A new Resources page in cluster mode lists
  everything the API server offers — ConfigMaps, Secrets, Ingresses, PVCs, RBAC, and every CustomResource
  an operator installed — and shows each with the columns that kind's own author declared. Kontena asks
  the cluster what it serves rather than shipping a list, so a resource type that arrived with cert-manager
  or the Prometheus operator appears without Kontena having heard of it, grouped under *Custom resources*
  because that is the half of a cluster there was no screen for. The listing is the same one `kubectl get`
  prints, so the columns match what you would see in a terminal against the same cluster instead of being a
  second opinion. Every row opens its manifest, and can be deleted where the cluster says that is allowed —
  and only there. Long listings stop at 500 rows and say so rather than pretending that is all there is.

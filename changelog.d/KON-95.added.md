- **A managed cluster is now measured against its own provider's support window.** GKE, EKS and AKS
  each keep a Kubernetes release alive on their own schedule, and none of them matches upstream's —
  so a cluster judged by upstream's dates would be called unsupported while its provider was still
  supporting it, sometimes a month early. Kontena now asks about the calendar belonging to whatever
  the cluster says it is. Anything that is plain Kubernetes — kind, minikube, k3s, kubeadm — takes the
  upstream calendar, which for kind and minikube is not an approximation but the exact answer.
  This completes the version health question that in-cluster version skew answered the other half of:
  skew says whether a cluster's own parts agree with each other, this says whether anyone is still
  fixing the release it runs.

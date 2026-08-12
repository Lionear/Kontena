- **Opening a Kubernetes cluster does a third of the work it used to.** The shell filled the namespace
  picker, built the landing page, and only then selected a namespace — and selecting one reads the
  cluster's workload kinds and rebuilds the page, because which page Workloads is depends on them. So
  every cluster you opened listed its namespaces six times and built the overview twice, throwing the
  first one away along with the seven watch streams it had just opened. It now reads what a page is
  built from before building one: two namespace lists instead of six, one overview instead of two.

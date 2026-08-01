- **Kontena can install metrics-server into a cluster that has none.** The Nodes page already explained
  why the CPU and memory gauges were empty; now it offers to fix it. It applies the upstream
  metrics-server manifest (v0.9.0, embedded in the app rather than downloaded), waits for it to answer
  and then draws the gauges. On kind and minikube — whose kubelet serves a self-signed certificate — it
  adds `--kubelet-insecure-tls`, which is the difference between a working install and a pod that never
  becomes ready. The confirmation names the release, the image and every kind that lands in the
  cluster.

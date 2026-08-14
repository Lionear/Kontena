- **Kontena starts about a third faster.** The window now appears in roughly a second instead of a
  second and a half, and the app is ready to use in 2.3 seconds where it took 3.6. Nothing was
  removed to get there: the build now ships compiled native code beside the app's own, so the first
  run of everything — drawing the window, reading your kubeconfigs, contacting the first backend —
  no longer waits for it to be compiled on your machine. The download is about 14 MB larger for it.

- **Connect an engine on another host.** Kontena managed the engine on your own machine and nothing else;
  now a remote Docker appears in the switcher like a local one, with the same pages and the same actions.
  Two ways in, added under Settings › Engines: **SSH**, which forwards the remote socket using the keys,
  agent and `ssh_config` you already have — nothing to generate or open up — and **TCP with TLS**, pointed
  at the `ca.pem`/`cert.pem`/`key.pem` directory an existing Docker TLS setup already uses. *Test
  connection* really connects before anything is saved, and reports what the host said rather than a
  generic failure. A TCP endpoint without certificates is refused unless you state outright that you want
  it: an unauthenticated engine port hands control of that machine to anyone who can reach it. (KON-46)

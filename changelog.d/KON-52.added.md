- **Credentials go in your system keychain.** Kontena can now store secrets where the operating system
  keeps them — the Secret Service on Linux, so they show up in Seahorse or KWallet and you can inspect
  and revoke them there — instead of in a file of its own. There is no fallback on purpose: if no
  keychain is reachable, Kontena says so in Settings › About and stores nothing, rather than writing a
  password somewhere it should not be. This is the groundwork for logging in to private registries.
  (KON-52)

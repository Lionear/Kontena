- **Credentials go in your system keychain.** Kontena can now store secrets where the operating system
  keeps them — the Secret Service on Linux, Credential Manager on Windows, the login Keychain on macOS —
  instead of in a file of its own. They show up in your own keychain tool under a readable name, so you
  can inspect and revoke them without Kontena's help. There is no fallback on purpose: if no keychain is
  reachable, Kontena says so in Settings › About and stores nothing, rather than writing a password
  somewhere it should not be. This is the groundwork for logging in to private registries. (KON-52)

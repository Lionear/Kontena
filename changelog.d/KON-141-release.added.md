- **The nerdctl plugin is now downloadable.** Every release carries a
  `kontena-plugin-nerdctl-<version>.zip` next to the app: unzip it into
  `%APPDATA%\Lionear\Kontena\plugins\nerdctl\` on Windows or `~/.config/Lionear/Kontena/plugins/nerdctl/`
  on Linux and macOS, start Kontena, and approve it when asked. It adds one backend per containerd
  namespace and needs nerdctl already on the machine — it does not install one. Deliberately not
  bundled with the app: downloading, unpacking and approving is the same path the plugin store will
  take later, and shipping it in the box would prove none of it.

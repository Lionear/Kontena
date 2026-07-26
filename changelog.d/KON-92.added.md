- **Create a network from the Networks page.** Networks could be listed and removed but not made, so
  putting two containers on a network of your own meant creating it outside Kontena first. **New
  network** asks for a name, a driver and — optionally — a subnet; left empty, the engine picks one and
  the list then shows what it chose. Only drivers that can actually be created are offered: `host` and
  `none` are the engine's own and cannot be made, and `overlay` needs Swarm. A subnet that is not valid
  CIDR is caught before the request goes out, since the daemon's own message for it is considerably
  less clear. (KON-92)

- **Run a container, make a volume or a network, and reclaim space on Apple `container`.** The Run
  dialog, the create dialogs and the prune and remove actions all work on this backend now, instead of
  quietly doing nothing.
- Two things this runtime will not do, and now says so instead of pretending: it has no restart policy,
  so asking for one is refused rather than accepted and forgotten; and a volume or network something is
  still using cannot be removed, which now arrives as an explanation rather than a row that stays put.

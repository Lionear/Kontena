- **Pull, tag and inspect images on Apple `container`.** Pulling reports the runtime's own progress as it
  goes, an image can be given a second name or removed, and the Run dialog pre-fills an image's
  environment variables.
- Its ports and volumes are not pre-filled: Apple's runtime does not report what an image declares
  there, so those are typed by hand rather than guessed. A private-registry login is refused on this
  backend — the runtime can only use one by keeping your password in its own store, and Kontena keeps
  registry secrets in your keychain. Public registries pull normally.

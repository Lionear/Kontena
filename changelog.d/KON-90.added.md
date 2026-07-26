- **Look inside a volume.** A volume used to be a name, a size and a mountpoint you could not open — to
  see what was actually in it you started a container with it mounted and poked around by hand.
  **Browse** on a volume row opens its contents: directories first, with sizes and how long ago each
  entry changed, and clicking a directory goes in. It is read-only, and deliberately so; nothing here
  writes, moves or deletes. Kontena reads the volume by mounting it into a container that is **created
  but never started**, so no image needs a shell and nothing of yours runs. Very large directories are
  listed up to a limit and say so, rather than going quiet for a minute. (KON-90)

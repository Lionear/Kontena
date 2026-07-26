- **Create a volume without running a container first.** Volumes could be listed and deleted, but the
  only way to *get* one was to let some earlier container create it as a side effect — so a named
  volume you wanted to mount had to be conjured up by running something you did not want. **New
  volume** on the Volumes page asks for a name and a driver, and the volume is then there to mount from
  the Run container dialog. A name that is already taken or invalid is reported in the dialog with what
  you typed still in it. (KON-91)

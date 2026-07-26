- **Screen readers can now name the icon buttons.** Every row action and every button in the command
  bar is an icon with no text, so assistive software announced twenty-eight of them as "button" and
  nothing else — leaving no way to tell restart from remove except by counting positions. Each one now
  reports what its tooltip says. The status dots and the glyphs inside the buttons are marked as
  decoration, so they are no longer announced as nameless elements sitting between you and the label
  that already says the same thing. (KON-56)
- **Secondary text is readable again.** The muted grey used for timestamps, column headings and
  status lines — "Up 2 hours", "Exited (0)" — sat at 2.7:1 against the surfaces behind it, well under
  the 4.5:1 that WCAG asks for body text, in both themes. Amber in the light theme cleared no
  threshold at all. All three are corrected to the nearest shade that passes, keeping their hue, so
  nothing looks different beyond being legible. A test now measures every text colour in both themes
  and fails the build if a new one drops below. (KON-56)

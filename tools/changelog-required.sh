#!/usr/bin/env bash
#
# Fails when a change touches the app (anything under src/) and adds or edits no changelog fragment
# (KON-459). The name check only sees fragments that are there, so a branch that simply forgot one
# went green: KON-454 and KON-455 merged that way in #316.
#
# Input is env, never argv splicing:
#   BASE   the commit the change is measured against, e.g. origin/develop (required)
#   HEAD   the change itself (default: HEAD)
# Exit 0 when a fragment is there or nothing under src/ changed; exit 1 otherwise.
#
# A change under src/ that a user cannot see (a refactor, a rename) opts out with the no-changelog
# label on the pull request; the workflow skips this script then, so the choice stays on the PR.
set -euo pipefail

: "${BASE:?BASE is required}"
HEAD="${HEAD:-HEAD}"

# Three dots: what the branch changed since it left BASE, not what BASE gained meanwhile.
if ! git diff --name-only "$BASE...$HEAD" -- src | grep -q .; then
  echo "Nothing under src/ changed; no fragment needed."
  exit 0
fi

if git diff --name-only --diff-filter=AM "$BASE...$HEAD" -- changelog.d \
     | grep -v '^changelog\.d/README\.md$' | grep -q .; then
  echo "A changelog fragment is part of this change."
  exit 0
fi

echo "::error::This changes src/ but adds no changelog fragment. Add changelog.d/<ticket>.<category>.md (see changelog.d/README.md), or label the pull request no-changelog if nobody using Kontena can see the change."
exit 1

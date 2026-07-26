# Contributing to Kontena

Thanks for your interest. Kontena is maintained by one person, so the bar for contributions is high
and the rules below are not negotiable. Reading them before you open a pull request saves everyone
time.

By participating you agree to the [Code of Conduct](CODE_OF_CONDUCT.md).

## Not writing code? Still useful

The high bar below applies to **code in pull requests**. A clear, reproducible bug report or a
well-argued feature idea costs you little and is one of the most helpful things you can send. Open an
[issue](https://github.com/Lionear/Kontena/issues) for either.

## Pull request policy

This project is built in an AI-assisted workflow itself (direction, architecture and acceptance
testing are the maintainer's; a large share of the implementation is written by AI agents
orchestrated through Claude Code). So to be clear: PRs are judged on the result, not on what tool
produced them. That cuts both ways.

- **PRs that have not been checked by the author are rejected outright.** If you used an AI
  assistant, you are responsible for the result: it must build with zero warnings, stay consistent
  with the surrounding code, and read like code you would sign your name to. "My assistant generated
  it" is not a review.
- **PRs that exist to force an AI review or "another opinion" are closed without discussion.**
  Project direction rests with the maintainer; drive-by rewrites do not.
- **Stay in scope.** One PR, one topic. No unrequested refactors, renames or "while I was here"
  changes bundled in.

A PR that does not meet the bar is closed, not line-by-line reviewed. Don't take it personally —
it keeps a one-person project alive.

## Before you open a PR

1. **Open an issue first** for anything beyond a trivial fix, so the approach can be agreed before
   you spend time on it.
2. **Match the codebase.** Conventions to hold: C# / .NET 10, Avalonia + MVVM (CommunityToolkit.Mvvm),
   code and comments in English, one top-level type per file, no new third-party dependencies without
   prior agreement in the issue, comments explain *why* rather than restating the code. House style:
   file-scoped namespaces, nullable enabled, Allman braces, primary constructors, `Async` suffix,
   `ct` as the last parameter.
3. **Build clean and green.** `dotnet build` with zero warnings, and `dotnet test` passing. Run the
   affected flow to verify behaviour (`dotnet run --project src/Kontena.App`); UI changes should be
   checked visually.
4. **Respect the engine boundary.** A new container engine is **not** a change to the host — the
   UI and business logic only ever talk to the Container Engine Abstraction Layer (`IContainerEngine`,
   the CEAL). A new backend is a `src/Kontena.Adapters.*` project (or an external plugin) that
   implements that contract and references **only** `Kontena.Sdk`, the public extension package. No UI
   change, no `Kontena.Core` dependency: model the union of capabilities, expose the intersection
   cleanly, degrade gracefully at the edges.
5. **Mind the credential trust boundary.** Engine credentials and secrets are stored in the OS
   keychain and never written to disk in plaintext, logged, or transmitted anywhere other than the
   engine being connected to. Anything that would change that needs an issue first.
6. **Record it in the changelog.** When the work is finished, add a fragment in
   [`changelog.d/`](changelog.d/README.md) — not an edit to [`CHANGELOG.md`](CHANGELOG.md); see
   *Changelog* below. A finished item that leaves no trace there is not finished.

## Commit style

This repository uses [Conventional Commits](https://www.conventionalcommits.org/):

```
<type>(<scope>): <subject>

[optional body — the WHY, only when the diff doesn't make it obvious]
```

Types: `feat` · `fix` · `refactor` · `chore` · `docs` · `test` · `style` · `perf` · `ci`. Scope
(optional but encouraged) is the component or module, e.g. `feat(engines):`, `fix(app):`. Subject:
short, imperative, lowercase, no trailing period.

## Changelog

Every finished work item ends up in [`CHANGELOG.md`](CHANGELOG.md) under `## [Unreleased]`, so that
from one release to the next it is clear what actually changed — without reading the git log. The
file follows [Keep a Changelog](https://keepachangelog.com/).

**Do not edit `CHANGELOG.md` in a feature branch.** Add a fragment instead — one file per change,
named after your ticket:

```
changelog.d/KON-107.fixed.md
```

Every branch used to append to the same `### Added` block, so two branches in the air at once
conflicted on that one line — and on a file whose conflicts carry no information, since both sides
are additions and the resolution is always "keep both". Two fragments never touch the same path.
See [`changelog.d/README.md`](changelog.d/README.md) for the details.

- The category is in the filename: `added`, `changed`, `deprecated`, `removed`, `fixed`, `security`.
  The commit types map straight onto these: `feat:` → `added`, `fix:` → `fixed`,
  `changed`/`refactor:`/`perf:` → `changed`, `removed` → `removed`.
- Keep it user-facing: describe what changed for the person *using* Kontena, not the class that
  changed.
- Never write a version heading yourself.
- Preview what the next release's notes will read like with
  `python3 tools/changelog-render.py --dry-run`. CI validates the filenames on every PR.
- A fragment can only *add*. To correct an unreleased entry that a later change made wrong, edit that
  entry where it lives — in `CHANGELOG.md` if it was already folded in, or in its own fragment.
- **Releasing is a tag.** The maintainer bumps `<Version>` in `Directory.Build.props`, then pushes a
  `v<semver>` tag (`git tag v0.1.0 && git push origin v0.1.0`). The Build workflow folds the pending
  fragments into `[Unreleased]`, rolls that into a dated `## [0.1.0]` section, removes the fragments,
  and uses the same text as the GitHub release notes.
  The tag is the version — the About screen and the changelog both read it.

## Builds & releases

CI lives in [`.github/workflows`](.github/workflows): `test.yml` runs the unit tests on every push
and PR; `changelog.yml` validates the changelog fragments; `build.yml` publishes cross-platform
builds. Pushing to `main` refreshes the rolling **preview** release; a nightly schedule refreshes
the rolling **nightly** prerelease from `develop`; pushing a `v<semver>` tag cuts a real **stable**
release. The builds are unsigned / not notarized for now.

Packaging is [Velopack](https://velopack.io) (`vpk`), so the same step produces the download and the
update feed the installed app reads — an installer built apart from its feed is one the updater
cannot follow. Each build publishes into a channel named `<platform>-<stream>` — three platforms
times the three streams above (`linux-stable`, `win-nightly`, `osx-preview`, …), and all three
streams are selectable in Settings → Updates, so no feed is published that nobody can subscribe to.
The platform is part of the name because a channel is one feed,
and a mixed feed would offer a Windows package to a Linux install; the app derives the same name
from `Kontena.Core.Models.ReleaseChannel`, so the two sides cannot drift apart. Alongside the
download, a release carries a `.nupkg` and a `releases.<channel>.json` — those are the updater's,
not the user's.

## Dependencies & notices

Bundled open-source dependencies keep their own licenses; the required attribution lives in
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md), generated from the NuGet dependency closure. If your
change adds, removes or bumps a dependency, regenerate it and commit the result
(`python3 tools/generate-third-party-notices.py`; `--check` verifies the committed file is current, and
CI runs it).

## License of contributions

The project is source-available under a split license: `src/Kontena.Sdk` is
[MIT](src/Kontena.Sdk/LICENSE) (the public extension contract), everything else is
[Apache-2.0 with the Commons Clause](LICENSE). By submitting a contribution you agree that it is
licensed under the same terms as the part of the tree it touches. If that doesn't work for you, open
an issue before contributing.

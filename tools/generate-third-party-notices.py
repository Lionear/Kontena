#!/usr/bin/env python3
"""Regenerate THIRD-PARTY-NOTICES.md from the NuGet dependency closure.

Why a script and not a hand-kept list: the shipped closure changes with every dependency bump, so a
manual list rots silently. Everything needed is already in `project.assets.json` (the resolved closure,
transitives included) plus each package's `.nuspec` in the local NuGet cache, and almost every package
carries a machine-readable SPDX expression.

Scope is the shipped set only: projects under `src/`.

Run after changing dependencies, then commit the result:

    python3 tools/generate-third-party-notices.py

`--check` regenerates in memory and exits non-zero if the committed file is out of date, without writing.
Requires a restore to have run (project.assets.json must exist).
"""

import json
import os
import pathlib
import re
import sys
import xml.etree.ElementTree as ET

REPO = pathlib.Path(__file__).resolve().parent.parent
NUGET_CACHE = pathlib.Path(os.environ.get("NUGET_PACKAGES", pathlib.Path.home() / ".nuget" / "packages"))
OUTPUT = REPO / "THIRD-PARTY-NOTICES.md"

# Packages whose .nuspec only carries the deprecated <licenseUrl>: map by hand. Empty today.
LICENSE_URL_FALLBACK: dict[str, str] = {}

# Bundled artwork, which no dependency closure knows about: the backend marks are vendored as path data
# in source (KON-80), so they ship in the binary and belong in the notices like any other bundled thing.
# Hand-kept on purpose — it changes when a backend is added, not when a package is bumped.
ARTWORK = """## Bundled artwork

The backend chips draw each product's own mark, vendored as path data from
[simple-icons](https://github.com/simple-icons/simple-icons) (CC0-1.0), which packages the marks below
as single-path monochrome icons.

| Mark | Where | Trademark |
|---|---|---|
| Docker | `Kontena.Adapters.Docker/DockerBrand.cs` | Docker, Inc. |
| Podman | `Kontena.Adapters.Podman/PodmanBrand.cs` | the Podman project |
| Kubernetes | `Kontena.Adapters.Kubernetes/KubernetesBrand.cs` | The Linux Foundation |
| Apple | `Kontena.App/AppleBrand.cs` | Apple Inc. |

CC0 covers simple-icons' packaging of the artwork, not the marks themselves. Each mark remains its
owner's trademark and is used here nominatively — to name which engine or runtime a backend is. Kontena
is not affiliated with, sponsored by, or endorsed by any of them.

The interface icons are [Lucide](https://lucide.dev) (ISC), in `Kontena.App/Icons.axaml`."""

# Debug-only packages that are excluded from Release builds (IncludeAssets=None), so they never ship in
# the distributed binary and don't belong in the notices.
EXCLUDE = {"AvaloniaUI.DiagnosticsSupport"}


def shipped_assets():
    """project.assets.json for every project that ships (src/), skipping build output."""
    found = [p for p in (REPO / "src").glob("**/project.assets.json") if "/bin/" not in str(p)]
    if not found:
        sys.exit("No project.assets.json under src/ — run `dotnet restore` first.")
    return found


def resolve_packages():
    """name -> version for the whole resolved closure of the shipped projects."""
    packages = {}
    for assets in shipped_assets():
        data = json.loads(assets.read_text())
        for key, lib in data.get("libraries", {}).items():
            if lib.get("type") != "package":
                continue
            name, version = key.split("/", 1)
            if name in EXCLUDE:
                continue
            # Several projects can resolve the same package at different versions; keep the highest so
            # the notice matches what actually ends up next to the executable.
            if name not in packages or _newer(version, packages[name]):
                packages[name] = version
    return packages


def _newer(a, b):
    def parts(v):
        return [int(x) if x.isdigit() else x for x in re.split(r"[.\-+]", v)]

    try:
        return parts(a) > parts(b)
    except TypeError:
        return a > b


def nuspec_path(name, version):
    return NUGET_CACHE / name.lower() / version / f"{name.lower()}.nuspec"


def read_license(name, version):
    """(spdx_or_label, project_url, embedded_licence_text_or_None)."""
    path = nuspec_path(name, version)
    if not path.exists():
        return None, None, None

    xml = re.sub(r'\sxmlns="[^"]+"', "", path.read_text(encoding="utf-8", errors="replace"), count=1)
    meta = ET.fromstring(xml).find("metadata")
    if meta is None:
        return None, None, None

    project_url = (meta.findtext("projectUrl") or "").strip() or None
    license_el = meta.find("license")

    if license_el is not None and license_el.get("type") == "expression":
        return (license_el.text or "").strip(), project_url, None

    if license_el is not None and license_el.get("type") == "file":
        # Keep the path inside the package directory: an absolute path or ../ would otherwise read an
        # arbitrary file straight into the notices output.
        base = path.parent.resolve()
        text_path = (base / (license_el.text or "").strip()).resolve()
        if not text_path.is_relative_to(base):
            return "See licence text below", project_url, None
        text = text_path.read_text(encoding="utf-8", errors="replace") if text_path.exists() else None
        return "See licence text below", project_url, text

    if meta.findtext("licenseUrl"):
        return LICENSE_URL_FALLBACK.get(name, "See project URL"), project_url, None

    return None, project_url, None


def main():
    packages = resolve_packages()
    rows, embedded, unknown = [], [], []

    for name in sorted(packages, key=str.lower):
        version = packages[name]
        spdx, url, text = read_license(name, version)
        if spdx is None:
            unknown.append(f"{name} {version}")
            continue
        rows.append((name, version, spdx, url))
        if text:
            embedded.append((name, version, text.strip()))

    if unknown:
        sys.exit("No licence metadata for:\n  " + "\n  ".join(unknown))

    out = [
        "# Third-party notices",
        "",
        "Kontena bundles the open-source packages and artwork below. Each remains under its own licence;",
        "this file reproduces the attribution those licences require.",
        "",
        "> Generated by `tools/generate-third-party-notices.py` from the NuGet dependency closure, plus",
        "> the bundled-artwork section kept in that script — do not edit this file by hand. Re-run it",
        "> after changing dependencies or vendoring artwork.",
        "",
        f"## Packages ({len(rows)})",
        "",
        "| Package | Version | Licence |",
        "|---|---|---|",
    ]
    for name, version, spdx, url in rows:
        label = f"[{name}]({url})" if url else name
        out.append(f"| {label} | {version} | {spdx} |")

    if embedded:
        out += ["", "## Licence texts", "",
                "Packages that ship their licence as a file rather than an SPDX expression:", ""]
        for name, version, text in embedded:
            out += [f"### {name} {version}", "", "```", text, "```", ""]

    out += ["", ARTWORK, ""]

    rendered = "\n".join(out).rstrip() + "\n"

    if "--check" in sys.argv:
        current = OUTPUT.read_text(encoding="utf-8") if OUTPUT.exists() else ""
        if current != rendered:
            sys.exit(f"{OUTPUT.name} is out of date — re-run {pathlib.Path(__file__).name} and commit the result.")
        print(f"{OUTPUT.name} is up to date — {len(rows)} packages.")
        return

    OUTPUT.write_text(rendered, encoding="utf-8")
    print(f"Wrote {OUTPUT.relative_to(REPO)} — {len(rows)} packages, {len(embedded)} embedded licence texts.")


if __name__ == "__main__":
    main()

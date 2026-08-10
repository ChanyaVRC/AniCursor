#!/usr/bin/env python3
"""Fast, dependency-free checks for the VPM repository and release package."""

from __future__ import annotations

import json
import re
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
PACKAGE_NAME = "com.chanya.ani-cursor"
PACKAGE = ROOT / "Packages" / PACKAGE_NAME
TEXT_SUFFIXES = {".asmdef", ".cs", ".json", ".md", ".py", ".yml", ".yaml"}
FORBIDDEN = ("c:/users/", "f:/vrcavatargit/", "assets/modified/", "_buildtools")


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit(message)


def main() -> None:
    manifest_path = PACKAGE / "package.json"
    require(manifest_path.is_file(), f"Missing {manifest_path}")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))

    require(manifest.get("name") == PACKAGE_NAME, "Unexpected package name")
    require(bool(re.fullmatch(r"\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?", manifest.get("version", ""))),
            "Package version is not SemVer")
    author = manifest.get("author", {})
    require(all(author.get(field) for field in ("name", "email", "url")),
            "Package author must include name, email, and url")
    require("com.vrchat.avatars" in manifest.get("vpmDependencies", {}),
            "Missing VRChat Avatars dependency")
    require("nadena.dev.modular-avatar" in manifest.get("vpmDependencies", {}),
            "Missing Modular Avatar dependency")

    for relative in (
        ".github/workflows/release.yml",
        ".github/workflows/build-listing.yml",
        "Website/index.html",
        f"Packages/{PACKAGE_NAME}/LICENSE.md",
    ):
        require((ROOT / relative).is_file(), f"Missing {relative}")

    violations: list[str] = []
    for path in PACKAGE.rglob("*"):
        if not path.is_file() or path.suffix.lower() not in TEXT_SUFFIXES:
            continue
        relative = path.relative_to(PACKAGE).as_posix()
        if relative.lower().startswith("tests/"):
            continue
        content = path.read_text(encoding="utf-8").replace("\\", "/").lower()
        for marker in FORBIDDEN:
            if marker in content:
                violations.append(f"{relative}: {marker}")
    require(not violations, "Non-portable package content:\n" + "\n".join(violations))

    for script in (PACKAGE / "Tools~" / "Pipeline").glob("*.py"):
        compile(script.read_text(encoding="utf-8"), str(script), "exec")

    print(f"Validated {PACKAGE_NAME}@{manifest['version']}")


if __name__ == "__main__":
    main()

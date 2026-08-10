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

    release_workflow = (ROOT / ".github" / "workflows" / "release.yml").read_text(encoding="utf-8")
    require("unitypackage" not in release_workflow.lower(),
            "Release workflow must remain VPM-only; UnityPackage output is not supported")

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

    prepare = (PACKAGE / "Tools~" / "Pipeline" / "prepare_ani_cursor.py").read_text(encoding="utf-8")
    blender = (PACKAGE / "Tools~" / "Pipeline" / "build_ani_cursor_blender.py").read_text(encoding="utf-8")
    window = (PACKAGE / "Editor" / "AniCursorPipelineWindow.cs").read_text(encoding="utf-8")
    builder = (PACKAGE / "Editor" / "AniCursorUnityBuilder.cs").read_text(encoding="utf-8")
    require("SCHEMA_VERSION = 3" in prepare, "Prepare stage must emit manifest schema 3")
    require('manifest.get("schema_version") != 3' in blender,
            "Blender stage must require manifest schema 3")
    require("schema_version 3 is supported" in window and
            "schema_version 3 is supported" in builder,
            "Unity stages must require manifest schema 3")

    generator_sources = prepare + blender + window + builder
    removed_contracts = (
        "RightHandAnchor",
        "Position_Adjust",
        "CursorObjects",
        "legacy_renderer_path",
        "BuildFromRequestFile",
        "unity_build_request.json",
    )
    require(not any(token in generator_sources for token in removed_contracts),
            "Removed migration contract remains in generator sources")

    print(f"Validated {PACKAGE_NAME}@{manifest['version']}")


if __name__ == "__main__":
    main()

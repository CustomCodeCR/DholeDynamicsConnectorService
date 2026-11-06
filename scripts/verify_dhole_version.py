#!/usr/bin/env python3
"""Dhole universal version gate for GitHub Actions (no dependencies)."""
import json
import os
import re
import subprocess
import sys
from pathlib import Path
from xml.etree import ElementTree as ET

SEMVER = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")

def parse(value):
    if not isinstance(value, str) or not SEMVER.fullmatch(value):
        raise ValueError(f"Invalid MAJOR.MINOR.PATCH: {value!r}")
    return tuple(map(int, value.split(".")))

def source_at(ref, path):
    p = subprocess.run(["git", "show", f"{ref}:{path}"], capture_output=True, text=True)
    return p.stdout if p.returncode == 0 else None

def previous_version(ref):
    if not ref or set(ref) == {"0"}:
        return None
    previous = source_at(ref, "version.json")
    if previous is not None:
        return json.loads(previous)["version"]
    old_pkg = source_at(ref, "package.json")
    if old_pkg is not None:
        return json.loads(old_pkg).get("version")
    return None

def main():
    root = Path(__file__).resolve().parent.parent
    os.chdir(root)
    metadata = json.loads(Path("version.json").read_text(encoding="utf-8"))
    version = metadata["version"]
    parse(version)
    parse(metadata["ecosystemVersion"])
    assert metadata["ecosystem"] == "Dhole"
    assert metadata["component"].startswith("Dhole")
    assert metadata["schemaVersion"] == 1
    assert metadata["releaseId"].strip()
    assert parse(metadata["ecosystemVersion"]) >= parse(version), "Global ecosystem version cannot predate component"
    assert Path("AGENTS.md").is_file(), "Missing permanent Codex instructions"
    changelog = Path("CHANGELOG.md").read_text(encoding="utf-8")
    assert f"## [{version}]" in changelog, "CHANGELOG missing current release"

    if (root / "package.json").exists():
        pkg = json.loads(Path("package.json").read_text(encoding="utf-8"))
        assert pkg["version"] == version, "package.json version mismatch"
        if Path("package-lock.json").exists():
            lock = json.loads(Path("package-lock.json").read_text(encoding="utf-8"))
            assert lock["version"] == version
            assert lock["packages"][""]["version"] == version

    if (root / "Directory.Build.targets").exists():
        xml = ET.parse("Directory.Build.targets")
        values = [n.text for n in xml.iter() if n.tag == "Version"]
        assert values and all(v == version for v in values), "MSBuild version mismatch"
        for proj in root.rglob("*.csproj"):
            if any(seg in {"bin", "obj", "node_modules"} for seg in proj.parts):
                continue
            xml_project = ET.parse(proj)
            overrides = [n.text for n in xml_project.iter() if n.tag == "Version"]
            assert all(v == version for v in overrides), f"{proj} overrides the release version"
    elif metadata["component"] not in {"DholeWeb", "DholeSystem"}:
        raise AssertionError("Backend missing Directory.Build.targets assembly version")

    if (root / "release-manifest.json").exists():
        manifest = json.loads(Path("release-manifest.json").read_text(encoding="utf-8"))
        assert manifest["ecosystemVersion"] == version
        assert manifest["releaseId"] == metadata["releaseId"]
        assert len(manifest["components"]) >= 15
        assert all(item["version"] == version for item in manifest["components"])

    ref = os.environ.get("DH_VERSION_BASE", "").strip()
    prev = previous_version(ref)
    if prev is not None:
        assert parse(version) > parse(prev), (
            f"Version must increase for every change: base={prev}, candidate={version}"
        )
    print(f"PASS Dhole {metadata['component']} {version}, base={prev or 'unversioned legacy'}")

if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, AssertionError, ET.ParseError) as e:
        print(f"FAIL Dhole version policy: {e}", file=sys.stderr)
        sys.exit(1)

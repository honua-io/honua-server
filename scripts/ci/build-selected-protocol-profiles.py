#!/usr/bin/env python3
"""Build protocol profiles selected by protocol-profile-selection.py.

The selector stays dry-run. This script reads its JSON and compiles each
selected profile except ``all``, which the calling workflow already built.
Honua.Server writes those profiles under bin/protocol-<name> and
obj/protocol-<name>, so they do not replace the default output tree.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SERVER = "src/Honua.Server/Honua.Server.csproj"
SUPPORTED = ("gsr", "ogc-api", "ogc-api-classic", "all")


def profiles_to_build(payload):
    profiles = payload.get("profiles")
    if not isinstance(profiles, list) or not profiles:
        raise SystemExit("protocol profile selection has no profiles array")
    selected = []
    for profile in profiles:
        if profile not in SUPPORTED:
            raise SystemExit(f"unsupported protocol profile '{profile}'")
        if profile != "all" and profile not in selected:
            selected.append(profile)
    return selected


def build_command(profile):
    return [
        "dotnet",
        "build",
        SERVER,
        "--configuration",
        "Release",
        "-p:HonuaProtocolProfile=" + profile,
        "-p:TreatWarningsAsErrors=true",
        "-p:RunAnalyzers=true",
        "-p:UseSharedCompilation=false",
    ]


def main(argv=None):
    parser = argparse.ArgumentParser(description="Build selected Honua protocol profiles")
    parser.add_argument("--selection", required=True, help="JSON written by protocol-profile-selection.py")
    parser.add_argument("--dry-run", action="store_true", help="Print the build commands and do not run them")
    args = parser.parse_args(argv)
    try:
        payload = json.loads(Path(args.selection).read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise SystemExit(f"protocol profile selection is unreadable: {exc}") from exc

    selected = profiles_to_build(payload)
    if not selected:
        print("no extra protocol profiles to build")
        return 0

    for profile in selected:
        command = build_command(profile)
        print("building protocol profile " + profile, flush=True)
        if args.dry_run:
            print(" ".join(command))
            continue
        completed = subprocess.run(command, cwd=ROOT, check=False)
        if completed.returncode != 0:
            return completed.returncode
    return 0


if __name__ == "__main__":
    sys.exit(main())

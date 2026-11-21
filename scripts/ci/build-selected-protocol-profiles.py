#!/usr/bin/env python3
"""Build protocol profiles selected by protocol-profile-selection.py.

The selector stays dry-run. This script reads its JSON and compiles each
selected profile except ``all``, which the calling workflow already built.
Honua.Server writes those profiles under bin/protocol-<name> and
obj/protocol-<name>, so they do not replace the default output tree.

Honua.Server always compiles with analyzers. When the producer built without
analyzers (PRODUCER_RUN_ANALYZERS=false), referenced projects are first brought
up to date in that same mode and then reused, so a profile does not recompile
every referenced project with analyzers turned on.
"""

from __future__ import annotations

import argparse
import json
import os
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


def dotnet_build(profile, *extra):
    return [
        "dotnet",
        "build",
        SERVER,
        "--configuration",
        "Release",
        "-maxcpucount:4",
        "-p:HonuaProtocolProfile=" + profile,
        "-p:TreatWarningsAsErrors=true",
        "-p:UseSharedCompilation=false",
        *extra,
    ]


def analyzer_build(profile):
    return [dotnet_build(profile, "-p:RunAnalyzers=true")]


def reference_reuse_build(profile):
    return [
        dotnet_build(profile, "-p:RunAnalyzers=false", "-t:ResolveProjectReferences"),
        dotnet_build(profile, "--no-restore", "-p:RunAnalyzers=true", "-p:BuildProjectReferences=false"),
    ]


def profile_builder():
    if os.environ.get("PRODUCER_RUN_ANALYZERS", "true").strip().lower() == "false":
        return reference_reuse_build
    return analyzer_build


def print_commands(commands):
    for command in commands:
        print(" ".join(command))
    return 0


def run_commands(commands):
    for command in commands:
        completed = subprocess.run(command, cwd=ROOT, check=False)
        if completed.returncode != 0:
            return completed.returncode
    return 0


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

    build = profile_builder()
    execute = print_commands if args.dry_run else run_commands
    for profile in selected:
        print("building protocol profile " + profile, flush=True)
        returncode = execute(build(profile))
        if returncode != 0:
            return returncode
    return 0


if __name__ == "__main__":
    sys.exit(main())

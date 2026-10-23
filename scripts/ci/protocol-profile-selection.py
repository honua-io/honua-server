#!/usr/bin/env python3
"""Dry-run selector for Honua protocol profiles.

Prints the profiles, project closure, and shards a change needs. It does not
restore, build, publish, or start a container. Unknown profiles fail.
Uncertainty about a changed path fails closed to full validation of every
supported profile without multiplying shards by the profile count.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
DEFINITION = ROOT / "eng" / "protocol-profiles.v1.json"
SHARDS = ROOT / ".github" / "ci-shards.json"
FORMAT = "honua.protocol-profile-selection/v1"

WIDEN_EXACT = {
    "eng/protocol-profiles.v1.json",
    "eng/Honua.ProtocolProfiles.props",
    "eng/Honua.BuildProfiles.props",
    "src/Honua.Server/Honua.Server.csproj",
    "src/Honua.Server/Program.cs",
    "src/Honua.Server/Features/Infrastructure/Hosting/FeatureRegistrationExtensions.cs",
    "src/Honua.Server/Features/Infrastructure/Hosting/ProtocolProfile.cs",
    "src/Honua.Server/Startup/JsonContextRegistration.cs",
    "Dockerfile",
    "docker/Dockerfile.aot",
    "docker/Dockerfile.protocol-profile-runtime",
    "scripts/ci/protocol-profile-selection.py",
    "scripts/ci/build-selected-protocol-profiles.py",
    "scripts/ci/prune-protocol-profile-outputs.py",
    ".github/ci-shards.json",
    "Directory.Build.targets",
}

MODULE_PREFIXES = (
    ("src/Honua.Protocols.GeoServices/", "geoservices"),
    ("src/Honua.Protocols.OgcApi/", "ogc-api"),
    ("src/Honua.Protocols.OgcClassic/", "ogc-classic"),
    ("src/Honua.Protocols.OData/", "odata"),
    ("src/Honua.Protocols.Stac/", "stac"),
    ("src/Honua.Protocols.SensorThings/", "sensorthings"),
    ("src/Honua.Protocols.Scene/", "scene"),
    ("src/Honua.Scene/", "scene"),
    ("src/Honua.Server/Features/Protocols/Grpc/", "grpc"),
    ("src/Honua.Ai/Features/Protocols/Mcp/", "mcp"),
)

SHARD_HINTS = {
    "geoservices": ("GeoServices", "FeatureServer", "Geocoding", "GPServer"),
    "ogc-api": ("OGC API",),
    "ogc-classic": ("OGC Classic", "WFS"),
    "odata": ("OData",),
    "stac": ("STAC",),
    "sensorthings": ("SensorThings",),
    "scene": ("Scene",),
    "grpc": ("gRPC",),
    "mcp": ("MCP",),
}


def load_definition():
    try:
        document = json.loads(DEFINITION.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise SystemExit(f"protocol profile definition is unreadable: {exc}") from exc
    profiles = document.get("profiles")
    supported = document.get("supportedProfiles")
    if document.get("defaultProfile") != "all" or supported != ["gsr", "ogc-api", "ogc-api-classic", "all"]:
        raise SystemExit("protocol profile definition must default to all and list exactly gsr, ogc-api, ogc-api-classic, all")
    if not isinstance(profiles, dict) or list(profiles) != supported:
        raise SystemExit("protocol profile definition profiles do not match supportedProfiles")
    return document


def classify(path):
    normalized = path.replace("\\", "/").lstrip("./")
    if normalized in WIDEN_EXACT or normalized.startswith(".github/workflows/"):
        return "widen", None
    for prefix, module in MODULE_PREFIXES:
        if normalized.startswith(prefix):
            return "module", module
    if normalized.startswith("docs/") and normalized.endswith(".md"):
        return "docs", None
    return "unmap", None


def shard_names():
    try:
        document = json.loads(SHARDS.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return []
    names = []
    if isinstance(document, dict):
        values = document.get("shards", document.values())
    else:
        values = document
    for entry in values:
        if isinstance(entry, dict) and isinstance(entry.get("name"), str):
            names.append(entry["name"])
    return names


def select_shards(modules, names):
    hints = [hint for module in modules for hint in SHARD_HINTS.get(module, ())]
    selected = []
    for name in names:
        if any(hint in name for hint in hints) and name not in selected:
            selected.append(name)
    return selected


def projects_for(document, profiles):
    modules = document["modules"]
    paths = []
    for profile in profiles:
        for module in document["profiles"][profile]["modules"]:
            for project in modules[module].get("projects", []):
                if project not in paths:
                    paths.append(project)
    server = document["serverProject"]
    if server not in paths:
        paths.append(server)
    return paths


def identity(profile, modules, build_profile):
    joined = "+".join(modules)
    return f"honua.protocol-profile/v1|{profile}|{joined}|build={build_profile}"


def git_changed_files():
    base = os.environ.get("BASE_REF", "")
    head = os.environ.get("HEAD_REF", "")
    if not base or not head:
        return None
    import subprocess

    completed = subprocess.run(
        ["git", "diff", "--name-only", base, head],
        cwd=ROOT,
        check=False,
        capture_output=True,
        text=True,
    )
    if completed.returncode != 0:
        return None
    return [line for line in completed.stdout.splitlines() if line.strip()]


def decide(document, changed, requested_profile):
    supported = document["supportedProfiles"]
    default_profile = document["defaultProfile"]
    if requested_profile is not None and requested_profile not in supported:
        raise SystemExit(
            f"Unsupported HonuaProtocolProfile '{requested_profile}'. "
            "Supported profiles: gsr, ogc-api, ogc-api-classic, all."
        )

    if changed is None:
        return full(document, "changed files could not be resolved; validating every profile")

    modules = []
    widen = False
    for path in changed:
        kind, module = classify(path)
        if kind == "widen" or kind == "unmap":
            widen = True
        elif kind == "module" and module not in modules:
            modules.append(module)

    if widen:
        return full(document, "a shared or unmapped path changed; validating every profile")

    if not modules:
        profiles = [requested_profile or default_profile]
        return payload(
            document,
            mode="none",
            profiles=profiles,
            shards=[],
            reason="no protocol-bearing change; default profile only",
        )

    profiles = [
        name
        for name in supported
        if any(module in document["profiles"][name]["modules"] for module in modules)
    ]
    if requested_profile is not None:
        profiles = [requested_profile]
    return payload(
        document,
        mode="affected",
        profiles=profiles,
        shards=select_shards(modules, shard_names()),
        reason="changed files map to " + ",".join(modules),
    )


def full(document, reason):
    return payload(
        document,
        mode="full",
        profiles=list(document["supportedProfiles"]),
        shards="run_all",
        reason=reason,
    )


def payload(document, mode, profiles, shards, reason):
    build_profile = "full"
    return {
        "format": FORMAT,
        "mode": mode,
        "defaultProfile": document["defaultProfile"],
        "profiles": profiles,
        "projects": projects_for(document, profiles),
        "shards": shards,
        "build": "managed",
        "publish": "native-aot-supported",
        "artifactIdentity": [
            identity(profile, document["profiles"][profile]["modules"], build_profile) for profile in profiles
        ],
        "reason": reason,
    }


def main(argv=None):
    parser = argparse.ArgumentParser(description="Dry-run Honua protocol profile selection")
    parser.add_argument("--dry-run", action="store_true", help="Accepted for callers. Selection never builds.")
    parser.add_argument("--execute", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--profile", default=None)
    parser.add_argument("--changed-file", action="append", default=[])
    parser.add_argument("--changed-from-git", action="store_true")
    args = parser.parse_args(argv)
    if args.execute:
        print("protocol-profile-selection is dry-run only and does not build or publish", file=sys.stderr)
        return 2

    document = load_definition()
    if args.changed_from_git:
        changed = git_changed_files()
    elif args.changed_file:
        changed = args.changed_file
    else:
        changed = []
    print(json.dumps(decide(document, changed, args.profile), sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())

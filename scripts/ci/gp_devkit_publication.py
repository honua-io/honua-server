#!/usr/bin/env python3
"""Publish Honua.Geoprocessing.Cli and keep candidate packs out of that claim.

Installed authoring packs a source-built nupkg and exercises it. That candidate
is not a published package. Publication is an explicit canonical-trunk dispatch
that pushes the repository-version tool to GitHub Packages and records the
identity honua-release#615 consumes. Supported-OS host-debug receipts are a
separate handoff and never establish published availability.
"""

import argparse
import hashlib
import json
import os
import platform
import re
import subprocess
import sys
import zipfile
import xml.etree.ElementTree as ET
from pathlib import Path

REPOSITORY = "honua-io/honua-server"
PACKAGE_ID = "Honua.Geoprocessing.Cli"
TOOL_COMMAND = "honua-gp-devkit"
FEED = "https://nuget.pkg.github.com/honua-io/index.json"
SCHEMA = "honua.gp-devkit-publication/v1"
HOST_DEBUG_SCHEMA = "honua.gp-devkit-host-debug/v1"
HANDOFF_SCHEMA = "honua.gp-devkit-host-debug-handoff/v1"
HOST_DEBUG_WORKFLOW = ".github/workflows/gp-installed-authoring.yml"
SUPPORTED_OS = ("linux", "macos", "windows")
HANDED_TO = "honua-release#615"


def require(condition, finding):
    if not condition:
        raise ValueError(finding)


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def write_json(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def package_identity(path):
    path = Path(path)
    require(path.is_file(), "devkit package is missing")
    with zipfile.ZipFile(path) as package:
        specs = [name for name in package.namelist() if name.endswith(".nuspec")]
        require(len(specs) == 1, "expected exactly one package manifest")
        metadata = ET.fromstring(package.read(specs[0]))
    identity = metadata.find(".//{*}id")
    version = metadata.find(".//{*}version")
    require(identity is not None and identity.text == PACKAGE_ID, "unexpected devkit package id")
    require(version is not None and version.text, "missing devkit package version")
    return {"package_id": identity.text, "version": version.text, "sha256": digest(path)}


def candidate_receipt(package):
    identity = package_identity(package)
    return {
        "schema": SCHEMA,
        "package_id": identity["package_id"],
        "version": identity["version"],
        "nupkg_sha256": identity["sha256"],
        "feed": FEED,
        "tool_command": TOOL_COMMAND,
        "published": False,
        "establishes_published_availability": False,
        "handed_to": HANDED_TO,
        "reason": "Installed authoring and checkout packs do not publish this package.",
    }


def require_publication(environment):
    require(environment.get("GITHUB_EVENT_NAME") == "workflow_dispatch", "publication requires explicit dispatch")
    require(environment.get("GITHUB_REF") == "refs/heads/trunk", "publication requires canonical trunk")
    require(environment.get("GITHUB_REPOSITORY") == REPOSITORY, "unexpected repository")
    require(environment.get("PUBLISH_DEVKIT") == "true", "publication was not explicitly selected")
    sha = environment.get("GITHUB_SHA", "")
    run = environment.get("GITHUB_RUN_ID", "")
    attempt = environment.get("GITHUB_RUN_ATTEMPT", "")
    require(re.fullmatch(r"[0-9a-f]{40}", sha), "missing exact source SHA")
    require(re.fullmatch(r"[1-9][0-9]*", run), "missing authoritative run ID")
    require(re.fullmatch(r"[1-9][0-9]*", attempt), "missing authoritative run attempt")
    token = environment.get("NUGET_AUTH_TOKEN", "")
    require(isinstance(token, str) and token.strip() and "\n" not in token and "\r" not in token,
            "missing package publish credential")
    return {"source_sha": sha, "run_id": run, "run_attempt": attempt, "repository": REPOSITORY}


def publish(package, environment, output):
    origin = require_publication(environment)
    identity = package_identity(package)
    require("installed." not in identity["version"], "candidate version suffixes are not a published release")
    subprocess.run(
        ["dotnet", "nuget", "push", str(package), "--source", FEED, "--api-key", environment["NUGET_AUTH_TOKEN"], "--skip-duplicate"],
        check=True,
    )
    receipt = {
        "schema": SCHEMA,
        "package_id": identity["package_id"],
        "version": identity["version"],
        "nupkg_sha256": identity["sha256"],
        "feed": FEED,
        "tool_command": TOOL_COMMAND,
        "source_sha": origin["source_sha"],
        "run_id": origin["run_id"],
        "run_attempt": origin["run_attempt"],
        "published": True,
        "establishes_published_availability": True,
        "handed_to": HANDED_TO,
        "host_debug": {
            "establishes_published_availability": False,
            "workflow": HOST_DEBUG_WORKFLOW,
            "note": "Supported-OS host-debug receipts are a separate handoff and do not establish published availability.",
        },
    }
    write_json(output, receipt)
    return receipt


def write_host_debug_receipt(path, package_version):
    system = {"Linux": "linux", "Windows": "windows", "Darwin": "macos"}.get(platform.system())
    require(system in SUPPORTED_OS, "unsupported host-debug operating system")
    require(isinstance(package_version, str) and package_version.strip() and not any(ch.isspace() for ch in package_version),
            "missing package version")
    receipt = {
        "schema": HOST_DEBUG_SCHEMA,
        "published": False,
        "establishes_published_availability": False,
        "os": system,
        "package_id": PACKAGE_ID,
        "package_version": package_version,
        "status": "succeeded",
        "source": "installed-candidate",
    }
    write_json(path, receipt)
    return receipt


def host_debug_handoff(paths):
    receipts = []
    for path in paths:
        receipt = json.loads(Path(path).read_text(encoding="utf-8"))
        require(receipt.get("schema") == HOST_DEBUG_SCHEMA, "invalid host-debug receipt")
        require(receipt.get("published") is False, "host-debug receipt must not claim publication")
        require(receipt.get("establishes_published_availability") is False,
                "host-debug evidence is not published availability")
        require(receipt.get("status") == "succeeded", "host-debug evidence did not succeed")
        require(receipt.get("package_id") == PACKAGE_ID, "unexpected devkit package")
        require(receipt.get("os") in SUPPORTED_OS, "unsupported host-debug operating system")
        require(isinstance(receipt.get("package_version"), str) and receipt["package_version"], "missing package version")
        receipts.append(receipt)
    systems = sorted(receipt["os"] for receipt in receipts)
    require(systems == list(SUPPORTED_OS), "host-debug handoff requires linux, windows, and macos")
    return {
        "schema": HANDOFF_SCHEMA,
        "published": False,
        "establishes_published_availability": False,
        "operating_systems": systems,
        "handed_to": HANDED_TO,
        "workflow": HOST_DEBUG_WORKFLOW,
        "receipts": receipts,
    }


def handoff_directory(directory):
    paths = sorted(path for path in Path(directory).rglob("*.json") if path.is_file())
    require(paths, "no host-debug receipts")
    return host_debug_handoff(paths)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("publish", "handoff", "candidate"))
    parser.add_argument("--nupkg", type=Path)
    parser.add_argument("--receipts", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(argv)
    if args.action == "publish":
        require(args.nupkg is not None and args.nupkg.is_dir(), "publish requires the packed devkit directory")
        packages = sorted(args.nupkg.glob(f"{PACKAGE_ID}.*.nupkg"))
        require(len(packages) == 1, "expected exactly one packed devkit nupkg")
        publish(packages[0], os.environ, args.output)
    elif args.action == "candidate":
        require(args.nupkg is not None and args.nupkg.is_file(), "candidate requires one nupkg")
        write_json(args.output, candidate_receipt(args.nupkg))
    else:
        require(args.receipts is not None, "handoff requires a receipt directory")
        write_json(args.output, handoff_directory(args.receipts))


if __name__ == "__main__":
    try:
        main()
    except ValueError as exc:
        print(f"gp-devkit publication refused: {exc}", file=sys.stderr)
        sys.exit(1)

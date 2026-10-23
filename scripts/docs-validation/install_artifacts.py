#!/usr/bin/env python3
"""Validate published installation examples without compiling the server."""
from __future__ import annotations

import argparse
import json
import re
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MANIFEST = "docs/get-started/install-artifacts.json"
INSTALL_PAGES = tuple(f"docs/get-started/{name}.md" for name in (
    "quickstart", "linux-packages", "windows-packages"))
CLIENT_PAGES = tuple(f"docs/get-started/{name}.md" for name in (
    "first-dataset", "first-map", "registry-clients")) + (
    "docs/sdks/dotnet/dotnet-getting-started.md",)
VERSION = r"[0-9]+\.[0-9]+\.[0-9]+(?:[-+][A-Za-z0-9.-]+)?"
IMAGE_REFERENCE = re.compile(r"ghcr\.io/honua-io/honua-server(?::[\w.-]+|@sha256:[0-9a-f]{64})")
IMAGE_ASSIGNMENT = re.compile(r"(?:HONUA_IMAGE=|\$Image\s*=\s*['\"])([^\s'\"\\]+)")
# The release channel tag is what the docs and the executable-docs gate certify.
# It is not published until release promotion, so the anonymous install proof
# pulls serverImageProof instead of failing the pull with "manifest unknown".
RELEASE_CHANNEL_IMAGE = "ghcr.io/honua-io/honua-server:2026.1-rc"


def _image_reference(image: str) -> bool:
    return bool(re.fullmatch(IMAGE_REFERENCE, image))


def image_to_pull(manifest: dict) -> str:
    """Image the anonymous proof pulls.

    Documented assignments stay on serverImage. A release-channel tag that is
    not published yet pulls serverImageProof, which must already exist.
    """
    image = manifest["serverImage"]
    proof = manifest.get("serverImageProof")
    if image == RELEASE_CHANNEL_IMAGE and proof:
        return proof
    return image


FENCES = re.compile(r"^```([^\n]*)\n(.*?)^```\s*$", re.M | re.S)


def check(root: Path = ROOT) -> list[str]:
    manifest = json.loads((root / MANIFEST).read_text())
    image = manifest["serverImage"]
    if not _image_reference(image):
        raise ValueError("serverImage must name a tagged or digest-pinned Honua public image")
    if image.endswith(":latest"):
        raise ValueError("serverImage must select an explicit version, not latest")
    proof = manifest.get("serverImageProof")
    if proof is not None and not _image_reference(proof):
        raise ValueError("serverImageProof must name a tagged or digest-pinned Honua public image")
    problems = []
    pages = {path: (root / path).read_text() for path in INSTALL_PAGES + CLIENT_PAGES}
    for path in INSTALL_PAGES:
        if "customer-install-manifest.json" not in pages[path]:
            problems.append(f"{path}: installation instructions must reference customer-install-manifest.json")
        pins = IMAGE_ASSIGNMENT.findall(pages[path])
        if not pins or any(pin != image for pin in pins):
            problems.append(f"{path}: image assignments must match {MANIFEST}: {image}")
    for package, version in manifest["packages"].items():
        if not re.fullmatch(VERSION, version):
            raise ValueError(f"{package}: expected an explicit package version")
        name = re.escape(package)
        # Executable pip/npm/NuGet pins, registry tables, and adjacent prose.
        pattern = re.compile(
            rf"(?<![\w.-]){name}(?:==|@| --version |`? +`?)({VERSION})(?![\w.-])")
        found = False
        for path, text in pages.items():
            pins = pattern.findall(text)
            found |= bool(pins)
            if any(pin != version for pin in pins):
                problems.append(f"{path}: {package} versions must match {MANIFEST}: {version}")
        if not found:
            problems.append(f"{package}: no versioned example found in the installation journey")
    return problems


def needs_smoke(base: str, root: Path = ROOT) -> bool:
    """Only artifact or executable-procedure changes need Docker on a PR.

    Harness changes also run the proof. Missing base files fail toward running
    it, and arbitrary non-prose docs remain governed by the normal CI router.
    """
    changed = subprocess.run(
        ["git", "diff", "--name-only", base, "HEAD"], cwd=root,
        check=True, capture_output=True, text=True).stdout.splitlines()
    if any(path.startswith("scripts/docs-validation/") or path in {
        ".github/workflows/docs-install-gate.yml", ".github/workflows/pr-gate.yml"
    } for path in changed):
        return True
    for path in (MANIFEST, *INSTALL_PAGES):
        old = subprocess.run(["git", "show", f"{base}:{path}"], cwd=root,
                             capture_output=True, text=True)
        if old.returncode or not (root / path).is_file():
            return True
        current = (root / path).read_text()
        if path == MANIFEST:
            if json.loads(old.stdout) != json.loads(current):
                return True
        elif FENCES.findall(old.stdout) != FENCES.findall(current):
            return True
    return False


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--smoke-base", help="also print whether this PR needs the Docker proof")
    parser.add_argument("--github-output", type=Path)
    args = parser.parse_args()
    try:
        problems = check()
        if problems:
            raise ValueError("\n".join(problems))
        print("Installation artifact references are consistent.")
        if args.smoke_base:
            result = f"smoke={str(needs_smoke(args.smoke_base)).lower()}\n"
            print(result, end="")
            if args.github_output:
                with args.github_output.open("a") as output:
                    output.write(result)
        return 0
    except (OSError, ValueError, KeyError, subprocess.CalledProcessError) as error:
        print(f"::error::{error}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

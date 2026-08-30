#!/usr/bin/env python3
"""Generate or verify the repository's shipped-example inventory."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
MANIFEST = ROOT / "examples" / "manifest.json"
FENCE = re.compile(r"^ {0,3}```(?P<language>[^\s`]*)")
CUSTOMER_PATHS = {
    "samples/gp-local-dev/submit-buffer.sh": "gp-local-dev",
    "scripts/demos/run-mobile-offline-demo.sh": "mobile-offline",
    "scripts/demos/run-stac-ops-demo.sh": "stac-ops",
}


def shipped_files(*pathspecs: str) -> list[Path]:
    """Return tracked, staged, or non-ignored files that would ship from Git."""
    completed = subprocess.run(
        ["git", "ls-files", "--cached", "--others", "--exclude-standard", "--", *pathspecs],
        cwd=ROOT,
        check=True,
        capture_output=True,
        text=True,
    )
    return [ROOT / value for value in sorted(set(completed.stdout.splitlines()))]


def stable_id(kind: str, path: str, suffix: str = "") -> str:
    value = f"{kind}:{path}:{suffix}".encode()
    return hashlib.sha256(value).hexdigest()[:16]


def entry(kind: str, path: str, suffix: str = "", **extra: object) -> dict[str, object]:
    validation = {
        "status": "not-validated",
        "reason": "Inventoried in wave 1; no execution evidence is claimed.",
    }
    if path in CUSTOMER_PATHS:
        validation = {
            "status": "scheduled-nightly",
            "reason": (
                "Selected for execution by the advisory nightly lane; "
                "no successful run is claimed by this manifest."
            ),
            "runner": "scripts/examples/validate-customer-paths.sh",
            "scenario": CUSTOMER_PATHS[path],
        }
    result: dict[str, object] = {
        "id": stable_id(kind, path, suffix),
        "kind": kind,
        "path": path,
        "validation": validation,
    }
    result.update(extra)
    return result


def docs_entries() -> list[dict[str, object]]:
    result: list[dict[str, object]] = []
    paths = shipped_files("README.md", "docs/**/*.md")
    for path in paths:
        relative = path.relative_to(ROOT).as_posix()
        ordinal = 0
        inside_fence = False
        for line_number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
            match = FENCE.match(line)
            if match is None:
                continue
            if inside_fence:
                inside_fence = False
                continue
            inside_fence = True
            ordinal += 1
            result.append(entry(
                "docs-fence",
                relative,
                str(ordinal),
                fence=ordinal,
                line=line_number,
                language=match.group("language") or "plain",
            ))
    return result


def file_entries() -> list[dict[str, object]]:
    result: list[dict[str, object]] = []
    for path in shipped_files("samples/**"):
        result.append(entry("sample-asset", path.relative_to(ROOT).as_posix()))
    for path in shipped_files("docs/**/examples/**"):
        result.append(entry("docs-example-asset", path.relative_to(ROOT).as_posix()))
    for path in shipped_files("scripts/demos/*", "scripts/dev/*sample*", "scripts/ci/*quickstart*"):
        result.append(entry("quickstart-adjacent-script", path.relative_to(ROOT).as_posix()))
    return result


def build_manifest() -> dict[str, object]:
    entries = docs_entries() + file_entries()
    entries.sort(key=lambda item: (str(item["path"]), str(item["kind"]), str(item["id"])))
    counts: dict[str, int] = {}
    for item in entries:
        status = str(item["validation"]["status"])  # type: ignore[index]
        counts[status] = counts.get(status, 0) + 1
    return {
        "schemaVersion": 1,
        "scope": {
            "docs": "Every fenced block in README.md and docs/**/*.md",
            "samples": "Every file below samples/",
            "docsExampleAssets": "Every file below a docs/**/examples/ directory",
            "quickstartAdjacentScripts": "scripts/demos/*, scripts/dev/*sample*, scripts/ci/*quickstart*",
        },
        "statusDefinitions": {
            "scheduled-nightly": "The shipped example is configured to execute against a locally built candidate; the manifest does not claim that an unobserved run passed.",
            "not-validated": "The example is inventoried, but wave 1 has not produced execution evidence for it.",
        },
        "summary": {"total": len(entries), "byStatus": counts},
        "entries": entries,
    }


def render() -> str:
    return json.dumps(build_manifest(), indent=2, sort_keys=False) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    expected = render()
    if args.check:
        actual = MANIFEST.read_text(encoding="utf-8") if MANIFEST.exists() else ""
        if actual != expected:
            print("examples/manifest.json is stale; run scripts/examples/generate-manifest.py", file=sys.stderr)
            return 1
        print(f"example manifest is complete ({len(build_manifest()['entries'])} entries)")
        return 0
    MANIFEST.parent.mkdir(parents=True, exist_ok=True)
    MANIFEST.write_text(expected, encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

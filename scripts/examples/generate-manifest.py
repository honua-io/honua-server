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
CANDIDATE_IMAGE = "ghcr.io/honua-io/honua-server@sha256:373aa1fdf1bd4153df9cb21e25e43dfc463c0e194fcac13b40a39c4bb390eb72"
CANDIDATE_SOURCE_REVISION = "ac30266fbd153363bebdbed13130accc8ab0c94a"
EVIDENCE_RECORDED_AT = "2026-09-01T15:14:00Z"
QUICKSTART_PASSED_FENCES = {2, 3, 6, 7, 8, 9, 10, 12}
QUICKSTART_BLOCKERS = {
    4: "https://github.com/honua-io/honua-server/issues/3364",
    5: "https://github.com/honua-io/honua-server/issues/3364",
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
        "status": "not-executable",
        "reason": "Inventory item is supporting source, prose, output, or configuration rather than an independently runnable example.",
        "check": "inventory-classification",
    }
    if path == "samples/gp-local-dev/submit-buffer.sh":
        validation = {
            "status": "passed",
            "reason": "Submitted geometry.buffer, reached successful, fetched GeoJSON, and cleaned up.",
            "runner": "scripts/examples/validate-customer-paths.sh",
            "scenario": "gp-local-dev",
            "check": "HONUA_EXAMPLES_CANDIDATE_IMAGE=<candidate> bash scripts/examples/validate-customer-paths.sh gp-local-dev",
        }
    elif path == "scripts/demos/run-mobile-offline-demo.sh":
        validation = {
            "status": "blocked",
            "reason": "Candidate returned an empty feature collection for seeded layer 68910.",
            "runner": "scripts/examples/validate-customer-paths.sh",
            "scenario": "mobile-offline",
            "check": "HONUA_EXAMPLES_CANDIDATE_IMAGE=<candidate> bash scripts/examples/validate-customer-paths.sh mobile-offline",
            "blockedBy": ["https://github.com/honua-io/honua-server/issues/3836"],
        }
    elif path == "scripts/demos/run-stac-ops-demo.sh":
        validation = {
            "status": "blocked",
            "reason": "Core STAC checks passed, but the release candidate does not ship /samples/stac-ops/.",
            "runner": "scripts/examples/validate-customer-paths.sh",
            "scenario": "stac-ops",
            "check": "HONUA_EXAMPLES_CANDIDATE_IMAGE=<candidate> bash scripts/examples/validate-customer-paths.sh stac-ops",
            "blockedBy": ["https://github.com/honua-io/honua-server/issues/3837"],
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
            item = entry(
                "docs-fence",
                relative,
                str(ordinal),
                fence=ordinal,
                line=line_number,
                language=match.group("language") or "plain",
            )
            if relative == "docs/get-started/quickstart.md":
                if ordinal in QUICKSTART_PASSED_FENCES:
                    item["validation"] = {
                        "status": "passed",
                        "reason": "Executed by the extracted quickstart journey; all postconditions passed.",
                        "runner": "scripts/docs-validation/validate-quickstart.sh",
                        "check": "HONUA_SERVER_IMAGE=<candidate> bash scripts/docs-validation/validate-quickstart.sh",
                    }
                elif ordinal in QUICKSTART_BLOCKERS:
                    item["validation"] = {
                        "status": "blocked",
                        "reason": "Optional Console surface has no compatible shipped image in this candidate lane.",
                        "check": "docs-validation annotation mode=skip",
                        "blockedBy": [QUICKSTART_BLOCKERS[ordinal]],
                    }
            result.append(item)
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
        "evidence": {
            "recordedAt": EVIDENCE_RECORDED_AT,
            "candidateImage": CANDIDATE_IMAGE,
            "candidateSourceRevision": CANDIDATE_SOURCE_REVISION,
        },
        "scope": {
            "docs": "Every fenced block in README.md and docs/**/*.md",
            "samples": "Every file below samples/",
            "docsExampleAssets": "Every file below a docs/**/examples/ directory",
            "quickstartAdjacentScripts": "scripts/demos/*, scripts/dev/*sample*, scripts/ci/*quickstart*",
        },
        "statusDefinitions": {
            "passed": "Observed execution against the exact candidate satisfied all assertions.",
            "blocked": "Execution reached an unshipped surface or product defect and includes explicit blockedBy issue links.",
            "not-executable": "The inventory item is not an independent runnable example; no green execution claim is made.",
        },
        "summary": {"total": len(entries), "byStatus": counts},
        "entries": entries,
    }


def render() -> str:
    return json.dumps(build_manifest(), indent=2, sort_keys=False) + "\n"


def validate(manifest: dict[str, object]) -> None:
    evidence = manifest["evidence"]  # type: ignore[index]
    image = str(evidence["candidateImage"])  # type: ignore[index]
    if "@sha256:" not in image:
        raise ValueError("candidateImage must be digest-pinned")
    for item in manifest["entries"]:  # type: ignore[index]
        validation = item["validation"]
        if validation["status"] == "blocked" and not validation.get("blockedBy"):
            raise ValueError(f"blocked entry lacks blockedBy: {item['id']}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    manifest = build_manifest()
    validate(manifest)
    expected = json.dumps(manifest, indent=2, sort_keys=False) + "\n"
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

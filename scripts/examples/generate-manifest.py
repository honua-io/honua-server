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
QUICKSTART_PASSED_BLOCKS = {
    2: "b7dcc487442e5bb18398ef9fd8fadd9fa9d3a430aec1df890251059be3f5f76a",
    3: "063c2dd6d7247f175aa58232a57c5d2908f8dd56b4628a2427204e0a5dcf9c1b",
    6: "e76431c6a1302517e47d59fa2219d82660ac786fa422f91f3361c2f7ecb829ff",
    7: "7026974350286cab8c62f7c08a3fb501637c1eb4615e41244ebf166c67158662",
    8: "3dcf805e024d72bad7d028140907d1089192d77dcae2f9de3632db8f65041b23",
    9: "86ebc22b9a2dfe24ff69fa9aef97ce7d67f5000c4e696ea78dcb0e897769e4f8",
    10: "dc14542e87a084856cbfbacbc824f812e4d266fbaf1ad4c09e966448a98b1f9f",
    12: "9c1e88dc1b1b34b2ac23ea96e8c821171f53748d52eba3c096ebc5d639d4acfe",
}
QUICKSTART_BLOCKED_BLOCKS = {
    4: (
        "748ed86d6413f4e9fbcba570d3b8be0655f0eed420a952d858b8ddc9e6fc733c",
        "https://github.com/honua-io/honua-server/issues/3364",
    ),
    5: (
        "5bd4b0ec32f4801900e7af88d778f959ab5cf866757152c703ed9a77aa2f5a2e",
        "https://github.com/honua-io/honua-server/issues/3364",
    ),
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
        lines = path.read_text(encoding="utf-8").splitlines()
        block_lines: list[str] = []
        opening_line = 0
        language = "plain"
        for line_number, line in enumerate(lines, 1):
            match = FENCE.match(line)
            if match is None:
                if inside_fence:
                    block_lines.append(line)
                continue
            if inside_fence:
                inside_fence = False
                block_hash = hashlib.sha256(("\n".join(block_lines) + "\n").encode()).hexdigest()
                item = entry(
                    "docs-fence", relative, str(ordinal), fence=ordinal,
                    line=opening_line, language=language,
                )
                is_quickstart = relative == "docs/get-started/quickstart.md"
                if is_quickstart:
                    item["contentSha256"] = block_hash
                if is_quickstart and QUICKSTART_PASSED_BLOCKS.get(ordinal) == block_hash:
                    item["validation"] = {
                        "status": "passed",
                        "reason": "Executed by the extracted quickstart journey; all postconditions passed.",
                        "runner": "scripts/docs-validation/validate-quickstart.sh",
                        "check": "HONUA_SERVER_IMAGE=<candidate> bash scripts/docs-validation/validate-quickstart.sh",
                    }
                elif (
                    is_quickstart
                    and ordinal in QUICKSTART_BLOCKED_BLOCKS
                    and QUICKSTART_BLOCKED_BLOCKS[ordinal][0] == block_hash
                ):
                    item["validation"] = {
                        "status": "blocked",
                        "reason": "Optional Console surface has no compatible shipped image in this candidate lane.",
                        "check": "docs-validation annotation mode=skip",
                        "blockedBy": [QUICKSTART_BLOCKED_BLOCKS[ordinal][1]],
                    }
                elif is_quickstart and (
                    ordinal in QUICKSTART_PASSED_BLOCKS or ordinal in QUICKSTART_BLOCKED_BLOCKS
                ):
                    item["validation"] = {
                        "status": "unvalidated",
                        "reason": "This quickstart block has no execution evidence matching its content hash.",
                        "check": "content-hash-evidence",
                    }
                result.append(item)
                block_lines = []
                continue
            inside_fence = True
            ordinal += 1
            opening_line = line_number
            language = match.group("language") or "plain"
    return result


def file_entries() -> list[dict[str, object]]:
    result: list[dict[str, object]] = []
    for path in shipped_files("samples/**"):
        result.append(entry("sample-asset", path.relative_to(ROOT).as_posix()))
    for path in shipped_files("docs/**/examples/**"):
        result.append(entry("docs-example-asset", path.relative_to(ROOT).as_posix()))
    for path in shipped_files("scripts/demos/*", "scripts/dev/*sample*", "scripts/ci/*quickstart*"):
        item = entry("quickstart-adjacent-script", path.relative_to(ROOT).as_posix())
        if item["validation"]["status"] == "not-executable":  # type: ignore[index]
            item["validation"] = {
                "status": "unvalidated",
                "reason": "Runnable quickstart-adjacent script has not been explicitly validated.",
                "check": "explicit-execution-evidence",
            }
        result.append(item)
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
            "unvalidated": "The inventory item is runnable but has no matching execution evidence.",
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

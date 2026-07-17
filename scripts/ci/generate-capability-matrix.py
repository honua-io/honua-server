#!/usr/bin/env python3
# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""Phase-A capability-matrix aggregation (issue #2893).

Joins the following committed, already-generated/drift-gated sources into
``docs/gis/data/capability-matrix.v1.json``:

  * ``docs/gis/data/feature-catalog.json``      -- entry/proving-test counts and
    maturity per capability key (the ``capability`` field stamped by
    FeatureCatalogGenerator via capability-route-mapping.v1.json).
  * ``docs/cite-status.md``                     -- per-suite OGC CITE pass rates,
    joined onto the capability keys whose protocol the suite covers.
  * ``docs/gis/data/geoservices-rest-parity.json`` -- per-service Esri GeoServices
    REST parity status, joined via capability-keys.v1.json's esriCompatMatrix
    crosswalk.
  * ``docs/gis/data/capability-keys.v1.json``   -- the canonical capability list
    plus all four crosswalk sections (esriAssess, interop, esriCompatMatrix,
    geobench).
  * ``docs/gis/data/capability-no-surface-allowlist.v1.json`` -- capabilities with
    no distinct route.

Dependency-light by design: Python 3 standard library only (json, re,
argparse), matching the existing scripts/ci/*.py and scripts/client-compat/*.py
conventions in this repo. No dotnet build is required to run it.

This is explicitly a Phase-A / temporary home (design amendment on issue #2893):
the aggregation lifts-and-shifts to honua-evidence in Phase B (#2892) once that
repo exists; the versioned schema here is the contract, so the move will be a
CI-job relocation, not a redesign.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]

# Capability keys whose protocol has an official OGC CITE ETS. Maps a
# capability key to the exact "Suite" column value in docs/cite-status.md's
# per-protocol table.
CAPABILITY_TO_CITE_SUITE: dict[str, str] = {
    "serve.wms": "WMS 1.3",
    "serve.wmts": "WMTS 1.0",
    "serve.wcs": "WCS 2.0",
    "serve.ogc-api-features": "OGC API Features 1.0",
    "serve.ogc-api-tiles": "OGC API Tiles 1.0",
}


def load_json(path: Path) -> dict:
    with path.open(encoding="utf-8") as handle:
        return json.load(handle)


def parse_cite_status(path: Path) -> dict[str, dict]:
    """Parses the "Current Per-Protocol Status" markdown table into
    {suite: {profile, passed, total, pass_rate}}."""
    text = path.read_text(encoding="utf-8")
    row_pattern = re.compile(
        r"^\|\s*([^|]+?)\s*\|\s*`?([^|`]+?)`?\s*\|\s*(\d+)\s*/\s*(\d+)\s*\|\s*([\d.]+)%\s*\|",
        re.MULTILINE,
    )
    suites: dict[str, dict] = {}
    for match in row_pattern.finditer(text):
        suite, profile, passed, total, pass_rate = match.groups()
        if suite in {"Suite", "---"}:
            continue
        suites[suite] = {
            "profile": profile.strip(),
            "passed": int(passed),
            "total": int(total),
            "passRate": float(pass_rate),
        }
    return suites


def build_matrix(
    feature_catalog: dict,
    cite_suites: dict[str, dict],
    parity: dict,
    capability_keys: dict,
    no_surface: dict,
) -> dict:
    entries = feature_catalog["entries"]

    per_capability: dict[str, dict] = {}
    for entry in entries:
        capability = entry.get("capability", "")
        if not capability:
            continue
        bucket = per_capability.setdefault(
            capability,
            {"entryCount": 0, "provingTestCount": 0, "maturity": {}},
        )
        bucket["entryCount"] += 1
        bucket["provingTestCount"] += len(entry.get("proving_tests", []))
        maturity = entry.get("maturity", "unknown")
        bucket["maturity"][maturity] = bucket["maturity"].get(maturity, 0) + 1

    parity_by_service = {service["id"]: service for service in parity.get("services", [])}
    crosswalks = capability_keys.get("crosswalks", {})

    esri_compat_by_capability: dict[str, list[str]] = {}
    for row in crosswalks.get("esriCompatMatrix", []):
        esri_compat_by_capability.setdefault(row["capability"], []).append(row["serviceId"])

    esri_assess_by_capability: dict[str, list[str]] = {}
    for row in crosswalks.get("esriAssess", []):
        esri_assess_by_capability.setdefault(row["capability"], []).append(row["assessKey"])

    interop_by_capability: dict[str, list[dict]] = {}
    for row in crosswalks.get("interop", []):
        interop_by_capability.setdefault(row["capability"], []).append(
            {"clientLane": row["clientLane"], "protocol": row["protocol"]}
        )

    geobench_by_capability: dict[str, list[str]] = {}
    for row in crosswalks.get("geobench", []):
        geobench_by_capability.setdefault(row["capability"], []).append(row["scenario"])

    no_surface_by_capability = {
        row["capability"]: row for row in no_surface.get("noSurfaceCapabilities", [])
    }

    capabilities_out = []
    for capability in sorted(capability_keys["capabilities"], key=lambda c: c["key"]):
        key = capability["key"]
        counts = per_capability.get(key, {"entryCount": 0, "provingTestCount": 0, "maturity": {}})

        cite_entries = []
        cite_suite = CAPABILITY_TO_CITE_SUITE.get(key)
        if cite_suite and cite_suite in cite_suites:
            cite_entries.append({"suite": cite_suite, **cite_suites[cite_suite]})

        parity_entries = []
        for service_id in esri_compat_by_capability.get(key, []):
            service = parity_by_service.get(service_id)
            if service:
                parity_entries.append(
                    {
                        "serviceId": service_id,
                        "displayName": service.get("displayName", ""),
                        "parity": service.get("parity", ""),
                    }
                )

        capabilities_out.append(
            {
                "key": key,
                "displayName": capability["displayName"],
                "category": capability["category"],
                "edition": capability["edition"],
                "entryCount": counts["entryCount"],
                "provingTestCount": counts["provingTestCount"],
                "maturity": counts["maturity"],
                "noSurface": no_surface_by_capability.get(key),
                "cite": cite_entries,
                "parity": parity_entries,
                "esriAssess": esri_assess_by_capability.get(key, []),
                "interop": interop_by_capability.get(key, []),
                "geobench": geobench_by_capability.get(key, []),
            }
        )

    return {
        "schemaVersion": "1.0.0",
        "generator": "scripts/ci/generate-capability-matrix.py",
        "trackingIssue": "#2893",
        "sourceArtifacts": [
            "docs/gis/data/feature-catalog.json",
            "docs/cite-status.md",
            "docs/gis/data/geoservices-rest-parity.json",
            "docs/gis/data/capability-keys.v1.json",
            "docs/gis/data/capability-no-surface-allowlist.v1.json",
        ],
        "capabilities": capabilities_out,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--output",
        type=Path,
        default=REPO_ROOT / "docs" / "gis" / "data" / "capability-matrix.v1.json",
        help="Output path for capability-matrix.v1.json",
    )
    parser.add_argument(
        "--check",
        action="store_true",
        help="Exit non-zero if the generated output differs from --output's current contents (drift gate).",
    )
    args = parser.parse_args()

    feature_catalog = load_json(REPO_ROOT / "docs" / "gis" / "data" / "feature-catalog.json")
    cite_suites = parse_cite_status(REPO_ROOT / "docs" / "cite-status.md")
    parity = load_json(REPO_ROOT / "docs" / "gis" / "data" / "geoservices-rest-parity.json")
    capability_keys = load_json(REPO_ROOT / "docs" / "gis" / "data" / "capability-keys.v1.json")
    no_surface = load_json(REPO_ROOT / "docs" / "gis" / "data" / "capability-no-surface-allowlist.v1.json")

    matrix = build_matrix(feature_catalog, cite_suites, parity, capability_keys, no_surface)
    rendered = json.dumps(matrix, indent=2) + "\n"

    if args.check:
        if not args.output.exists():
            print(f"::error::{args.output} does not exist; run without --check to generate it.", file=sys.stderr)
            return 1
        current = args.output.read_text(encoding="utf-8")
        if current != rendered:
            print(
                f"::error::{args.output} is stale. Run "
                "`python3 scripts/ci/generate-capability-matrix.py` and commit the result.",
                file=sys.stderr,
            )
            return 1
        print(f"{args.output} is up to date.")
        return 0

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(rendered, encoding="utf-8")
    print(f"Wrote {args.output} ({len(matrix['capabilities'])} capabilities).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

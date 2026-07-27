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
  * ``tests/baselines/client-compat/**/*.cert.json`` -- committed client-interop
    evidence envelopes, joined onto each capability's ``interop[]`` rows as a
    per-pair ``freshness`` stamp (issue #2897 item 4).

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
import datetime as dt
import importlib.util
import json
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
ENVELOPE_ROOT = REPO_ROOT / "tests" / "baselines" / "client-compat"
FRESHNESS_MAX_AGE_DAYS = 14


def _load_capability_impact():
    """Load scripts/ci/capability-impact.py (dashed filename) for its shared
    evidence helpers (parse_timestamp / is_green / freshness_state)."""
    path = Path(__file__).resolve().parent / "capability-impact.py"
    spec = importlib.util.spec_from_file_location("capability_impact", path)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader
    spec.loader.exec_module(module)
    return module


CAPABILITY_IMPACT = _load_capability_impact()

# Capability keys whose protocol has an official OGC CITE ETS. Maps a
# capability key to the exact "Suite" column value in docs/cite-status.md's
# per-protocol table.
CAPABILITY_TO_CITE_SUITES: dict[str, list[str]] = {
    "serve.wms": ["WMS 1.3"],
    "serve.wmts": ["WMTS 1.0"],
    "serve.wcs": ["WCS 2.0"],
    "serve.wfs": ["WFS 1.0", "WFS 1.1", "WFS 2.0"],
    "serve.ogc-api-features": ["OGC API Features 1.0"],
    "serve.ogc-api-tiles": ["OGC API Tiles 1.0"],
}
# Format-conformance suites (GeoPackage/GML/KML) validate output encodings that
# span several serving capabilities; they are deliberately surfaced in the
# matrix's top-level ``unjoinedCiteSuites`` rather than force-mapped to one key.


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


def load_interop_evidence(envelope_root: Path) -> tuple[dict[tuple[str, str], dict], dt.datetime | None]:
    """Deterministically load committed client-compat envelopes keyed by
    (clientLane, protocol), plus the freshness anchor.

    Deliberately iterates sorted paths (unlike capability-impact.py's
    ``load_envelopes``): the bare regeneration must be byte-stable because the
    capability-matrix drift gate and the merge train both re-run it, so a
    filesystem-order-dependent winner for duplicate (lane, protocol) pairs is
    not acceptable here. Duplicate pairs follow the
    scripts/client-compat/diff-baselines.py precedent (newest observation
    wins) but compare parsed UTC-normalized instants rather than raw strings:
    ISO-8601 offsets break lexicographic ordering ("...T00:30:00+02:00" sorts
    above "...T23:00:00Z" of the previous day while being the earlier
    instant). Equal instants keep the first under sorted path order — both
    deterministic, so ``--check`` stays byte-stable. For the same reason the anchor is the newest
    ``run_date`` across the committed envelopes, not wall-clock time: an
    ``ageDays`` measured from "now" would drift the committed artifact stale
    every day with no source change. A future-dated ``run_date`` must never
    become the anchor (it would self-report age zero/fresh while making every
    legitimate envelope look stale), and silently excluding it against "now"
    would make the committed bytes wall-clock dependent — the byte-stable
    ``--check`` gate could start failing with no source change once time
    passes the bogus timestamp. So generation FAILS instead: a future-dated
    envelope is corrupt input and is rejected with an explicit error before
    any matrix bytes are produced; wall clock is used only inside that hard
    validation, never in emitted output.
    """
    envelopes: dict[tuple[str, str], dict] = {}
    if envelope_root.exists():
        for path in sorted(envelope_root.rglob("*.cert.json")):
            try:
                envelope = load_json(path)
            except (OSError, json.JSONDecodeError):
                continue
            lane = envelope.get("client_lane") or envelope.get("clientLane")
            protocol = envelope.get("protocol")
            if lane and protocol:
                key = (lane, protocol)
                existing = envelopes.get(key)
                # Duplicate (lane, protocol) pairs: keep the most recent
                # observation, matching scripts/client-compat/
                # diff-baselines.py's newest-wins semantics — but compared as
                # parsed UTC instants, not lexicographically (mixed ISO-8601
                # offsets misorder as strings). Strict '>' keeps the first
                # sorted path on equal instants; an unparseable timestamp
                # never displaces a parseable one (deterministic).
                if existing is None:
                    envelopes[key] = envelope
                else:
                    candidate_at, _ = CAPABILITY_IMPACT.parse_timestamp(envelope)
                    existing_at, _ = CAPABILITY_IMPACT.parse_timestamp(existing)
                    if candidate_at is not None and (existing_at is None or candidate_at > existing_at):
                        envelopes[key] = envelope
    now = dt.datetime.now(dt.timezone.utc)
    timestamps = []
    for (lane, protocol), envelope in envelopes.items():
        timestamp, provenance = CAPABILITY_IMPACT.parse_timestamp(envelope)
        if timestamp is None:
            continue
        if timestamp > now:
            raise SystemExit(
                "error: client-compat envelope for "
                f"({lane}, {protocol}) has a future-dated observation "
                f"({provenance} = {timestamp.isoformat()}): corrupt input — fix the envelope's "
                "run_date; refusing to generate a capability matrix from it."
            )
        timestamps.append(timestamp)
    anchor = max(timestamps) if timestamps else None
    return envelopes, anchor


def interop_freshness(envelope: dict | None, anchor: dt.datetime | None) -> dict:
    """Stamp {state, ageDays, runDate} for one interop (clientLane, protocol) row.

    ``unknown``: no committed envelope, no usable observation timestamp, or no
    anchor to measure against. ``stale``: envelope is not green, or its
    evidence observation is more than FRESHNESS_MAX_AGE_DAYS behind the newest
    committed envelope, or (defensively) it claims an observation newer than
    the anchor — negative age fails closed via the shared helper and is
    flagged with an explicit ``reason``. Future-dated envelopes are rejected
    outright by ``load_interop_evidence`` before this stamping runs, so the
    negative-age branch should be unreachable in practice. ``fresh``
    otherwise.
    """
    if envelope is None or anchor is None:
        return {"state": "unknown", "ageDays": None, "runDate": None}
    row = CAPABILITY_IMPACT.freshness_state(envelope, anchor, FRESHNESS_MAX_AGE_DAYS)
    if row["observedAt"] is None:
        return {"state": "unknown", "ageDays": None, "runDate": None}
    result = {
        "state": "stale" if row["stale"] else "fresh",
        "ageDays": row["ageDays"],
        "runDate": row["observedAt"],
    }
    if row["ageDays"] is not None and row["ageDays"] < 0:
        result["reason"] = "run_date is newer than the evidence anchor (future-dated); fails closed as stale"
    return result


def build_matrix(
    feature_catalog: dict,
    cite_suites: dict[str, dict],
    parity: dict,
    capability_keys: dict,
    no_surface: dict,
    interop_evidence: tuple[dict[tuple[str, str], dict], dt.datetime | None] | None = None,
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

    envelopes, freshness_anchor = interop_evidence if interop_evidence is not None else ({}, None)
    interop_by_capability: dict[str, list[dict]] = {}
    for row in crosswalks.get("interop", []):
        envelope = envelopes.get((row["clientLane"], row["protocol"]))
        interop_by_capability.setdefault(row["capability"], []).append(
            {
                "clientLane": row["clientLane"],
                "protocol": row["protocol"],
                "freshness": interop_freshness(envelope, freshness_anchor),
            }
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
        for cite_suite in CAPABILITY_TO_CITE_SUITES.get(key, []):
            if cite_suite in cite_suites:
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

    joined_suites = {suite for suites in CAPABILITY_TO_CITE_SUITES.values() for suite in suites}
    unjoined = sorted(set(cite_suites) - joined_suites)
    return {
        "unjoinedCiteSuites": unjoined,
        "schemaVersion": "1.1.0",
        "generator": "scripts/ci/generate-capability-matrix.py",
        "trackingIssue": "#2893",
        "sourceArtifacts": [
            "docs/gis/data/feature-catalog.json",
            "docs/cite-status.md",
            "docs/gis/data/geoservices-rest-parity.json",
            "docs/gis/data/capability-keys.v1.json",
            "docs/gis/data/capability-no-surface-allowlist.v1.json",
            "tests/baselines/client-compat/",
        ],
        "evidenceFreshness": {
            "anchor": (
                freshness_anchor.isoformat().replace("+00:00", "Z") if freshness_anchor is not None else None
            ),
            "maxAgeDays": FRESHNESS_MAX_AGE_DAYS,
            "basis": (
                "Newest run_date across the committed client-compat envelopes; deterministic "
                "by design so the drift gate stays byte-stable (see #2897)."
            ),
        },
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

    matrix = build_matrix(
        feature_catalog,
        cite_suites,
        parity,
        capability_keys,
        no_surface,
        interop_evidence=load_interop_evidence(ENVELOPE_ROOT),
    )
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

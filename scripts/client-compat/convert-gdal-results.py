#!/usr/bin/env python3
"""Convert tests/python/gdal-ogr-results.json into cross-client cert envelopes.

The GDAL/OGR lane writes a custom per-protocol/category JSON report. The
client-interop baseline-diff only consumes ``.cert.json`` envelopes, so this
converter projects the GDAL report into one envelope per protocol with each
test category mapped to a CERT-* test_case_id.

Mapping rules:
  protocol "oapif" → cert protocol "ogc-features"
  protocol "wfs"   → cert protocol "wfs"

Category → CERT-* mapping is many-to-one because the GDAL test suite emits
fine-grained category labels (e.g. ``feature_read``, ``attribute_query``,
``spatial_query``) rather than the coarse ``read``/``query`` buckets. When
several categories roll up into one CERT-* ID their statuses are aggregated
with ``fail > pass > skip > not-applicable`` so a single failing
sub-category cannot be hidden behind passing siblings, and a single passing
sub-category cannot upgrade an otherwise-failing roll-up.

CLI / SDK lane scope: per
``docs/gis/CROSS_CLIENT_CERTIFICATION_MATRIX.md`` (line 99) the CLI lane
covers "All CERT-* except CERT-RNDR". The GDAL ``export_*`` categories
(``export_geojson`` / ``export_gpkg`` / ``export_csv`` / ``export``)
exercise ogr2ogr serialization paths that the matrix does not certify as
CERT-RNDR-* evidence for this lane, so they are intentionally **unmapped**
here — their pass/fail status is preserved in the raw
``gdal-ogr-results.json`` for human inspection but does not feed any
common-core CERT-* result. CERT-RNDR-01 / CERT-RNDR-02 (and the visual /
style slice IDs) therefore appear in the GDAL envelope as
``not-applicable``.

Categories not exercised in the input are seeded as ``not-applicable``;
known-applicable IDs the suite does not exercise are seeded as ``skip`` so
each envelope keeps a stable common-core shape.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
from datetime import datetime, timezone
from pathlib import Path

PROTOCOL_MAP = {"oapif": "ogc-features", "wfs": "wfs"}

# Maps the category labels emitted by tests/python/gdal_ogr/test_*.py (via
# ``evidence_collector.record(..., category, ...)``) onto the common-core
# CERT-* IDs. Many-to-one is intentional — see module docstring for the
# aggregation rule. The legacy short labels (``read`` / ``query``) are
# kept as aliases so any historical or hand-curated GDAL report still maps
# cleanly. ``schema_introspection`` maps onto CERT-SCHM-01 because the
# matrix defines that as the schema-coverage ID (DISC-01 is for service /
# collection discovery, not field/property schema).
CATEGORY_MAP = {
    # Discovery family (test_*_discovery.py)
    "discovery": "CERT-DISC-01",
    "feature_count": "CERT-DISC-01",
    # Schema family — CERT-SCHM-01 ("retrieve field/property schema") in
    # the matrix; recorded by test_*_discovery.py:test_schema_introspection.
    "schema_introspection": "CERT-SCHM-01",
    # Read family (test_*_read.py)
    "feature_read": "CERT-CONN-01",
    "read": "CERT-CONN-01",
    # Query family (test_*_query.py)
    "attribute_query": "CERT-QFLT-01",
    "spatial_query": "CERT-QFLT-01",
    "query": "CERT-QFLT-01",
    # Export family is intentionally **unmapped** — see _UNMAPPED_CATEGORIES
    # below and the module docstring. The CLI / SDK lane row in the matrix
    # excludes CERT-RNDR for this lane, so a passing ogr2ogr export must not
    # certify CERT-RNDR-01 in the cert envelope.
}

# Categories the converter recognises but deliberately does not feed into
# the common-core results. They appear in the raw ``gdal-ogr-results.json``
# (preserved for human inspection) but not in the cert envelope, so the
# baseline cannot certify a CERT-* ID outside the matrix contract. Listing
# them here suppresses the unknown-category ::warning:: that would
# otherwise fire on every GDAL run.
_UNMAPPED_CATEGORIES: frozenset[str] = frozenset({
    "export_geojson",
    "export_gpkg",
    "export_csv",
    "export",  # legacy alias
})

# Higher number wins when several categories roll up to one CERT-* ID.
# Mirrors the aggregation rule the conftest already applies per-category
# inside a single test module.
_STATUS_PRIORITY = {"fail": 3, "pass": 2, "skip": 1, "not-applicable": 0}


def _worst(current: str | None, candidate: str) -> str:
    if current is None:
        return candidate
    if _STATUS_PRIORITY.get(candidate, -1) > _STATUS_PRIORITY.get(current, -1):
        return candidate
    return current

# 24-ID common-core matrix (18 base + 6 visual / style slice IDs) — kept in
# sync with tests/js-browser/cesium/support/cert-reporter.ts.
COMMON_CORE_IDS = (
    "CERT-CONN-01", "CERT-CONN-02",
    "CERT-AUTH-01", "CERT-AUTH-02",
    "CERT-DISC-01", "CERT-DISC-02",
    "CERT-SCHM-01", "CERT-SCHM-02",
    "CERT-QFLT-01", "CERT-QFLT-02",
    "CERT-PAGE-01", "CERT-PAGE-02",
    "CERT-GEOM-01", "CERT-GEOM-02",
    "CERT-ERRH-01", "CERT-ERRH-02",
    "CERT-RNDR-01", "CERT-RNDR-02",
    "CERT-RNDR-SYM-01", "CERT-RNDR-LIN-01", "CERT-RNDR-FIL-01",
    "CERT-RNDR-LBL-01", "CERT-RNDR-SPR-01", "CERT-RNDR-URL-01",
)

# Categories the GDAL suite does substantiate (keep as 'skip' if absent
# from a given run rather than 'not-applicable').
APPLICABLE_TO_GDAL = {CATEGORY_MAP[c] for c in CATEGORY_MAP}


def _result(case_id: str, status: str, notes: str = "") -> dict:
    return {
        "test_case_id": case_id,
        "status": status,
        "duration_ms": None,
        "measured_count": None,
        "measured_delta": None,
        "notes": notes,
        "evidence_ref": "",
    }


def build_envelope(
    cert_protocol: str,
    categories: dict[str, str],
    gdal_version: str,
    run_id: str,
    run_date: str,
    server_version: str,
    environment: str,
) -> dict:
    # Roll up every recognised category in `categories` onto its CERT-* ID,
    # aggregating with worst-status-wins. Categories the converter does not
    # know about are emitted as a stderr warning so newly-added tests do not
    # silently disappear from the envelope.
    cid_status: dict[str, str] = {}
    for category, status in categories.items():
        cid = CATEGORY_MAP.get(category)
        if cid is None:
            if category in _UNMAPPED_CATEGORIES:
                # Intentionally outside the cert envelope — see module
                # docstring + _UNMAPPED_CATEGORIES. Evidence is preserved in
                # the raw gdal-ogr-results.json.
                continue
            print(
                f"::warning::Unknown GDAL category '{category}' (status={status}); "
                "add it to CATEGORY_MAP in convert-gdal-results.py.",
                file=sys.stderr,
            )
            continue
        cid_status[cid] = _worst(cid_status.get(cid), status)

    results: list[dict] = []
    for cid in COMMON_CORE_IDS:
        if cid in APPLICABLE_TO_GDAL:
            status = cid_status.get(cid)
            if status is None:
                results.append(_result(cid, "skip", "Not exercised by GDAL/OGR suite in this run."))
            else:
                results.append(_result(cid, status))
        else:
            results.append(_result(cid, "not-applicable"))

    summary = {
        "total": len(results),
        "passed": sum(1 for r in results if r["status"] == "pass"),
        "failed": sum(1 for r in results if r["status"] == "fail"),
        "skipped": sum(1 for r in results if r["status"] == "skip"),
        "not_applicable": sum(1 for r in results if r["status"] == "not-applicable"),
    }
    return {
        "schema_version": "1.0",
        "run_id": run_id,
        "run_date": run_date,
        "server_version": server_version,
        "client_lane": "cli",
        "client_version": gdal_version or "unknown",
        "protocol": cert_protocol,
        "environment": environment,
        "results": results,
        "summary": summary,
        "cite_results": None,
        "extensions": [],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--input", required=True, help="Path to gdal-ogr-results.json")
    parser.add_argument("--output-dir", required=True, help="Directory for emitted .cert.json envelopes")
    parser.add_argument("--run-id", default=None)
    args = parser.parse_args()

    input_path = Path(args.input)
    output_dir = Path(args.output_dir)
    if not input_path.exists():
        print(f"::warning::{input_path} not found; skipping conversion.", file=sys.stderr)
        return 0

    try:
        report = json.loads(input_path.read_text())
    except (OSError, json.JSONDecodeError) as exc:
        print(f"::error::Could not read {input_path}: {exc}", file=sys.stderr)
        return 1

    output_dir.mkdir(parents=True, exist_ok=True)
    run_id = args.run_id or os.environ.get("CERT_RUN_ID") or os.environ.get("GITHUB_RUN_ID") or datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    run_date = report.get("timestamp") or datetime.now(timezone.utc).isoformat()
    server_version = os.environ.get("GITHUB_SHA", "local")
    environment = "ci" if os.environ.get("CI") else "local"
    gdal_version = report.get("gdal_version") or ""

    written = 0
    for protocol_key, categories in (report.get("protocols") or {}).items():
        cert_protocol = PROTOCOL_MAP.get(protocol_key)
        if cert_protocol is None:
            print(f"::warning::Unknown GDAL protocol '{protocol_key}'; skipping.", file=sys.stderr)
            continue
        envelope = build_envelope(
            cert_protocol=cert_protocol,
            categories=categories,
            gdal_version=gdal_version,
            run_id=run_id,
            run_date=run_date,
            server_version=server_version,
            environment=environment,
        )
        out_path = output_dir / f"{run_id}-cli-gdal-{cert_protocol}.cert.json"
        out_path.write_text(json.dumps(envelope, indent=2) + "\n")
        print(f"Wrote {out_path}")
        written += 1

    if written == 0:
        print("::warning::No protocols converted from GDAL report.", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())

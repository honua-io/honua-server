#!/usr/bin/env python3
"""Convert tests/python/gdal-ogr-results.json into cross-client cert envelopes.

The GDAL/OGR lane writes a custom per-protocol/category JSON report. The
client-interop baseline-diff only consumes ``.cert.json`` envelopes, so this
converter projects the GDAL report into one envelope per protocol with each
test category mapped to a CERT-* test_case_id.

Mapping rules:
  protocol "oapif" → cert protocol "ogc-features"
  protocol "wfs"   → cert protocol "wfs"
  category "discovery" → CERT-DISC-01
  category "read"      → CERT-CONN-01
  category "query"     → CERT-QFLT-01
  category "export"    → CERT-RNDR-01

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
CATEGORY_MAP = {
    "discovery": "CERT-DISC-01",
    "read": "CERT-CONN-01",
    "query": "CERT-QFLT-01",
    "export": "CERT-RNDR-01",
}

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
    results: list[dict] = []
    for cid in COMMON_CORE_IDS:
        if cid in APPLICABLE_TO_GDAL:
            # Find matching category (reverse-lookup) and use its status.
            category = next(c for c, mapped in CATEGORY_MAP.items() if mapped == cid)
            status = categories.get(category)
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

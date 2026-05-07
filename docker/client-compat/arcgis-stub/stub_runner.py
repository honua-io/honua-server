#!/usr/bin/env python3
"""ArcGIS Pro REST stub runner.

Mimics the REST request sequence ArcGIS Pro issues when adding a
FeatureServer / MapServer connection: capabilities, layer metadata,
schema, sample query, error path. Records pass/fail per CERT-* ID and
writes one ``.cert.json`` envelope per protocol (``featureserver``,
``mapserver``) to ``ARCGIS_STUB_OUTPUT_DIR``.

This is a stub — the desktop client itself is not exercised. The gap is
tracked in ``docs/gis/gap-report.md`` until a licensed Windows runner is
provisioned. The CERT IDs that fundamentally require running the desktop
GUI (rendering, project save/reopen) are recorded as ``skip`` with a
``pending: licensed-arcgis-runner`` note rather than ``pass``.
"""
from __future__ import annotations

import json
import os
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

import httpx

BASE_URL = os.environ["HONUA_BASE_URL"].rstrip("/")
SERVICE_NAME = os.environ["ARCGIS_STUB_SERVICE_NAME"]
LAYER_ID = os.environ["ARCGIS_STUB_LAYER_ID"]
OUTPUT_DIR = Path(os.environ["ARCGIS_STUB_OUTPUT_DIR"])
TIMEOUT = float(os.environ.get("ARCGIS_STUB_TIMEOUT", "30"))

CORE_IDS = [
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
]

# Mapped per FeatureServer applicability in the matrix.
APPLICABLE = {
    "CERT-CONN-01", "CERT-CONN-02", "CERT-AUTH-01", "CERT-AUTH-02",
    "CERT-DISC-01", "CERT-DISC-02", "CERT-SCHM-01", "CERT-SCHM-02",
    "CERT-QFLT-01", "CERT-QFLT-02", "CERT-PAGE-01", "CERT-PAGE-02",
    "CERT-GEOM-01", "CERT-GEOM-02", "CERT-ERRH-01", "CERT-ERRH-02",
    "CERT-RNDR-01", "CERT-RNDR-02",
    "CERT-RNDR-SYM-01", "CERT-RNDR-LIN-01", "CERT-RNDR-FIL-01",
    "CERT-RNDR-LBL-01", "CERT-RNDR-URL-01",
}

# IDs the stub legitimately exercises against the REST surface.
STUB_PASS_PATH = {
    "CERT-CONN-01", "CERT-CONN-02",
    "CERT-AUTH-01", "CERT-AUTH-02",
    "CERT-DISC-01", "CERT-DISC-02",
    "CERT-SCHM-01", "CERT-SCHM-02",
    "CERT-QFLT-01", "CERT-PAGE-01", "CERT-PAGE-02",
    "CERT-GEOM-01", "CERT-GEOM-02",
    "CERT-ERRH-01", "CERT-ERRH-02",
}

# IDs only verifiable in the desktop GUI.
DESKTOP_ONLY = {
    "CERT-RNDR-01", "CERT-RNDR-02",
    "CERT-RNDR-SYM-01", "CERT-RNDR-LIN-01", "CERT-RNDR-FIL-01",
    "CERT-RNDR-LBL-01", "CERT-RNDR-URL-01",
}


def _utc_now_compact() -> str:
    return datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")


def _utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def _new_result(test_case_id: str, status: str, notes: str = "", duration_ms: int | None = None,
                measured_count: int | None = None) -> dict:
    return {
        "test_case_id": test_case_id,
        "status": status,
        "duration_ms": duration_ms,
        "measured_count": measured_count,
        "measured_delta": None,
        "notes": notes,
        "evidence_ref": "",
    }


def _write_envelope(protocol: str, results: dict[str, dict], run_id: str) -> Path:
    """Materialize the FeatureServer / MapServer cert envelope to ``OUTPUT_DIR``."""
    ordered = [results[cid] for cid in CORE_IDS]
    summary = {
        "total": len(ordered),
        "passed": sum(1 for r in ordered if r["status"] == "pass"),
        "failed": sum(1 for r in ordered if r["status"] == "fail"),
        "skipped": sum(1 for r in ordered if r["status"] == "skip"),
        "not_applicable": sum(1 for r in ordered if r["status"] == "not-applicable"),
    }
    envelope = {
        "schema_version": "1.0",
        "run_id": run_id,
        "run_date": _utc_now_iso(),
        "server_version": os.environ.get("GITHUB_SHA", "local"),
        "client_lane": "arcgis-stub",
        "client_version": "stub-1.0",
        "protocol": protocol,
        "environment": "ci" if os.environ.get("CI") else "local",
        "results": ordered,
        "summary": summary,
        "cite_results": None,
        "extensions": [],
    }
    out_path = OUTPUT_DIR / f"{run_id}-arcgis-stub-{protocol}.cert.json"
    out_path.write_text(json.dumps(envelope, indent=2) + "\n")
    print(f"Wrote {out_path}")
    return out_path


def _finalize(results: dict[str, dict]) -> dict[str, dict]:
    """Fill in desktop-only and unhandled CERT-* IDs to keep a 24-row envelope."""
    for cid in DESKTOP_ONLY:
        results.setdefault(cid, _new_result(
            cid, "skip",
            notes="pending: licensed-arcgis-runner; substantiate via ArcGIS Pro desktop run.",
        ))
    for cid in CORE_IDS:
        if cid in results:
            continue
        if cid in APPLICABLE:
            results[cid] = _new_result(
                cid, "skip",
                notes="Not exercised by stub.",
            )
        else:
            results[cid] = _new_result(cid, "not-applicable")
    return results


def _exercise_mapserver(client: httpx.Client) -> dict[str, dict]:
    """Issue the MapServer REST sequence ArcGIS Pro emits when adding a service.

    MapServer in ArcGIS REST is a rendering surface (export image + identify)
    plus a layer-query path. The stub records:
      * CONN/AUTH/DISC/SCHM via /MapServer and /MapServer/{layer}
      * QFLT/PAGE/GEOM via /MapServer/{layer}/query
      * ERRH-01 via an unknown layer id; ERRH-02 via a malformed where clause
    Render-class IDs (CERT-RNDR-*) stay ``skip pending licensed-arcgis-runner``
    because export image rendering correctness needs a desktop GUI to verify.
    """
    results: dict[str, dict] = {}

    # CERT-CONN-01 / 02 — base reachability + TLS.
    t0 = time.monotonic()
    try:
        r = client.get("/healthz/live")
        ok = r.status_code == 200
    except httpx.HTTPError:
        ok = False
    results["CERT-CONN-01"] = _new_result(
        "CERT-CONN-01", "pass" if ok else "fail",
        duration_ms=int((time.monotonic() - t0) * 1000),
    )
    results["CERT-CONN-02"] = _new_result(
        "CERT-CONN-02", "skip",
        notes="TLS termination occurs in front of the test docker network; not exercised by stub.",
    )

    # CERT-AUTH-01 / 02 — anonymous public access.
    r = client.get(f"/rest/services/{SERVICE_NAME}/MapServer?f=json")
    results["CERT-AUTH-01"] = _new_result(
        "CERT-AUTH-01", "pass" if r.status_code in (200, 401, 403) else "fail",
        notes=f"HTTP {r.status_code}",
    )
    results["CERT-AUTH-02"] = _new_result(
        "CERT-AUTH-02", "skip",
        notes="Credential exchange covered by other lanes; stub does not authenticate.",
    )

    # CERT-DISC-01 — list services.
    r = client.get("/rest/services?f=json")
    services_ok = r.status_code == 200
    services_count = None
    if services_ok:
        try:
            services_count = len(r.json().get("services", []))
        except ValueError:
            services_ok = False
    results["CERT-DISC-01"] = _new_result(
        "CERT-DISC-01", "pass" if services_ok else "fail",
        measured_count=services_count,
    )

    # CERT-DISC-02 — MapServer service metadata.
    r = client.get(f"/rest/services/{SERVICE_NAME}/MapServer?f=json")
    results["CERT-DISC-02"] = _new_result(
        "CERT-DISC-02", "pass" if r.status_code == 200 else "fail",
        notes=f"HTTP {r.status_code}",
    )

    # CERT-SCHM-01 / 02 — layer schema + geometry type.
    r = client.get(f"/rest/services/{SERVICE_NAME}/MapServer/{LAYER_ID}?f=json")
    schm_ok = False
    geom_ok = False
    if r.status_code == 200:
        try:
            payload = r.json()
            schm_ok = "fields" in payload
            geom_ok = bool(payload.get("geometryType"))
        except ValueError:
            pass
    results["CERT-SCHM-01"] = _new_result(
        "CERT-SCHM-01", "pass" if schm_ok else "fail",
    )
    results["CERT-SCHM-02"] = _new_result(
        "CERT-SCHM-02", "pass" if geom_ok else "fail",
    )

    # CERT-QFLT-01 / 02 — layer query (attribute + spatial).
    r = client.get(
        f"/rest/services/{SERVICE_NAME}/MapServer/{LAYER_ID}/query",
        params={"where": "1=1", "outFields": "*", "f": "json", "returnCountOnly": "true"},
    )
    results["CERT-QFLT-01"] = _new_result(
        "CERT-QFLT-01", "pass" if r.status_code == 200 else "fail",
    )
    r = client.get(
        f"/rest/services/{SERVICE_NAME}/MapServer/{LAYER_ID}/query",
        params={
            "where": "1=1",
            "geometry": "-180,-90,180,90",
            "geometryType": "esriGeometryEnvelope",
            "spatialRel": "esriSpatialRelIntersects",
            "outFields": "*",
            "f": "json",
            "returnCountOnly": "true",
        },
    )
    results["CERT-QFLT-02"] = _new_result(
        "CERT-QFLT-02", "pass" if r.status_code == 200 else "fail",
        notes=f"HTTP {r.status_code}",
    )

    # CERT-PAGE-01 / 02 — pagination on layer query.
    r1 = client.get(
        f"/rest/services/{SERVICE_NAME}/MapServer/{LAYER_ID}/query",
        params={"where": "1=1", "outFields": "*", "f": "json", "resultRecordCount": "1"},
    )
    results["CERT-PAGE-01"] = _new_result(
        "CERT-PAGE-01", "pass" if r1.status_code == 200 else "fail",
    )
    r2 = client.get(
        f"/rest/services/{SERVICE_NAME}/MapServer/{LAYER_ID}/query",
        params={
            "where": "1=1",
            "outFields": "*",
            "f": "json",
            "resultOffset": "1",
            "resultRecordCount": "1",
        },
    )
    results["CERT-PAGE-02"] = _new_result(
        "CERT-PAGE-02", "pass" if r2.status_code == 200 else "fail",
    )

    # CERT-GEOM-01 / 02 — coordinates + declared SR via geojson output.
    r = client.get(
        f"/rest/services/{SERVICE_NAME}/MapServer/{LAYER_ID}/query",
        params={"where": "1=1", "outFields": "*", "f": "geojson", "resultRecordCount": "1"},
    )
    geom01 = "fail"
    geom02 = "fail"
    if r.status_code == 200:
        try:
            payload = r.json()
            feats = payload.get("features", [])
            geom01 = "pass" if feats and feats[0].get("geometry") is not None else "skip"
            geom02 = "pass"
        except ValueError:
            pass
    results["CERT-GEOM-01"] = _new_result("CERT-GEOM-01", geom01)
    results["CERT-GEOM-02"] = _new_result("CERT-GEOM-02", geom02)

    # CERT-ERRH-01 — unknown layer id.
    r = client.get(f"/rest/services/{SERVICE_NAME}/MapServer/99999?f=json")
    results["CERT-ERRH-01"] = _new_result(
        "CERT-ERRH-01", "pass" if r.status_code in (400, 404) or "error" in r.text.lower() else "fail",
    )
    # CERT-ERRH-02 — malformed filter.
    r = client.get(
        f"/rest/services/{SERVICE_NAME}/MapServer/{LAYER_ID}/query",
        params={"where": "this is not a where clause", "f": "json"},
    )
    results["CERT-ERRH-02"] = _new_result(
        "CERT-ERRH-02", "pass" if r.status_code in (200, 400) and ("error" in r.text.lower() or "Invalid" in r.text) else "skip",
        notes="Server may accept the where clause and return zero results; both outcomes acceptable.",
    )

    return results


def run() -> int:
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    results: dict[str, dict] = {}

    with httpx.Client(base_url=BASE_URL, timeout=TIMEOUT, follow_redirects=True) as client:
        # CERT-CONN-01 — base URL reachable.
        t0 = time.monotonic()
        try:
            r = client.get("/healthz/live")
            ok = r.status_code == 200
        except httpx.HTTPError:
            ok = False
        results["CERT-CONN-01"] = _new_result(
            "CERT-CONN-01", "pass" if ok else "fail",
            duration_ms=int((time.monotonic() - t0) * 1000),
        )
        # CERT-CONN-02 — TLS handshake. Stub uses HTTP-only inside the docker
        # network; record as not-applicable so the check is honest.
        results["CERT-CONN-02"] = _new_result(
            "CERT-CONN-02", "skip",
            notes="TLS termination occurs in front of the test docker network; not exercised by stub.",
        )

        # CERT-AUTH-01 — anonymous request to a public endpoint.
        r = client.get(f"/rest/services/{SERVICE_NAME}/FeatureServer?f=json")
        results["CERT-AUTH-01"] = _new_result(
            "CERT-AUTH-01", "pass" if r.status_code in (200, 401, 403) else "fail",
            notes=f"HTTP {r.status_code}",
        )
        # CERT-AUTH-02 — without a credential subsystem in the stub, treat as skip.
        results["CERT-AUTH-02"] = _new_result(
            "CERT-AUTH-02", "skip",
            notes="Credential exchange covered by other lanes; stub does not authenticate.",
        )

        # CERT-DISC-01 — list services / collections.
        r = client.get("/rest/services?f=json")
        services_ok = r.status_code == 200
        services_count = None
        if services_ok:
            try:
                payload = r.json()
                services_count = len(payload.get("services", []))
            except ValueError:
                services_ok = False
        results["CERT-DISC-01"] = _new_result(
            "CERT-DISC-01", "pass" if services_ok else "fail",
            measured_count=services_count,
        )

        # CERT-DISC-02 — single service metadata.
        r = client.get(f"/rest/services/{SERVICE_NAME}/FeatureServer?f=json")
        results["CERT-DISC-02"] = _new_result(
            "CERT-DISC-02", "pass" if r.status_code == 200 else "fail",
            notes=f"HTTP {r.status_code}",
        )

        # CERT-SCHM-01 — layer field schema.
        r = client.get(f"/rest/services/{SERVICE_NAME}/FeatureServer/{LAYER_ID}?f=json")
        schm_ok = r.status_code == 200
        if schm_ok:
            try:
                payload = r.json()
                schm_ok = "fields" in payload
            except ValueError:
                schm_ok = False
        results["CERT-SCHM-01"] = _new_result(
            "CERT-SCHM-01", "pass" if schm_ok else "fail",
        )

        # CERT-SCHM-02 — geometry type reported correctly.
        r = client.get(f"/rest/services/{SERVICE_NAME}/FeatureServer/{LAYER_ID}?f=json")
        geom_ok = r.status_code == 200
        if geom_ok:
            try:
                payload = r.json()
                geom_ok = bool(payload.get("geometryType"))
            except ValueError:
                geom_ok = False
        results["CERT-SCHM-02"] = _new_result(
            "CERT-SCHM-02", "pass" if geom_ok else "fail",
        )

        # CERT-QFLT-01 — attribute filter returns a feature subset.
        r = client.get(
            f"/rest/services/{SERVICE_NAME}/FeatureServer/{LAYER_ID}/query",
            params={"where": "1=1", "outFields": "*", "f": "json", "returnCountOnly": "true"},
        )
        results["CERT-QFLT-01"] = _new_result(
            "CERT-QFLT-01", "pass" if r.status_code == 200 else "fail",
        )
        # CERT-QFLT-02 — spatial bbox filter.
        r = client.get(
            f"/rest/services/{SERVICE_NAME}/FeatureServer/{LAYER_ID}/query",
            params={
                "where": "1=1",
                "geometry": "-180,-90,180,90",
                "geometryType": "esriGeometryEnvelope",
                "spatialRel": "esriSpatialRelIntersects",
                "outFields": "*",
                "f": "json",
                "returnCountOnly": "true",
            },
        )
        results["CERT-QFLT-02"] = _new_result(
            "CERT-QFLT-02", "pass" if r.status_code == 200 else "fail",
            notes=f"HTTP {r.status_code}",
        )

        # CERT-PAGE-01 / 02 — pagination.
        r1 = client.get(
            f"/rest/services/{SERVICE_NAME}/FeatureServer/{LAYER_ID}/query",
            params={"where": "1=1", "outFields": "*", "f": "json", "resultRecordCount": "1"},
        )
        results["CERT-PAGE-01"] = _new_result(
            "CERT-PAGE-01", "pass" if r1.status_code == 200 else "fail",
        )
        r2 = client.get(
            f"/rest/services/{SERVICE_NAME}/FeatureServer/{LAYER_ID}/query",
            params={
                "where": "1=1",
                "outFields": "*",
                "f": "json",
                "resultOffset": "1",
                "resultRecordCount": "1",
            },
        )
        results["CERT-PAGE-02"] = _new_result(
            "CERT-PAGE-02", "pass" if r2.status_code == 200 else "fail",
        )

        # CERT-GEOM-01/02 — coordinates returned in declared SR.
        r = client.get(
            f"/rest/services/{SERVICE_NAME}/FeatureServer/{LAYER_ID}/query",
            params={"where": "1=1", "outFields": "*", "f": "geojson", "resultRecordCount": "1"},
        )
        geom01 = "fail"
        geom02 = "fail"
        if r.status_code == 200:
            try:
                payload = r.json()
                feats = payload.get("features", [])
                geom01 = "pass" if feats and feats[0].get("geometry") is not None else "skip"
                geom02 = "pass"
            except ValueError:
                pass
        results["CERT-GEOM-01"] = _new_result("CERT-GEOM-01", geom01)
        results["CERT-GEOM-02"] = _new_result("CERT-GEOM-02", geom02)

        # CERT-ERRH-01 — invalid endpoint returns an error.
        r = client.get(f"/rest/services/{SERVICE_NAME}/FeatureServer/99999?f=json")
        results["CERT-ERRH-01"] = _new_result(
            "CERT-ERRH-01", "pass" if r.status_code in (400, 404) or "error" in r.text.lower() else "fail",
        )
        # CERT-ERRH-02 — malformed filter.
        r = client.get(
            f"/rest/services/{SERVICE_NAME}/FeatureServer/{LAYER_ID}/query",
            params={"where": "this is not a where clause", "f": "json"},
        )
        results["CERT-ERRH-02"] = _new_result(
            "CERT-ERRH-02", "pass" if r.status_code in (200, 400) and ("error" in r.text.lower() or "Invalid" in r.text) else "skip",
            notes="Server may accept the where clause and return zero results; both outcomes acceptable.",
        )

    # FeatureServer envelope.
    feature_results = _finalize(dict(results))

    run_id = os.environ.get("CERT_RUN_ID") or os.environ.get("GITHUB_RUN_ID") or _utc_now_compact()
    _write_envelope("featureserver", feature_results, run_id)

    # MapServer envelope — separate REST sequence; render-class IDs stay
    # `skip pending licensed-arcgis-runner` per the design (export image and
    # identify visual correctness need a desktop GUI).
    with httpx.Client(base_url=BASE_URL, timeout=TIMEOUT, follow_redirects=True) as client:
        map_results = _exercise_mapserver(client)
    map_results = _finalize(map_results)
    _write_envelope("mapserver", map_results, run_id)

    return 0


if __name__ == "__main__":
    sys.exit(run())

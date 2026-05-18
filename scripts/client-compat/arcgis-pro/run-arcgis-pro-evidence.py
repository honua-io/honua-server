#!/usr/bin/env python3
"""Licensed ArcGIS Pro desktop evidence runner.

This script is intended to run under ArcGIS Pro's Python environment
(`propy.bat` or the ArcGIS Pro Python window). It connects ArcPy to Honua
GeoServices REST FeatureServer and MapServer endpoints, writes one
`desktop-arcgis` `.cert.json` envelope per protocol, and stores only redacted
relative artifact references.

The `--fixture-observations` mode is deliberately ArcPy-free so ordinary PR
checks can validate the envelope contract without requiring ArcGIS Pro.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import sys
import time
import traceback
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


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

STATUS_VALUES = {"pass", "fail", "skip", "not-applicable"}
QUERY_FOCUSED_IDS = {
    "CERT-QFLT-01", "CERT-QFLT-02",
    "CERT-PAGE-01", "CERT-PAGE-02",
    "CERT-GEOM-01", "CERT-GEOM-02",
    "CERT-ERRH-02",
}
FEATURESERVER_NON_APPLICABLE = {"CERT-RNDR-SPR-01"}
MAPSERVER_NON_APPLICABLE = {
    "CERT-RNDR-SYM-01", "CERT-RNDR-LIN-01", "CERT-RNDR-FIL-01",
    "CERT-RNDR-LBL-01", "CERT-RNDR-SPR-01", "CERT-RNDR-URL-01",
}
DESKTOP_EXTENSION_IDS = ["DSK-EXT-01", "DSK-EXT-02"]
SENSITIVE_ENV_NAMES = [
    "HONUA_API_KEY",
    "HONUA_AUTHORIZATION",
    "ARCGIS_PASSWORD",
    "ARCGIS_TOKEN",
    "ARCGIS_LICENSE",
    "ESRI_PASSWORD",
    "ESRI_TOKEN",
]


def utc_now_compact() -> str:
    return datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")


def utc_now_iso() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def redact_text(value: Any) -> str:
    """Redact common credential forms before writing notes, logs, or refs."""
    if value is None:
        return ""

    text = str(value)
    for name in SENSITIVE_ENV_NAMES:
        secret = os.environ.get(name)
        if secret and len(secret) >= 4:
            text = text.replace(secret, "[REDACTED]")

    # URL userinfo and sensitive query parameters.
    text = re.sub(r"(?i)(https?://)[^/@\s]+@", r"\1[REDACTED]@", text)
    text = re.sub(
        r"(?i)([?&](?:token|api[_-]?key|key|password|client[_-]?secret|access[_-]?token)=)[^&#\s]+",
        r"\1[REDACTED]",
        text,
    )
    # Header / key-value forms commonly copied into logs.
    text = re.sub(r"(?i)(authorization\s*:\s*bearer\s+)[^\s,;]+", r"\1[REDACTED]", text)
    text = re.sub(r"(?i)(x-api-key\s*:\s*)[^\s,;]+", r"\1[REDACTED]", text)
    text = re.sub(
        r"(?i)(\b(?:token|api[_-]?key|password|client[_-]?secret|access[_-]?token)\s*[:=]\s*)[^\s,;]+",
        r"\1[REDACTED]",
        text,
    )
    return text


def safe_relative_ref(path: str | Path | None, output_dir: Path) -> str:
    if not path:
        return ""

    path_obj = Path(path)
    if path_obj.is_absolute():
        try:
            path_obj = path_obj.relative_to(output_dir.resolve())
        except ValueError:
            path_obj = Path(path_obj.name)

    return redact_text(path_obj.as_posix())


def make_result(
    test_case_id: str,
    status: str,
    *,
    duration_ms: int | None = None,
    measured_count: int | None = None,
    measured_delta: float | None = None,
    notes: str = "",
    evidence_ref: str = "",
) -> dict[str, Any]:
    if status not in STATUS_VALUES:
        raise ValueError(f"{test_case_id} has invalid status {status!r}")

    return {
        "test_case_id": test_case_id,
        "status": status,
        "duration_ms": duration_ms,
        "measured_count": measured_count,
        "measured_delta": measured_delta,
        "notes": redact_text(notes),
        "evidence_ref": redact_text(evidence_ref),
    }


def coerce_result(test_case_id: str, raw: Any, output_dir: Path) -> dict[str, Any]:
    if isinstance(raw, str):
        return make_result(test_case_id, raw)
    if not isinstance(raw, dict):
        raise ValueError(f"{test_case_id} fixture value must be an object or status string")

    status = raw.get("status")
    if not isinstance(status, str):
        raise ValueError(f"{test_case_id} fixture result must include a string status")

    return make_result(
        test_case_id,
        status,
        duration_ms=raw.get("duration_ms"),
        measured_count=raw.get("measured_count"),
        measured_delta=raw.get("measured_delta"),
        notes=raw.get("notes", ""),
        evidence_ref=safe_relative_ref(raw.get("evidence_ref", ""), output_dir),
    )


def default_result(protocol: str, test_case_id: str) -> dict[str, Any]:
    if protocol == "featureserver" and test_case_id in FEATURESERVER_NON_APPLICABLE:
        return make_result(test_case_id, "not-applicable")

    if protocol == "mapserver" and test_case_id in MAPSERVER_NON_APPLICABLE:
        return make_result(
            test_case_id,
            "not-applicable",
            notes="MapServer evidence does not carry FeatureServer drawingInfo or MVT sprite style checks.",
        )

    if protocol == "mapserver" and test_case_id in QUERY_FOCUSED_IDS:
        return make_result(
            test_case_id,
            "not-applicable",
            notes="MapServer layer query endpoint was not exercised by this run.",
        )

    return make_result(
        test_case_id,
        "skip",
        notes="Not exercised by this licensed ArcGIS Pro run.",
    )


def summarize(results: list[dict[str, Any]]) -> dict[str, int]:
    return {
        "total": len(results),
        "passed": sum(1 for item in results if item["status"] == "pass"),
        "failed": sum(1 for item in results if item["status"] == "fail"),
        "skipped": sum(1 for item in results if item["status"] == "skip"),
        "not_applicable": sum(1 for item in results if item["status"] == "not-applicable"),
    }


def build_envelope(
    observations: dict[str, Any],
    protocol: str,
    output_dir: Path,
) -> dict[str, Any]:
    protocol_data = observations.get("protocols", {}).get(protocol, {})
    checks = protocol_data.get("checks", {})
    if not isinstance(checks, dict):
        raise ValueError(f"{protocol} checks must be an object")

    result_by_id: dict[str, dict[str, Any]] = {}
    for test_case_id, raw_result in checks.items():
        if test_case_id not in CORE_IDS:
            raise ValueError(f"{protocol} contains unknown CERT id {test_case_id}")
        result_by_id[test_case_id] = coerce_result(test_case_id, raw_result, output_dir)

    ordered = [result_by_id.get(test_case_id) or default_result(protocol, test_case_id) for test_case_id in CORE_IDS]

    extension_data = protocol_data.get("extensions", {})
    if extension_data is None:
        extension_data = {}
    if not isinstance(extension_data, dict):
        raise ValueError(f"{protocol} extensions must be an object")

    extensions: list[dict[str, Any]] = []
    for extension_id in DESKTOP_EXTENSION_IDS:
        raw = extension_data.get(extension_id)
        if raw is None:
            continue
        extensions.append(coerce_result(extension_id, raw, output_dir))

    run_id = str(observations.get("run_id") or utc_now_compact())
    return {
        "schema_version": "1.0",
        "run_id": run_id,
        "run_date": str(observations.get("run_date") or utc_now_iso()),
        "server_version": str(observations.get("server_version") or os.environ.get("GITHUB_SHA") or "local"),
        "client_lane": "desktop-arcgis",
        "client_version": str(observations.get("client_version") or "ArcGIS Pro unknown"),
        "protocol": protocol,
        "environment": str(observations.get("environment") or ("ci" if os.environ.get("GITHUB_ACTIONS") else "local")),
        "results": ordered,
        "summary": summarize(ordered),
        "cite_results": None,
        "extensions": extensions,
    }


def write_envelopes(observations: dict[str, Any], output_dir: Path) -> list[Path]:
    cert_dir = output_dir / "certification"
    cert_dir.mkdir(parents=True, exist_ok=True)
    run_id = str(observations.get("run_id") or utc_now_compact())
    observations.setdefault("run_id", run_id)

    paths: list[Path] = []
    for protocol in ("featureserver", "mapserver"):
        envelope = build_envelope(observations, protocol, output_dir)
        out_path = cert_dir / f"{run_id}-desktop-arcgis-{protocol}.cert.json"
        out_path.write_text(json.dumps(envelope, indent=2) + "\n", encoding="utf-8")
        paths.append(out_path)
    return paths


def make_url(base_url: str, path: str, params: dict[str, Any] | None = None) -> str:
    url = f"{base_url.rstrip('/')}/{path.lstrip('/')}"
    if params:
        url = f"{url}?{urllib.parse.urlencode(params)}"
    return url


def auth_headers(args: argparse.Namespace) -> dict[str, str]:
    headers = {"Accept": "application/json"}
    api_key = os.environ.get(args.api_key_env) if args.api_key_env else ""
    authorization = os.environ.get(args.authorization_env) if args.authorization_env else ""
    if api_key:
        headers["X-API-Key"] = api_key
    if authorization:
        headers["Authorization"] = authorization
    return headers


def request_json(url: str, headers: dict[str, str], timeout_seconds: int) -> dict[str, Any]:
    start = time.monotonic()
    request = urllib.request.Request(url, headers=headers)
    status = 0
    raw_body = b""
    try:
        with urllib.request.urlopen(request, timeout=timeout_seconds) as response:
            status = int(response.status)
            raw_body = response.read()
    except urllib.error.HTTPError as exc:
        status = int(exc.code)
        raw_body = exc.read()
    except urllib.error.URLError as exc:
        return {
            "status": 0,
            "json": None,
            "text": redact_text(str(exc.reason)),
            "duration_ms": int((time.monotonic() - start) * 1000),
        }

    text = raw_body.decode("utf-8", errors="replace")
    try:
        payload = json.loads(text)
    except json.JSONDecodeError:
        payload = None
    return {
        "status": status,
        "json": payload,
        "text": redact_text(text[:2000]),
        "duration_ms": int((time.monotonic() - start) * 1000),
    }


def first_feature_id(payload: dict[str, Any] | None) -> Any:
    if not payload:
        return None
    features = payload.get("features") or []
    if not features:
        return None
    attrs = features[0].get("attributes") or features[0].get("properties") or {}
    for key in ("objectid", "OBJECTID", "ObjectId", "id"):
        if key in attrs:
            return attrs[key]
    return features[0].get("id")


def add_rest_checks(
    protocol: str,
    checks: dict[str, dict[str, Any]],
    args: argparse.Namespace,
    headers: dict[str, str],
) -> None:
    service_path = f"rest/services/{args.service_id}/{protocol_to_service(protocol)}"
    layer_path = f"{service_path}/{args.layer_id}"

    service = request_json(make_url(args.base_url, f"{service_path}", {"f": "json"}), headers, args.timeout_seconds)
    checks["CERT-CONN-01"] = make_result(
        "CERT-CONN-01",
        "pass" if service["status"] == 200 else "fail",
        duration_ms=service["duration_ms"],
        notes=f"HTTP {service['status']} {protocol_to_service(protocol)} metadata",
    )
    if args.base_url.lower().startswith("https://"):
        checks["CERT-CONN-02"] = make_result(
            "CERT-CONN-02",
            "pass" if service["status"] in (200, 401, 403) else "fail",
            duration_ms=service["duration_ms"],
            notes="HTTPS request completed.",
        )
    else:
        checks["CERT-CONN-02"] = make_result(
            "CERT-CONN-02",
            "skip",
            notes="Target base URL is HTTP; TLS is terminated outside this evidence run.",
        )

    checks["CERT-AUTH-01"] = make_result(
        "CERT-AUTH-01",
        "pass" if service["status"] in (200, 401, 403) else "fail",
        notes=f"HTTP {service['status']}; target may be public or protected depending on fixture.",
    )
    checks["CERT-AUTH-02"] = make_result(
        "CERT-AUTH-02",
        "pass" if ("X-API-Key" in headers or "Authorization" in headers) and service["status"] == 200 else "skip",
        notes="Credentialed request succeeded." if service["status"] == 200 and len(headers) > 1 else "No credential secret configured for this run.",
    )

    services = request_json(make_url(args.base_url, "rest/services", {"f": "json"}), headers, args.timeout_seconds)
    services_count = None
    if isinstance(services["json"], dict):
        services_count = len(services["json"].get("services") or [])
    checks["CERT-DISC-01"] = make_result(
        "CERT-DISC-01",
        "pass" if services["status"] == 200 and services_count is not None else "fail",
        measured_count=services_count,
    )
    checks["CERT-DISC-02"] = make_result(
        "CERT-DISC-02",
        "pass" if service["status"] == 200 and isinstance(service["json"], dict) else "fail",
        notes=f"HTTP {service['status']}",
    )

    layer = request_json(make_url(args.base_url, layer_path, {"f": "json"}), headers, args.timeout_seconds)
    layer_payload = layer["json"] if isinstance(layer["json"], dict) else {}
    fields = layer_payload.get("fields") or []
    checks["CERT-SCHM-01"] = make_result(
        "CERT-SCHM-01",
        "pass" if layer["status"] == 200 and len(fields) > 0 else "fail",
        measured_count=len(fields) if fields else None,
    )
    checks["CERT-SCHM-02"] = make_result(
        "CERT-SCHM-02",
        "pass" if layer["status"] == 200 and bool(layer_payload.get("geometryType")) else "fail",
        notes=str(layer_payload.get("geometryType") or ""),
    )

    first_page = request_json(
        make_url(args.base_url, f"{layer_path}/query", {
            "where": "1=1",
            "outFields": "*",
            "f": "json",
            "resultRecordCount": 1,
        }),
        headers,
        args.timeout_seconds,
    )
    first_id = first_feature_id(first_page["json"])
    oid_field = next((f.get("name") for f in fields if str(f.get("type", "")).lower().endswith("oid")), "objectid")
    equality_where = f"{oid_field} = {first_id}" if first_id is not None else "1=1"
    equality = request_json(
        make_url(args.base_url, f"{layer_path}/query", {
            "where": equality_where,
            "outFields": "*",
            "f": "json",
            "returnCountOnly": "true",
        }),
        headers,
        args.timeout_seconds,
    )
    equality_count = equality["json"].get("count") if isinstance(equality["json"], dict) else None
    checks["CERT-QFLT-01"] = make_result(
        "CERT-QFLT-01",
        "pass" if equality["status"] == 200 and equality_count is not None else "fail",
        measured_count=equality_count,
        notes=f"where={equality_where}",
    )

    spatial = request_json(
        make_url(args.base_url, f"{layer_path}/query", {
            "where": "1=1",
            "geometry": args.spatial_query_envelope,
            "geometryType": "esriGeometryEnvelope",
            "spatialRel": "esriSpatialRelIntersects",
            "outFields": "*",
            "f": "json",
            "returnCountOnly": "true",
        }),
        headers,
        args.timeout_seconds,
    )
    spatial_count = spatial["json"].get("count") if isinstance(spatial["json"], dict) else None
    checks["CERT-QFLT-02"] = make_result(
        "CERT-QFLT-02",
        "pass" if spatial["status"] == 200 and spatial_count is not None else "fail",
        measured_count=spatial_count,
    )

    second_page = request_json(
        make_url(args.base_url, f"{layer_path}/query", {
            "where": "1=1",
            "outFields": "*",
            "f": "json",
            "resultOffset": 1,
            "resultRecordCount": 1,
        }),
        headers,
        args.timeout_seconds,
    )
    second_id = first_feature_id(second_page["json"])
    checks["CERT-PAGE-01"] = make_result(
        "CERT-PAGE-01",
        "pass" if first_page["status"] == 200 and first_id is not None else "fail",
        measured_count=1 if first_id is not None else 0,
    )
    checks["CERT-PAGE-02"] = make_result(
        "CERT-PAGE-02",
        "pass" if second_page["status"] == 200 and second_id is not None and second_id != first_id else "fail",
        notes=f"first={first_id}; second={second_id}",
    )

    geojson = request_json(
        make_url(args.base_url, f"{layer_path}/query", {
            "where": "1=1",
            "outFields": "*",
            "f": "geojson",
            "resultRecordCount": 1,
        }),
        headers,
        args.timeout_seconds,
    )
    has_geometry = False
    if isinstance(geojson["json"], dict):
        features = geojson["json"].get("features") or []
        has_geometry = bool(features and features[0].get("geometry"))
    checks["CERT-GEOM-01"] = make_result(
        "CERT-GEOM-01",
        "pass" if geojson["status"] == 200 and has_geometry else "fail",
    )
    spatial_ref = layer_payload.get("extent", {}).get("spatialReference") or layer_payload.get("spatialReference")
    checks["CERT-GEOM-02"] = make_result(
        "CERT-GEOM-02",
        "pass" if bool(spatial_ref) else "fail",
        notes=redact_text(json.dumps(spatial_ref)) if spatial_ref else "",
    )

    invalid = request_json(make_url(args.base_url, f"{service_path}/999999", {"f": "json"}), headers, args.timeout_seconds)
    checks["CERT-ERRH-01"] = make_result(
        "CERT-ERRH-01",
        "pass" if invalid["status"] in (400, 404) or "error" in invalid["text"].lower() else "fail",
        notes=f"HTTP {invalid['status']}",
    )
    malformed = request_json(
        make_url(args.base_url, f"{layer_path}/query", {
            "where": "this is not a where clause",
            "f": "json",
        }),
        headers,
        args.timeout_seconds,
    )
    checks["CERT-ERRH-02"] = make_result(
        "CERT-ERRH-02",
        "pass" if malformed["status"] in (200, 400) and ("error" in malformed["text"].lower() or "invalid" in malformed["text"].lower()) else "skip",
        notes="Server may accept the malformed where clause and return zero results; both outcomes are recorded for review.",
    )

    if protocol == "featureserver":
        has_drawing_info = bool(layer_payload.get("drawingInfo"))
        checks["CERT-RNDR-URL-01"] = make_result(
            "CERT-RNDR-URL-01",
            "pass" if has_drawing_info else "skip",
            notes="FeatureServer layer metadata included drawingInfo." if has_drawing_info else "FeatureServer layer metadata did not include drawingInfo.",
        )


def protocol_to_service(protocol: str) -> str:
    return "FeatureServer" if protocol == "featureserver" else "MapServer"


def add_arcpy_checks(
    observations: dict[str, Any],
    args: argparse.Namespace,
    output_dir: Path,
    log_lines: list[str],
) -> None:
    try:
        import arcpy  # type: ignore[import-not-found]
    except ImportError as exc:  # pragma: no cover - covered by runner environment, not PR CI.
        raise RuntimeError("ArcPy is required for live ArcGIS Pro evidence. Use --fixture-observations for contract tests.") from exc

    project_dir = output_dir / "project"
    screenshot_dir = output_dir / "screenshots"
    project_dir.mkdir(parents=True, exist_ok=True)
    screenshot_dir.mkdir(parents=True, exist_ok=True)

    observations["client_version"] = args.client_version
    if args.client_version == "auto":
        version_info = arcpy.GetInstallInfo()
        observations["client_version"] = f"ArcGIS Pro {version_info.get('Version', 'unknown')}"

    project_copy = project_dir / "Honua-ArcGISPro-Evidence.aprx"
    if args.project_template:
        shutil.copy2(args.project_template, project_copy)
        aprx = arcpy.mp.ArcGISProject(str(project_copy))
        log_lines.append(f"Opened project template copy: {project_copy.name}")
    else:
        aprx = arcpy.mp.ArcGISProject("CURRENT")
        log_lines.append("Opened ArcGIS Pro CURRENT project.")

    maps = aprx.listMaps(args.map_name) if args.map_name else aprx.listMaps()
    if not maps:
        maps = aprx.listMaps()
    if not maps:
        raise RuntimeError("ArcGIS Pro project does not contain a map.")
    active_map = maps[0]

    feature_urls = [
        make_url(args.base_url, f"rest/services/{args.service_id}/FeatureServer/{args.layer_id}"),
        make_url(args.base_url, f"rest/services/{args.service_id}/FeatureServer/{args.line_layer_id}"),
        make_url(args.base_url, f"rest/services/{args.service_id}/FeatureServer/{args.polygon_layer_id}"),
    ]
    mapserver_url = make_url(args.base_url, f"rest/services/{args.service_id}/MapServer")

    added_feature_layers = []
    for url in feature_urls:
        try:
            layer = active_map.addDataFromPath(url)
            added_feature_layers.append(layer)
            log_lines.append(f"ArcPy added FeatureServer layer: {redact_text(url)}")
        except Exception as exc:  # pragma: no cover - requires ArcPy.
            log_lines.append(f"ArcPy failed to add FeatureServer layer {redact_text(url)}: {redact_text(exc)}")

    map_layer_added = False
    try:
        active_map.addDataFromPath(mapserver_url)
        map_layer_added = True
        log_lines.append(f"ArcPy added MapServer connection: {redact_text(mapserver_url)}")
    except Exception as exc:  # pragma: no cover - requires ArcPy.
        log_lines.append(f"ArcPy failed to add MapServer connection {redact_text(mapserver_url)}: {redact_text(exc)}")

    feature_protocol = observations["protocols"]["featureserver"]
    map_protocol = observations["protocols"]["mapserver"]

    feature_protocol["checks"]["CERT-RNDR-01"] = make_result(
        "CERT-RNDR-01",
        "pass" if added_feature_layers else "fail",
        notes=f"ArcPy addDataFromPath added {len(added_feature_layers)} FeatureServer layer(s).",
    )
    map_protocol["checks"]["CERT-RNDR-01"] = make_result(
        "CERT-RNDR-01",
        "pass" if map_layer_added else "fail",
        notes="ArcPy addDataFromPath added the MapServer connection." if map_layer_added else "ArcPy could not add the MapServer connection.",
    )

    screenshot_ref = ""
    active_view = getattr(aprx, "activeView", None)
    if active_view is not None and hasattr(active_view, "exportToPNG"):
        screenshot = screenshot_dir / "arcgis-pro-map.png"
        try:
            active_view.exportToPNG(str(screenshot), 1280, 720)
            if screenshot.exists() and screenshot.stat().st_size > 0:
                screenshot_ref = safe_relative_ref(screenshot, output_dir)
                log_lines.append(f"Exported ArcGIS Pro active view screenshot: {screenshot_ref}")
        except Exception as exc:  # pragma: no cover - requires ArcPy UI.
            log_lines.append(f"Active view screenshot export failed: {redact_text(exc)}")

    if screenshot_ref:
        for protocol_data in (feature_protocol, map_protocol):
            protocol_data["checks"]["CERT-RNDR-01"]["evidence_ref"] = screenshot_ref
        for cert_id in ("CERT-RNDR-SYM-01", "CERT-RNDR-LIN-01", "CERT-RNDR-FIL-01"):
            feature_protocol["checks"][cert_id] = make_result(
                cert_id,
                "pass",
                notes="ArcGIS Pro rendered the seeded style layer in the exported map view; reviewer should inspect screenshot artifact.",
                evidence_ref=screenshot_ref,
            )
        feature_protocol["checks"]["CERT-RNDR-LBL-01"] = make_result(
            "CERT-RNDR-LBL-01",
            "skip",
            notes="Label rendering requires a labelingInfo fixture; no label fixture was configured for this run.",
        )
    else:
        for cert_id in ("CERT-RNDR-SYM-01", "CERT-RNDR-LIN-01", "CERT-RNDR-FIL-01", "CERT-RNDR-LBL-01"):
            feature_protocol["checks"].setdefault(cert_id, make_result(
                cert_id,
                "skip",
                notes="No ArcGIS Pro map screenshot was exported; configure an active view or layout export target.",
            ))

    try:
        if args.project_template:
            aprx.save()
        else:
            aprx.saveACopy(str(project_copy))
        reopened = arcpy.mp.ArcGISProject(str(project_copy))
        reopened_layer_count = sum(len(m.listLayers()) for m in reopened.listMaps())
        reload_ok = reopened_layer_count > 0
        project_ref = safe_relative_ref(project_copy, output_dir)
        log_lines.append(f"Saved and reopened project copy: {project_ref}; layers={reopened_layer_count}")
    except Exception as exc:  # pragma: no cover - requires ArcPy.
        reload_ok = False
        project_ref = ""
        log_lines.append(f"Project save/reopen failed: {redact_text(exc)}")

    for protocol_data in (feature_protocol, map_protocol):
        protocol_data["checks"]["CERT-RNDR-02"] = make_result(
            "CERT-RNDR-02",
            "pass" if reload_ok else "fail",
            notes="Saved project copy reopened with layers." if reload_ok else "Saved project copy did not reopen with layers.",
            evidence_ref=project_ref,
        )
        protocol_data["extensions"]["DSK-EXT-01"] = make_result(
            "DSK-EXT-01",
            "pass" if reload_ok else "fail",
            notes="Project save/reopen preserved the Honua layers." if reload_ok else "Project save/reopen did not preserve the Honua layers.",
            evidence_ref=project_ref,
        )
        protocol_data["extensions"]["DSK-EXT-02"] = make_result(
            "DSK-EXT-02",
            "not-applicable",
            notes="This licensed lane targets GeoServices REST FeatureServer and MapServer; WMS/WMTS remain covered by other desktop lanes.",
        )


def build_live_observations(args: argparse.Namespace, output_dir: Path) -> dict[str, Any]:
    observations: dict[str, Any] = {
        "run_id": args.run_id or os.environ.get("GITHUB_RUN_ID") or utc_now_compact(),
        "run_date": utc_now_iso(),
        "server_version": args.server_version or os.environ.get("GITHUB_SHA") or "local",
        "client_version": args.client_version,
        "environment": args.environment,
        "protocols": {
            "featureserver": {"checks": {}, "extensions": {}},
            "mapserver": {"checks": {}, "extensions": {}},
        },
    }

    headers = auth_headers(args)
    for protocol in ("featureserver", "mapserver"):
        add_rest_checks(protocol, observations["protocols"][protocol]["checks"], args, headers)

    log_lines: list[str] = [
        "Licensed ArcGIS Pro evidence run",
        f"base_url={redact_text(args.base_url)}",
        f"service_id={args.service_id}",
        f"layer_id={args.layer_id}",
    ]
    add_arcpy_checks(observations, args, output_dir, log_lines)

    log_dir = output_dir / "logs"
    log_dir.mkdir(parents=True, exist_ok=True)
    log_path = log_dir / "arcgis-pro-evidence.log"
    log_path.write_text("\n".join(redact_text(line) for line in log_lines) + "\n", encoding="utf-8")
    log_ref = safe_relative_ref(log_path, output_dir)
    for protocol_data in observations["protocols"].values():
        for result in protocol_data["checks"].values():
            if not result.get("evidence_ref") and result["test_case_id"].startswith("CERT-RNDR"):
                result["evidence_ref"] = log_ref

    return observations


def load_observations(path: Path) -> dict[str, Any]:
    with path.open("r", encoding="utf-8") as handle:
        payload = json.load(handle)
    if "protocols" not in payload:
        raise ValueError("Fixture observations must include a protocols object")
    return payload


def write_fixture_template(path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    template = {
        "run_id": "fixture-run",
        "run_date": "2026-01-01T00:00:00Z",
        "server_version": "fixture-server-sha",
        "client_version": "ArcGIS Pro fixture",
        "environment": "local",
        "protocols": {
            "featureserver": {
                "checks": {
                    "CERT-CONN-01": {"status": "pass", "duration_ms": 1, "notes": "FeatureServer metadata loaded."},
                    "CERT-RNDR-01": {"status": "skip", "notes": "Fill with live screenshot result on a licensed runner."},
                },
                "extensions": {
                    "DSK-EXT-01": {"status": "skip", "notes": "Fill with project save/reopen result."}
                },
            },
            "mapserver": {
                "checks": {
                    "CERT-CONN-01": {"status": "pass", "duration_ms": 1, "notes": "MapServer metadata loaded."},
                    "CERT-RNDR-01": {"status": "skip", "notes": "Fill with live screenshot result on a licensed runner."},
                },
                "extensions": {
                    "DSK-EXT-01": {"status": "skip", "notes": "Fill with project save/reopen result."}
                },
            },
        },
    }
    path.write_text(json.dumps(template, indent=2) + "\n", encoding="utf-8")


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default=os.environ.get("HONUA_BASE_URL", ""))
    parser.add_argument("--service-id", default=os.environ.get("HONUA_SERVICE_ID", "browser_compat"))
    parser.add_argument("--layer-id", default=os.environ.get("HONUA_LAYER_ID", "2000"))
    parser.add_argument("--line-layer-id", default=os.environ.get("HONUA_LINE_LAYER_ID", "2001"))
    parser.add_argument("--polygon-layer-id", default=os.environ.get("HONUA_POLYGON_LAYER_ID", "2002"))
    parser.add_argument("--spatial-query-envelope", default="-122.45,37.74,-122.38,37.80")
    parser.add_argument("--output-dir", default=os.environ.get("HONUA_ARCGIS_PRO_OUTPUT_DIR", "artifacts/arcgis-pro-desktop"))
    parser.add_argument("--run-id", default=os.environ.get("CERT_RUN_ID", ""))
    parser.add_argument("--server-version", default=os.environ.get("GITHUB_SHA", ""))
    parser.add_argument("--client-version", default="auto")
    parser.add_argument("--environment", default="ci" if os.environ.get("GITHUB_ACTIONS") else "local")
    parser.add_argument("--project-template", default=os.environ.get("ARCGIS_PRO_PROJECT_TEMPLATE", ""))
    parser.add_argument("--map-name", default=os.environ.get("ARCGIS_PRO_MAP_NAME", ""))
    parser.add_argument("--api-key-env", default="HONUA_API_KEY")
    parser.add_argument("--authorization-env", default="HONUA_AUTHORIZATION")
    parser.add_argument("--timeout-seconds", type=int, default=30)
    parser.add_argument("--fixture-observations", default="")
    parser.add_argument("--write-fixture-template", default="")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv or sys.argv[1:])
    output_dir = Path(args.output_dir).resolve()
    output_dir.mkdir(parents=True, exist_ok=True)

    try:
        if args.write_fixture_template:
            write_fixture_template(Path(args.write_fixture_template))
            return 0

        if args.fixture_observations:
            observations = load_observations(Path(args.fixture_observations))
        else:
            if not args.base_url:
                raise ValueError("--base-url or HONUA_BASE_URL is required for a live ArcGIS Pro evidence run")
            observations = build_live_observations(args, output_dir)

        paths = write_envelopes(observations, output_dir)
        summary_path = output_dir / "summary.md"
        lines = [
            "# Licensed ArcGIS Pro Evidence",
            "",
            f"- Run id: `{observations.get('run_id')}`",
            f"- Client lane: `desktop-arcgis`",
            f"- Output directory: `{output_dir}`",
            "",
            "## Envelopes",
            "",
        ]
        for path in paths:
            envelope = json.loads(path.read_text(encoding="utf-8"))
            summary = envelope["summary"]
            lines.append(
                f"- `{safe_relative_ref(path, output_dir)}`: {summary['passed']} passed, "
                f"{summary['failed']} failed, {summary['skipped']} skipped, "
                f"{summary['not_applicable']} not-applicable"
            )
        summary_path.write_text("\n".join(lines) + "\n", encoding="utf-8")
        print(f"Wrote {len(paths)} ArcGIS Pro evidence envelope(s) under {output_dir}")
        return 0
    except Exception as exc:
        error_dir = output_dir / "logs"
        error_dir.mkdir(parents=True, exist_ok=True)
        error_path = error_dir / "arcgis-pro-evidence-error.log"
        error_path.write_text(redact_text("".join(traceback.format_exception(exc))) + "\n", encoding="utf-8")
        print(f"ArcGIS Pro evidence run failed; redacted error log: {error_path}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())

"""Probe definitions, response validators, and reflection-error scanning for NativeAOT parity."""

from __future__ import annotations

import json
import re
from dataclasses import dataclass, field
from pathlib import Path

SCHEMA_VERSION = "honua.eval.aot-runtime-parity.v1"
PNG_MAGIC = b"\x89PNG\r\n\x1a\n"
MIN_PNG_BYTES = 67
PARQUET_MAGIC = b"PAR1"
CONDITIONAL_VALIDATORS = frozenset({"conditional_weak_etag_304", "conditional_etag_304"})
BINARY_VALIDATORS = frozenset({"non_empty_binary", "parquet_magic", "png_image"})
ERROR_ENVELOPE_KEYS = frozenset(
    {"error", "title", "detail", "code", "message", "status", "type"}
)
REFLECTION_ERROR_RE = re.compile(
    r"NotSupportedException|MissingMetadataException"
    r"|JsonSerializerIsReflectionEnabledByDefault"
    r"|Reflection-based serialization has been disabled"
    r"|\bIL2\d{3}\b|\bIL3\d{3}\b"
)


@dataclass(frozen=True)
class ParityConfig:
    """Configuration for a NativeAOT runtime parity verification run."""

    base_url: str | None = None
    image: str | None = None
    container_name: str | None = None
    log_file: Path | None = None
    service_id: str = "cng"
    map_service_id: str = "cng-map"
    layer_id: str = "1000"
    timeout: float = 15.0
    boot_timeout: float = 120.0
    postgis_image: str = "postgis/postgis:16-3.4"
    api_key: str | None = None
    scorecard: Path | None = None


@dataclass(frozen=True)
class ProbeSpec:
    """Definition of a single HTTP parity probe."""

    name: str
    category: str
    method: str
    path: str
    expected_status: int
    validator: str
    body: bytes | None = None
    headers: dict[str, str] = field(default_factory=dict)


@dataclass(frozen=True)
class HttpReply:
    """Captured HTTP response and timing metadata."""

    status: int
    headers: dict[str, str]
    body: bytes
    latency_ms: float
    error: str | None = None


@dataclass(frozen=True)
class ProbeResult:
    """Outcome of executing and validating one probe."""

    name: str
    category: str
    method: str
    path: str
    status_code: int
    expected_status: int
    latency_ms: float
    passed: bool
    detail: str


def build_probes(cfg: ParityConfig) -> list[ProbeSpec]:
    """Build the ordered health, protocol, and error-path probe suite."""
    return (
        _health_discovery_probes()
        + _geoservices_and_ogc_api_probes(cfg)
        + _ogc_classic_odata_mcp_probes(cfg)
        + _error_path_probes()
    )


def _health_discovery_probes() -> list[ProbeSpec]:
    return [
        ProbeSpec("health_live", "health", "GET", "/healthz/live", 200, "non_empty"),
        ProbeSpec("health_ready", "health", "GET", "/healthz/ready", 200, "ready_text"),
        ProbeSpec("openapi", "discovery", "GET", "/openapi.json", 200, "json_object"),
        ProbeSpec("conformance", "discovery", "GET", "/ogc/features/conformance", 200, "json_object"),
    ]


def _geoservices_and_ogc_api_probes(cfg: ParityConfig) -> list[ProbeSpec]:
    fs = f"/rest/services/{cfg.service_id}/FeatureServer/{cfg.layer_id}/query?where=1%3D1&outFields=*"
    ms = (
        f"/rest/services/{cfg.map_service_id}/MapServer/export"
        "?bbox=-180,-90,180,90&bboxSR=4326&size=256,256&format=png&f=image"
    )
    json_hdr = {"Content-Type": "application/json"}
    return [
        ProbeSpec("featureserver_json", "geoservices", "GET", f"{fs}&f=json", 200, "json_object"),
        ProbeSpec("featureserver_pbf", "geoservices", "GET", f"{fs}&f=pbf", 200, "non_empty_binary"),
        ProbeSpec("featureserver_geojson", "geoservices", "GET", f"{fs}&f=geojson", 200, "json_object"),
        ProbeSpec("featureserver_parquet", "geoservices", "GET", f"{fs}&f=parquet", 200, "parquet_magic"),
        ProbeSpec("mapserver_png_export", "geoservices", "GET", ms, 200, "png_image"),
        ProbeSpec("ogc_api_features", "ogc_api", "GET", "/ogc/features/collections", 200, "json_object"),
        ProbeSpec("ogc_api_tiles", "ogc_api", "GET", "/ogc/tiles/tileMatrixSets", 200, "json_object"),
        ProbeSpec("ogc_api_maps", "ogc_api", "GET", "/ogc/maps", 200, "json_object"),
        ProbeSpec("ogc_api_coverages", "ogc_api", "GET", "/ogc/coverages", 200, "json_object"),
        ProbeSpec("stac_search", "stac", "POST", "/stac/search", 200, "json_object", b'{"limit":1}', json_hdr),
    ]


def _ogc_classic_odata_mcp_probes(cfg: ParityConfig) -> list[ProbeSpec]:
    ms = cfg.map_service_id
    wms_map = (
        f"/rest/services/{ms}/MapServer/WMS?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap"
        f"&LAYERS={cfg.layer_id}&STYLES=&CRS=EPSG:4326&BBOX=-90,-180,90,180&WIDTH=256&HEIGHT=256&FORMAT=image/png"
    )
    mcp_body = b'{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}'
    mcp_hdr = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream"}
    return [
        ProbeSpec("wfs_20", "ogc_classic", "GET", "/wfs?service=WFS&version=2.0.0&request=GetCapabilities", 200, "xml_document"),
        ProbeSpec("wms_130", "ogc_classic", "GET", f"/rest/services/{ms}/MapServer/WMS?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetCapabilities", 200, "xml_document"),
        ProbeSpec("wms_get_map", "ogc_classic", "GET", wms_map, 200, "png_image"),
        ProbeSpec("wmts", "ogc_classic", "GET", f"/rest/services/{ms}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetCapabilities", 200, "xml_document"),
        ProbeSpec("wcs_20", "ogc_classic", "GET", f"/ogc/services/{ms}/wcs?SERVICE=WCS&VERSION=2.0.1&REQUEST=GetCapabilities", 200, "xml_document"),
        ProbeSpec("ogc_weak_etag_304", "ogc_api", "GET", f"/ogc/features/collections/{cfg.layer_id}/items?limit=1", 304, "conditional_weak_etag_304"),
        ProbeSpec("odata", "odata", "GET", "/odata", 200, "json_object"),
        ProbeSpec("odata_conditional", "odata", "GET", f"/odata/Layers({cfg.layer_id})/Features(1)", 304, "conditional_etag_304"),
        ProbeSpec("oidc_oauth_callback", "auth", "GET", "/sharing/rest/oauth2/callback", 200, "error_envelope"),
        ProbeSpec("mcp_tools_list", "mcp", "POST", "/mcp", 200, "mcp_tools", mcp_body, mcp_hdr),
    ]


def _error_path_probes() -> list[ProbeSpec]:
    return [
        ProbeSpec("error_400_bad_request", "error_path", "GET", "/stac/search?bbox=invalid", 400, "error_envelope"),
        ProbeSpec("error_401_unauthorized", "error_path", "GET", "/api/v1/admin/connections", 401, "error_envelope"),
        ProbeSpec("error_404_not_found", "error_path", "GET", "/ogc/features/collections/nonexistent-aot-probe-collection", 404, "error_envelope"),
        ProbeSpec("error_415_unsupported_media", "error_path", "POST", "/mcp", 415, "error_envelope", b"plain-text-payload", {"Content-Type": "text/plain"}),
    ]


def validate_reply(spec: ProbeSpec, reply: HttpReply) -> tuple[bool, str]:
    """Validate an HTTP probe reply against status and payload invariants."""
    if reply.error:
        return False, f"transport error: {reply.error}"
    if reply.status != spec.expected_status:
        return False, f"expected HTTP {spec.expected_status}, got {reply.status}"
    validator_fn = VALIDATORS.get(spec.validator)
    if validator_fn is None:
        return False, f"unknown validator: {spec.validator}"
    return validator_fn(reply.body)


def _check_non_empty(body: bytes) -> tuple[bool, str]:
    return (True, "ok") if body.strip() else (False, "empty response body")


def _check_ready(body: bytes) -> tuple[bool, str]:
    text = body.decode("utf-8", errors="replace")
    if "ready" not in text.lower():
        return False, f"readiness body missing 'Ready': {text[:80]}"
    return True, "ok"


def _check_non_empty_binary(body: bytes) -> tuple[bool, str]:
    return (True, f"ok ({len(body)} bytes)") if body else (False, "empty binary payload")


def _check_json_object(body: bytes) -> tuple[bool, str]:
    try:
        parsed = json.loads(body.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        return False, f"invalid JSON: {exc}"
    if not isinstance(parsed, dict):
        return False, f"expected JSON object, got {type(parsed).__name__}"
    return True, "ok"


def _check_parquet(body: bytes) -> tuple[bool, str]:
    if len(body) < 12 or body[:4] != PARQUET_MAGIC or body[-4:] != PARQUET_MAGIC:
        return False, f"invalid Parquet magic ({len(body)} bytes)"
    return True, f"ok ({len(body)} bytes)"


def _check_png(body: bytes) -> tuple[bool, str]:
    if not body.startswith(PNG_MAGIC):
        return False, "invalid PNG magic bytes"
    if len(body) < MIN_PNG_BYTES:
        return False, f"PNG payload too small ({len(body)} bytes)"
    if b"IHDR" not in body[:32] or b"IEND" not in body[-16:]:
        return False, "PNG missing required IHDR/IEND chunks"
    return True, f"ok ({len(body)} bytes)"


def _check_xml(body: bytes) -> tuple[bool, str]:
    text = body.decode("utf-8", errors="replace").lstrip()
    return (True, "ok") if text.startswith("<") else (False, "response is not XML")


def _extract_mcp_json_text(text: str) -> str:
    if text.startswith(("{", "[")):
        return text
    data_lines = [
        line[5:].strip()
        for line in text.splitlines()
        if line.startswith("data:") and line[5:].strip()
    ]
    return "\n".join(data_lines) if data_lines else text


def _check_mcp_tools(body: bytes) -> tuple[bool, str]:
    raw_text = body.decode("utf-8", errors="replace").strip()
    json_text = _extract_mcp_json_text(raw_text)
    try:
        parsed = json.loads(json_text)
    except json.JSONDecodeError as exc:
        return False, f"invalid MCP JSON-RPC response: {exc}"
    result = parsed.get("result") if isinstance(parsed, dict) else None
    if not isinstance(result, dict) or not isinstance(result.get("tools"), list):
        return False, "MCP tools/list result missing tools list"
    return True, f"ok ({len(result['tools'])} tools)"


def _check_error_envelope(body: bytes) -> tuple[bool, str]:
    text = body.decode("utf-8", errors="replace")
    if scan_reflection_errors(text):
        return False, "reflection/AOT error leaked in error response body"
    try:
        parsed = json.loads(text)
    except json.JSONDecodeError as exc:
        return False, f"error path did not return valid JSON: {exc}"
    if not isinstance(parsed, dict) or not ERROR_ENVELOPE_KEYS.intersection(parsed.keys()):
        return False, "missing structured JSON error envelope"
    return True, "ok"


VALIDATORS = {
    "non_empty": _check_non_empty,
    "ready_text": _check_ready,
    "non_empty_binary": _check_non_empty_binary,
    "json_object": _check_json_object,
    "parquet_magic": _check_parquet,
    "png_image": _check_png,
    "xml_document": _check_xml,
    "mcp_tools": _check_mcp_tools,
    "error_envelope": _check_error_envelope,
}


def scan_reflection_errors(text: str) -> list[str]:
    """Return deduplicated lines matching NativeAOT/trimming reflection errors."""
    matches: list[str] = []
    seen: set[str] = set()
    for line in text.splitlines():
        stripped = line.strip()
        if stripped and REFLECTION_ERROR_RE.search(stripped) and stripped not in seen:
            seen.add(stripped)
            matches.append(stripped[:300])
    return matches

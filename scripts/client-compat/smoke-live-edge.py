#!/usr/bin/env python3
# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""Read-only live edge smoke test for Honua behind CloudFront + AWS WAF.

Verifies health, OGC API Features caching/ETag 304 behavior, GeoServices catalog,
uppercase /WMTS pass-through, GIS SQL/CQL2/OData query patterns, and programmatic
GIS User-Agent headers without triggering HTTP 403 WAF blocks.
"""

from __future__ import annotations

import argparse
import datetime
import json
import os
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from collections.abc import Callable
from dataclasses import asdict, dataclass
from pathlib import Path

GIS_USER_AGENTS = (
    ("gdal", "GDAL/3.9.0"),
    ("qgis", "QGIS/3.44.13"),
    ("httpx", "python-httpx/0.27"),
    ("curl", "curl/8.5.0"),
)

UA_PROBE_PATHS = (
    ("rest_services", "/rest/services?f=json", True),
    ("ogc_features", "/ogc/features", True),
    ("wmts_upper", "/WMTS?service=WMTS&request=GetCapabilities", False),
)

WAF_QUERY_PROBES = (
    (
        "waf_where_1eq1",
        "/rest/services/test_service/FeatureServer/0/query?where=1%3D1&f=json",
    ),
    (
        "waf_where_sql_like",
        "/rest/services/test_service/FeatureServer/0/query?"
        + urllib.parse.urlencode({"where": "OBJECTID > 0 AND NAME LIKE 'A%'", "f": "json"}),
    ),
    (
        "waf_cql2_filter",
        "/ogc/features/collections/0/items?"
        + urllib.parse.urlencode({"filter": "name = 'alpha' AND count > 0", "filter-lang": "cql2-text"}),
    ),
    (
        "waf_odata_filter",
        "/odata/test_service/0?"
        + urllib.parse.urlencode({"$filter": "count gt 0 and startswith(name,'A')"}),
    ),
)


@dataclass(frozen=True)
class HttpResponse:
    status: int
    headers: dict[str, str]
    body: bytes
    error: str = ""


@dataclass(frozen=True)
class ProbeResult:
    name: str
    passed: bool
    status: int | None
    duration_ms: int
    waf_blocked: bool
    detail: str


@dataclass(frozen=True)
class SmokeConfig:
    base_url: str
    api_key: str | None = None
    timeout: float = 15.0
    check_gdal: bool = False
    check_owslib: bool = False


HttpFetcher = Callable[[str, dict[str, str], float], HttpResponse]


def default_http_fetch(url: str, headers: dict[str, str], timeout: float) -> HttpResponse:
    """Issue a single HTTP GET using stdlib urllib without leaking credentials."""
    req = urllib.request.Request(url, method="GET", headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            norm = {k.lower(): v for k, v in resp.headers.items()}
            return HttpResponse(status=resp.status, headers=norm, body=resp.read(65536))
    except urllib.error.HTTPError as exc:
        norm = {k.lower(): v for k, v in (exc.headers.items() if exc.headers else [])}
        return HttpResponse(status=exc.code, headers=norm, body=exc.read(65536))
    except (urllib.error.URLError, TimeoutError, OSError) as exc:
        return HttpResponse(status=0, headers={}, body=b"", error=str(exc))


def _base_headers(config: SmokeConfig, extra: dict[str, str] | None = None) -> dict[str, str]:
    headers = {"Accept": "*/*"}
    if config.api_key:
        headers["X-API-Key"] = config.api_key
    if extra:
        headers.update(extra)
    return headers


def _is_waf_block(resp: HttpResponse) -> bool:
    return resp.status == 403


def _waf_detail(resp: HttpResponse) -> str:
    server = resp.headers.get("server", "")
    cf_err = resp.headers.get("x-cache", "")
    hint = f" (server={server}, x-cache={cf_err})" if (server or cf_err) else ""
    return f"HTTP 403 blocked at edge/WAF{hint}"


@dataclass(frozen=True)
class _FetchTarget:
    path: str
    extra_headers: dict[str, str] | None = None


def _timed_fetch(
    config: SmokeConfig,
    target: _FetchTarget,
    fetcher: HttpFetcher,
) -> tuple[HttpResponse, int]:
    url = f"{config.base_url.rstrip('/')}{target.path}"
    started = time.monotonic()
    resp = fetcher(url, _base_headers(config, target.extra_headers), config.timeout)
    elapsed_ms = int((time.monotonic() - started) * 1000)
    return resp, elapsed_ms


def _check_healthz(config: SmokeConfig, fetcher: HttpFetcher) -> ProbeResult:
    resp, ms = _timed_fetch(config, _FetchTarget("/healthz/ready"), fetcher)
    if resp.error:
        return ProbeResult("healthz_ready", False, None, ms, False, f"request error: {resp.error}")
    ok = resp.status == 200
    detail = "ready" if ok else f"expected HTTP 200, got {resp.status}"
    return ProbeResult("healthz_ready", ok, resp.status, ms, _is_waf_block(resp), detail)


def _check_ogc_caching(config: SmokeConfig, fetcher: HttpFetcher) -> list[ProbeResult]:
    resp, ms = _timed_fetch(config, _FetchTarget("/ogc/features"), fetcher)
    if resp.error:
        fail = ProbeResult("ogc_features_landing", False, None, ms, False, f"request error: {resp.error}")
        return [fail, ProbeResult("ogc_features_etag_304", False, None, 0, False, "skipped: landing failed")]

    cc = resp.headers.get("cache-control", "")
    etag = resp.headers.get("etag", "")
    landing_ok = resp.status == 200 and bool(cc) and bool(etag)
    detail = f"Cache-Control={cc!r}, ETag={etag!r}" if landing_ok else (
        f"status={resp.status}, Cache-Control={cc!r}, ETag={etag!r}"
    )
    landing = ProbeResult("ogc_features_landing", landing_ok, resp.status, ms, _is_waf_block(resp), detail)
    if not etag:
        return [landing, ProbeResult("ogc_features_etag_304", False, None, 0, False, "no ETag to revalidate")]

    reval_target = _FetchTarget("/ogc/features", {"If-None-Match": etag})
    reval, r_ms = _timed_fetch(config, reval_target, fetcher)
    reval_ok = reval.status == 304
    r_detail = "HTTP 304 Not Modified" if reval_ok else f"expected HTTP 304, got {reval.status}"
    return [landing, ProbeResult("ogc_features_etag_304", reval_ok, reval.status, r_ms, _is_waf_block(reval), r_detail)]


def _check_rest_catalog(config: SmokeConfig, fetcher: HttpFetcher) -> ProbeResult:
    resp, ms = _timed_fetch(config, _FetchTarget("/rest/services?f=json"), fetcher)
    if resp.error:
        return ProbeResult("rest_services_catalog", False, None, ms, False, f"request error: {resp.error}")
    try:
        parsed = json.loads(resp.body.decode("utf-8", errors="replace")) if resp.status == 200 else None
    except ValueError:
        parsed = None
    ok = resp.status == 200 and isinstance(parsed, dict)
    detail = "valid GeoServices catalog JSON" if ok else f"status={resp.status} (invalid or non-200)"
    return ProbeResult("rest_services_catalog", ok, resp.status, ms, _is_waf_block(resp), detail)


def _check_wmts_uppercase(config: SmokeConfig, fetcher: HttpFetcher) -> ProbeResult:
    resp, ms = _timed_fetch(config, _FetchTarget("/WMTS?service=WMTS&request=GetCapabilities"), fetcher)
    if resp.error:
        return ProbeResult("wmts_uppercase_getcapabilities", False, None, ms, False, f"request error: {resp.error}")
    if _is_waf_block(resp):
        return ProbeResult("wmts_uppercase_getcapabilities", False, 403, ms, True, _waf_detail(resp))
    ok = resp.status in (200, 404)
    detail = f"reached origin (HTTP {resp.status}, not WAF-blocked)" if ok else f"unexpected HTTP {resp.status}"
    return ProbeResult("wmts_uppercase_getcapabilities", ok, resp.status, ms, False, detail)


def _check_waf_query_probe(probe: tuple[str, str], config: SmokeConfig, fetcher: HttpFetcher) -> ProbeResult:
    name, path = probe
    resp, ms = _timed_fetch(config, _FetchTarget(path), fetcher)
    if resp.error:
        return ProbeResult(name, False, None, ms, False, f"request error: {resp.error}")
    if _is_waf_block(resp):
        return ProbeResult(name, False, 403, ms, True, _waf_detail(resp))
    ok = 200 <= resp.status < 500
    detail = f"HTTP {resp.status} (not WAF-blocked)" if ok else f"unexpected server error HTTP {resp.status}"
    return ProbeResult(name, ok, resp.status, ms, False, detail)


def _check_user_agent_probes(config: SmokeConfig, fetcher: HttpFetcher) -> list[ProbeResult]:
    results: list[ProbeResult] = []
    for ua_key, ua_value in GIS_USER_AGENTS:
        for path_key, path, require_200 in UA_PROBE_PATHS:
            name = f"ua_{ua_key}_{path_key}"
            resp, ms = _timed_fetch(config, _FetchTarget(path, {"User-Agent": ua_value}), fetcher)
            results.append(_score_ua_response((name, require_200), resp, ms))
    return results


def _score_ua_response(spec: tuple[str, bool], resp: HttpResponse, ms: int) -> ProbeResult:
    name, require_200 = spec
    if resp.error:
        return ProbeResult(name, False, None, ms, False, f"request error: {resp.error}")
    if _is_waf_block(resp):
        return ProbeResult(name, False, 403, ms, True, _waf_detail(resp))
    ok = (resp.status == 200) if require_200 else (resp.status in (200, 404))
    detail = f"HTTP {resp.status}" if ok else f"unexpected HTTP {resp.status}"
    return ProbeResult(name, ok, resp.status, ms, False, detail)


def _check_gdal_client(config: SmokeConfig) -> ProbeResult:
    started = time.monotonic()
    if shutil.which("ogrinfo") is None:
        return ProbeResult("client_gdal_oapif", False, None, 0, False, "ogrinfo binary not found in PATH")
    dsn = f"OAPIF:{config.base_url.rstrip('/')}/ogc/features"
    env = dict(os.environ)
    if config.api_key:
        env["GDAL_HTTP_HEADERS"] = f"X-API-Key: {config.api_key}"
    proc = subprocess.run(
        ["ogrinfo", "-ro", "-so", dsn],
        check=False,
        capture_output=True,
        text=True,
        timeout=config.timeout * 2,
        env=env,
    )
    ms = int((time.monotonic() - started) * 1000)
    ok = proc.returncode == 0
    detail = "ogrinfo opened OAPIF landing" if ok else f"ogrinfo failed (rc={proc.returncode})"
    return ProbeResult("client_gdal_oapif", ok, None, ms, False, detail)


def _check_owslib_client(config: SmokeConfig) -> ProbeResult:
    started = time.monotonic()
    try:
        from owslib.ogcapi.features import Features
    except ImportError:
        return ProbeResult("client_owslib_oapif", False, None, 0, False, "owslib package not installed")
    headers = {"X-API-Key": config.api_key} if config.api_key else None
    try:
        client = Features(f"{config.base_url.rstrip('/')}/ogc/features", headers=headers)
        collections = client.collections()
        ms = int((time.monotonic() - started) * 1000)
        count = len(collections.get("collections", [])) if isinstance(collections, dict) else 0
        return ProbeResult("client_owslib_oapif", True, 200, ms, False, f"listed {count} collection(s)")
    except Exception as exc:  # noqa: BLE001
        ms = int((time.monotonic() - started) * 1000)
        return ProbeResult("client_owslib_oapif", False, None, ms, False, f"OWSLib error: {type(exc).__name__}")


def run_smoke(config: SmokeConfig, fetcher: HttpFetcher | None = None) -> dict:
    """Execute all live edge smoke checks and return a structured JSON-ready report."""
    active_fetcher = fetcher if fetcher is not None else default_http_fetch
    results: list[ProbeResult] = [
        _check_healthz(config, active_fetcher),
        *_check_ogc_caching(config, active_fetcher),
        _check_rest_catalog(config, active_fetcher),
        _check_wmts_uppercase(config, active_fetcher),
    ]
    for probe in WAF_QUERY_PROBES:
        results.append(_check_waf_query_probe(probe, config, active_fetcher))
    results.extend(_check_user_agent_probes(config, active_fetcher))

    if config.check_gdal:
        results.append(_check_gdal_client(config))
    if config.check_owslib:
        results.append(_check_owslib_client(config))

    passed = sum(1 for r in results if r.passed)
    waf_blocks = sum(1 for r in results if r.waf_blocked)
    return {
        "base_url": config.base_url.rstrip("/"),
        "timestamp": datetime.datetime.now(datetime.UTC).isoformat(),
        "summary": {
            "total": len(results),
            "passed": passed,
            "failed": len(results) - passed,
            "waf_blocks": waf_blocks,
        },
        "checks": [asdict(r) for r in results],
    }


def format_summary_table(report: dict) -> str:
    """Render a crisp ASCII summary table for terminal output."""
    lines = [
        f"Live Edge Smoke: {report['base_url']}",
        f"{'STATUS':<6} | {'CHECK':<32} | {'HTTP':<4} | {'MS':>5} | DETAIL",
        "-" * 86,
    ]
    for row in report["checks"]:
        status = "PASS" if row["passed"] else "FAIL"
        code = str(row["status"]) if row["status"] is not None else "-"
        lines.append(f"{status:<6} | {row['name']:<32} | {code:<4} | {row['duration_ms']:>5} | {row['detail']}")
    summary = report["summary"]
    lines.append("-" * 86)
    lines.append(
        f"Summary: {summary['passed']}/{summary['total']} passed, "
        f"{summary['failed']} failed, {summary['waf_blocks']} WAF 403 block(s)."
    )
    return "\n".join(lines)


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--base-url", required=True, help="Live Honua base URL (e.g. https://...cloudfront.net)")
    parser.add_argument("--api-key", default=None, help="Optional API key (or set HONUA_API_KEY; never logged)")
    parser.add_argument("--json-out", type=Path, default=None, help="Optional path to write JSON smoke report")
    parser.add_argument("--timeout", type=float, default=15.0, help="Per-request timeout in seconds")
    parser.add_argument("--check-gdal", action="store_true", help="Also probe OAPIF via ogrinfo")
    parser.add_argument("--check-owslib", action="store_true", help="Also probe OAPIF via OWSLib")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    api_key = args.api_key if args.api_key is not None else os.environ.get("HONUA_API_KEY")
    config = SmokeConfig(
        base_url=args.base_url,
        api_key=api_key,
        timeout=args.timeout,
        check_gdal=args.check_gdal,
        check_owslib=args.check_owslib,
    )
    report = run_smoke(config)
    print(format_summary_table(report))
    if args.json_out is not None:
        args.json_out.parent.mkdir(parents=True, exist_ok=True)
        args.json_out.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    return 0 if report["summary"]["failed"] == 0 else 1


if __name__ == "__main__":
    sys.exit(main())

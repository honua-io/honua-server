# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""Unit tests for scripts/client-compat/smoke-live-edge.py."""

from __future__ import annotations

import importlib.util
import json
import sys
from pathlib import Path

SCRIPT = Path(__file__).parents[3] / "scripts" / "client-compat" / "smoke-live-edge.py"
SPEC = importlib.util.spec_from_file_location("smoke_live_edge", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
smoke = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = smoke
SPEC.loader.exec_module(smoke)


def _ok_fetcher(url: str, headers: dict[str, str], _timeout: float):
    if url.endswith("/healthz/ready"):
        return smoke.HttpResponse(200, {"content-type": "text/plain"}, b"Ready")
    if url.endswith("/ogc/features"):
        if headers.get("If-None-Match") == '"etag-v1"':
            return smoke.HttpResponse(304, {"etag": '"etag-v1"'}, b"")
        return smoke.HttpResponse(
            200,
            {"cache-control": "public, max-age=60", "etag": '"etag-v1"'},
            b'{"links":[]}',
        )
    if url.endswith("/rest/services?f=json"):
        return smoke.HttpResponse(200, {"content-type": "application/json"}, b'{"services":[]}')
    if "/WMTS?" in url:
        return smoke.HttpResponse(404, {"content-type": "application/json"}, b'{"error":"not found"}')
    return smoke.HttpResponse(404, {"content-type": "application/json"}, b'{"error":"unseeded"}')


def test_happy_path_all_checks_pass_with_unseeded_404_queries() -> None:
    cfg = smoke.SmokeConfig(base_url="https://edge.honua.test")
    report = smoke.run_smoke(cfg, fetcher=_ok_fetcher)

    assert report["summary"]["failed"] == 0
    assert report["summary"]["waf_blocks"] == 0
    assert report["summary"]["passed"] == report["summary"]["total"]
    assert report["summary"]["total"] == 21


def test_waf_403_on_wmts_and_sql_query_are_hard_failures() -> None:
    def blocked_fetcher(url: str, headers: dict[str, str], timeout: float):
        if "/WMTS?" in url or "where=" in url:
            return smoke.HttpResponse(403, {"server": "CloudFront", "x-cache": "Error from cloudfront"}, b"Blocked")
        return _ok_fetcher(url, headers, timeout)

    cfg = smoke.SmokeConfig(base_url="https://edge.honua.test")
    report = smoke.run_smoke(cfg, fetcher=blocked_fetcher)
    checks = {row["name"]: row for row in report["checks"]}

    assert not checks["wmts_uppercase_getcapabilities"]["passed"]
    assert checks["wmts_uppercase_getcapabilities"]["waf_blocked"]
    assert not checks["waf_where_1eq1"]["passed"]
    assert checks["waf_where_1eq1"]["waf_blocked"]
    assert not checks["waf_where_sql_like"]["passed"]
    assert report["summary"]["waf_blocks"] >= 3


def test_waf_403_on_programmatic_gis_user_agent_fails() -> None:
    def ua_blocked_fetcher(url: str, headers: dict[str, str], timeout: float):
        if headers.get("User-Agent", "").startswith("GDAL/"):
            return smoke.HttpResponse(403, {"server": "CloudFront"}, b"Forbidden")
        return _ok_fetcher(url, headers, timeout)

    cfg = smoke.SmokeConfig(base_url="https://edge.honua.test")
    report = smoke.run_smoke(cfg, fetcher=ua_blocked_fetcher)
    checks = {row["name"]: row for row in report["checks"]}

    assert not checks["ua_gdal_rest_services"]["passed"]
    assert checks["ua_gdal_rest_services"]["waf_blocked"]
    assert checks["ua_qgis_rest_services"]["passed"]
    assert report["summary"]["waf_blocks"] == 3


def test_missing_etag_and_non_304_fail_caching_checks() -> None:
    def no_cache_fetcher(url: str, headers: dict[str, str], timeout: float):
        if url.endswith("/ogc/features"):
            return smoke.HttpResponse(200, {"content-type": "application/json"}, b'{"links":[]}')
        return _ok_fetcher(url, headers, timeout)

    cfg = smoke.SmokeConfig(base_url="https://edge.honua.test")
    report = smoke.run_smoke(cfg, fetcher=no_cache_fetcher)
    checks = {row["name"]: row for row in report["checks"]}

    assert not checks["ogc_features_landing"]["passed"]
    assert not checks["ogc_features_etag_304"]["passed"]


def test_api_key_is_forwarded_and_never_leaked_in_output(tmp_path: Path, monkeypatch, capsys) -> None:
    secret = "super-secret-api-key-987654"
    seen_headers: list[dict[str, str]] = []

    def recording_fetcher(url: str, headers: dict[str, str], timeout: float):
        seen_headers.append(dict(headers))
        return _ok_fetcher(url, headers, timeout)

    monkeypatch.setattr(smoke, "default_http_fetch", recording_fetcher)
    json_out = tmp_path / "smoke-report.json"

    exit_code = smoke.main([
        "--base-url", "https://edge.honua.test",
        "--api-key", secret,
        "--json-out", str(json_out),
    ])

    captured = capsys.readouterr()
    report_text = json_out.read_text(encoding="utf-8")
    parsed = json.loads(report_text)

    assert exit_code == 0
    assert seen_headers and all(h.get("X-API-Key") == secret for h in seen_headers)
    assert secret not in captured.out
    assert secret not in captured.err
    assert secret not in report_text
    assert parsed["summary"]["passed"] == parsed["summary"]["total"]


def test_optional_gdal_and_owslib_checks_report_missing_tools(monkeypatch) -> None:
    monkeypatch.setattr(smoke.shutil, "which", lambda _cmd: None)
    monkeypatch.setitem(sys.modules, "owslib.ogcapi.features", None)

    cfg = smoke.SmokeConfig(base_url="https://edge.honua.test", check_gdal=True, check_owslib=True)
    report = smoke.run_smoke(cfg, fetcher=_ok_fetcher)
    checks = {row["name"]: row for row in report["checks"]}

    assert not checks["client_gdal_oapif"]["passed"]
    assert "ogrinfo" in checks["client_gdal_oapif"]["detail"]
    assert not checks["client_owslib_oapif"]["passed"]
    assert "owslib" in checks["client_owslib_oapif"]["detail"]

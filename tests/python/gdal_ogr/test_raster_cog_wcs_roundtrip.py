# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""GDAL raster round-trip tests for WCS 2.0, ImageServer GeoTIFF export, and /vsicurl/ COG."""

from __future__ import annotations

import json
import os
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass
from pathlib import Path

import pytest

pytestmark = [pytest.mark.integration, pytest.mark.gdal]

COG_ARTIFACT_PATH = "/api/v1/rasters/cog/cog/0/1.tif"


@dataclass(frozen=True)
class WcsFixtureTarget:
    service: str
    coverage: str
    expected_pixel: float
    subset_lon: tuple[float, float]
    subset_lat: tuple[float, float]


def _http_status(url: str, method: str = "GET") -> int:
    req = urllib.request.Request(url, method=method)
    try:
        with urllib.request.urlopen(req, timeout=20) as resp:
            return resp.status
    except urllib.error.HTTPError as exc:
        return exc.code


def _resolve_wcs_target(base_url: str) -> WcsFixtureTarget:
    configured = os.getenv("HONUA_GDAL_WCS_SERVICE")
    if configured:
        return WcsFixtureTarget(
            service=configured,
            coverage="coverage_2000",
            expected_pixel=180.0,
            subset_lon=(-122.44, -122.40),
            subset_lat=(37.75, 37.78),
        )
    fallback_url = f"{base_url}/ogc/wcs/svc-test-service-image?SERVICE=WCS&VERSION=2.0.1&REQUEST=GetCapabilities"
    if _http_status(fallback_url) == 200:
        return WcsFixtureTarget(
            service="svc-test-service-image",
            coverage="coverage_0",
            expected_pixel=128.0,
            subset_lon=(-122.48, -122.38),
            subset_lat=(37.72, 37.82),
        )
    pytest.skip("No WCS coverage service seeded in this environment (set HONUA_GDAL_WCS_SERVICE).")
    raise AssertionError("unreachable")


def _assert_valid_geotiff_info(info: dict, expected_size: list[int], expected_pixel: float) -> None:
    assert info["driverShortName"] == "GTiff", f"Expected GTiff, got {info.get('driverShortName')}"
    assert info["size"] == expected_size, f"Expected size {expected_size}, got {info.get('size')}"

    gt = info.get("geoTransform")
    assert isinstance(gt, list) and len(gt) == 6, f"Missing or invalid geoTransform: {gt}"
    assert gt[1] > 0, f"Expected positive west-to-east pixel width, got {gt[1]}"
    assert gt[5] < 0, f"Expected negative north-up pixel height (geoTransform[5] < 0), got {gt[5]}"

    wkt = (info.get("coordinateSystem") or {}).get("wkt", "")
    assert wkt and ("4326" in wkt or "WGS 84" in wkt), f"Missing WGS84/EPSG:4326 CRS WKT: {wkt[:120]}"

    bands = info.get("bands") or []
    assert len(bands) >= 1, "GeoTIFF has no raster bands."
    band = bands[0]
    assert (band["computedMin"], band["computedMax"]) == (expected_pixel, expected_pixel), (
        f"Expected band stats ({expected_pixel}, {expected_pixel}), "
        f"got ({band.get('computedMin')}, {band.get('computedMax')})"
    )


def _wcs_subset_getcoverage_url(base_url: str, target: WcsFixtureTarget) -> str:
    lon_min, lon_max = target.subset_lon
    lat_min, lat_max = target.subset_lat
    params = [
        ("SERVICE", "WCS"),
        ("VERSION", "2.0.1"),
        ("REQUEST", "GetCoverage"),
        ("COVERAGEID", target.coverage),
        ("FORMAT", "image/tiff"),
        ("SUBSET", f"Long({lon_min},{lon_max})"),
        ("SUBSET", f"Lat({lat_min},{lat_max})"),
        ("SCALESIZE", "x(32),y(32)"),
        ("INTERPOLATION", "bilinear"),
    ]
    return f"{base_url}/ogc/wcs/{target.service}?{urllib.parse.urlencode(params)}"


def _imageserver_export_url(base_url: str) -> tuple[str, float]:
    service = os.getenv("HONUA_GDAL_GEOSERVICES_SERVICE") or os.getenv("HONUA_GDAL_SERVICE_ID", "test_service")
    if service == "browser_compat":
        bbox, expected = "-122.45,37.73,-122.38,37.80", 180.0
    else:
        bbox, expected = "-122.5,37.7,-122.35,37.84", 128.0
    query = urllib.parse.urlencode({
        "bbox": bbox,
        "bboxSR": "4326",
        "imageSR": "4326",
        "size": "64,64",
        "format": "tiff",
        "f": "image",
    })
    return f"{base_url}/rest/services/{service}/ImageServer/exportImage?{query}", expected


def test_wcs_geotiff_roundtrip_geotransform_crs_and_stats(request, ogr_run, tmp_path: Path) -> None:
    """gdal_translate + gdalinfo verify WCS 2.0 GeoTIFF geotransform, CRS, and band stats."""
    base_url = request.getfixturevalue("base_url")
    target = _resolve_wcs_target(base_url)

    cache = tmp_path / "wcs-cache"
    cache.mkdir()
    output_tif = tmp_path / "wcs-full.tif"
    dsn = f"WCS:{base_url}/ogc/wcs/{target.service}?version=2.0.1&coverage={target.coverage}"

    ogr_run(
        ["gdal_translate", "-oo", f"CACHE={cache}", "-of", "GTiff", dsn, str(output_tif)],
        timeout=60.0,
    ).assert_success("gdal_translate failed on WCS 2.0 coverage")

    info = json.loads(
        ogr_run(["gdalinfo", "-json", "-mm", str(output_tif)]).assert_success().stdout
    )
    _assert_valid_geotiff_info(info, [64, 64], target.expected_pixel)
    request.getfixturevalue("evidence_collector").record(
        "test_wcs_geotiff_roundtrip_geotransform_crs_and_stats", "wcs", "read", "pass"
    )


def test_wcs_subset_and_bilinear_interpolation_geotiff(request, ogr_run, tmp_path: Path) -> None:
    """WCS 2.0 GetCoverage with SUBSET, SCALESIZE, and INTERPOLATION=bilinear produces valid GeoTIFF."""
    base_url = request.getfixturevalue("base_url")
    target = _resolve_wcs_target(base_url)
    coverage_url = _wcs_subset_getcoverage_url(base_url, target)
    output_tif = tmp_path / "wcs-subset-bilinear.tif"

    ogr_run(
        [
            "gdal_translate",
            "--config", "GDAL_DISABLE_READDIR_ON_OPEN", "EMPTY_DIR",
            "-of", "GTiff",
            f"/vsicurl/{coverage_url}",
            str(output_tif),
        ],
        timeout=60.0,
    ).assert_success("gdal_translate failed on WCS 2.0 SUBSET + INTERPOLATION=bilinear")

    info = json.loads(
        ogr_run(["gdalinfo", "-json", "-mm", str(output_tif)]).assert_success().stdout
    )
    _assert_valid_geotiff_info(info, [32, 32], target.expected_pixel)
    request.getfixturevalue("evidence_collector").record(
        "test_wcs_subset_and_bilinear_interpolation_geotiff", "wcs", "read", "pass"
    )


def test_imageserver_export_geotiff_via_gdalinfo(request, ogr_run, tmp_path: Path) -> None:
    """gdalinfo reads Honua ImageServer exportImage GeoTIFF with north-up transform and CRS."""
    base_url = request.getfixturevalue("base_url")
    export_url, expected_pixel = _imageserver_export_url(base_url)
    output_tif = tmp_path / "imageserver-export.tif"

    ogr_run(
        [
            "gdal_translate",
            "--config", "GDAL_DISABLE_READDIR_ON_OPEN", "EMPTY_DIR",
            "-of", "GTiff",
            f"/vsicurl/{export_url}",
            str(output_tif),
        ],
        timeout=60.0,
    ).assert_success("gdal_translate failed on ImageServer exportImage GeoTIFF")

    info = json.loads(
        ogr_run(["gdalinfo", "-json", "-mm", str(output_tif)]).assert_success().stdout
    )
    _assert_valid_geotiff_info(info, [64, 64], expected_pixel)


def test_published_cog_vsicurl_roundtrip(request, ogr_run, tmp_path: Path) -> None:
    """gdalinfo + windowed gdal_translate read the published COG over /vsicurl/."""
    base_url = request.getfixturevalue("base_url")
    cog_url = f"{base_url}{COG_ARTIFACT_PATH}"
    head_status = _http_status(cog_url, method="HEAD")

    if head_status == 404 and not os.getenv("HONUA_BASE_URL"):
        pytest.skip("Published COG artifact cog/0/1.tif is only seeded in the Docker compat lane.")
    assert head_status == 200, f"HEAD {cog_url} returned {head_status} (expected 200)."

    info_res = ogr_run(
        [
            "gdalinfo", "-json", "-mm",
            "--config", "GDAL_DISABLE_READDIR_ON_OPEN", "EMPTY_DIR",
            f"/vsicurl/{cog_url}",
        ],
        timeout=60.0,
    ).assert_success("gdalinfo could not open published COG over /vsicurl/")
    _assert_valid_geotiff_info(json.loads(info_res.stdout), [64, 64], 128.0)

    window_tif = tmp_path / "cog-window.tif"
    ogr_run(
        [
            "gdal_translate",
            "--config", "GDAL_DISABLE_READDIR_ON_OPEN", "EMPTY_DIR",
            "-of", "GTiff",
            "-srcwin", "0", "0", "32", "32",
            f"/vsicurl/{cog_url}",
            str(window_tif),
        ],
        timeout=60.0,
    ).assert_success("Windowed gdal_translate failed on /vsicurl/ COG")

    window_info = json.loads(
        ogr_run(["gdalinfo", "-json", "-mm", str(window_tif)]).assert_success().stdout
    )
    _assert_valid_geotiff_info(window_info, [32, 32], 128.0)

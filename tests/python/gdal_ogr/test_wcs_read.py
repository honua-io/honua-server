# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""GDAL interoperability with the seeded WCS 2.0.1 raster."""

from __future__ import annotations

import json
import os
import urllib.error
import urllib.request
from dataclasses import dataclass
from http import HTTPStatus

import pytest

GEOTRANSFORM_LENGTH = 6
SEEDED_RASTER_DIM = 64
BROWSER_COMPAT_PIXEL = 180.0
TEST_SERVICE_PIXEL = 128.0


@dataclass(frozen=True)
class WcsFixtureTarget:
    service: str
    coverage: str
    expected_pixel: float
    subset_lon: tuple[float, float]
    subset_lat: tuple[float, float]


def http_status(url: str, method: str = "GET") -> int:
    req = urllib.request.Request(url, method=method)
    try:
        with urllib.request.urlopen(req, timeout=20) as resp:
            return resp.status
    except urllib.error.HTTPError as exc:
        return exc.code


def resolve_wcs_target(base_url: str) -> WcsFixtureTarget:
    configured = os.getenv("HONUA_GDAL_WCS_SERVICE")
    if configured:
        return WcsFixtureTarget(
            service=configured,
            coverage="coverage_2000",
            expected_pixel=BROWSER_COMPAT_PIXEL,
            subset_lon=(-122.44, -122.40),
            subset_lat=(37.75, 37.78),
        )
    fallback_url = f"{base_url}/ogc/wcs/svc-test-service-image?SERVICE=WCS&VERSION=2.0.1&REQUEST=GetCapabilities"
    if http_status(fallback_url) == HTTPStatus.OK:
        return WcsFixtureTarget(
            service="svc-test-service-image",
            coverage="coverage_0",
            expected_pixel=TEST_SERVICE_PIXEL,
            subset_lon=(-122.48, -122.38),
            subset_lat=(37.72, 37.82),
        )
    pytest.skip("HONUA_GDAL_WCS_SERVICE requires the client compatibility seed")
    raise AssertionError("unreachable")


def assert_valid_geotiff_info(info: dict, expected_size: list[int], expected_pixel: float) -> None:
    assert info["driverShortName"] == "GTiff", f"Expected GTiff, got {info.get('driverShortName')}"
    assert info["size"] == expected_size, f"Expected size {expected_size}, got {info.get('size')}"

    gt = info.get("geoTransform")
    assert isinstance(gt, list) and len(gt) == GEOTRANSFORM_LENGTH, f"Invalid geoTransform: {gt}"
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


@pytest.mark.integration
@pytest.mark.gdal
@pytest.mark.gdal_evidence("wcs", "read")
def test_wcs_reads_seeded_pixels(request, ogr_run, tmp_path):
    base_url = request.getfixturevalue("base_url")
    target = resolve_wcs_target(base_url)
    connection = f"WCS:{base_url}/ogc/wcs/{target.service}?version=2.0.1&coverage={target.coverage}"
    cache = tmp_path / "wcs-cache"
    cache.mkdir()
    raster = tmp_path / "coverage.tif"
    ogr_run([
        "gdal_translate", "-oo", f"CACHE={cache}", "-of", "GTiff", connection, str(raster),
    ], timeout=60.0).assert_success("gdal_translate failed on WCS 2.0 coverage")

    result = ogr_run(["gdalinfo", "-json", "-mm", str(raster)]).assert_success()
    assert_valid_geotiff_info(json.loads(result.stdout), [SEEDED_RASTER_DIM, SEEDED_RASTER_DIM], target.expected_pixel)
    evidence_collector = request.getfixturevalue("evidence_collector")
    evidence_collector.record("test_wcs_reads_seeded_pixels", "wcs", "read", "pass")

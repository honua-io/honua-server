# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""GDAL raster round-trip tests for WCS 2.0 subset/interpolation, ImageServer GeoTIFF, and /vsicurl/ COG."""

from __future__ import annotations

import json
import os
import urllib.parse
from http import HTTPStatus
from pathlib import Path

import pytest

from .test_wcs_read import (
    BROWSER_COMPAT_PIXEL,
    SEEDED_RASTER_DIM,
    TEST_SERVICE_PIXEL,
    WcsFixtureTarget,
    assert_valid_geotiff_info,
    http_status,
    resolve_wcs_target,
)

pytestmark = [pytest.mark.integration, pytest.mark.gdal]

COG_ARTIFACT_PATH = "/api/v1/rasters/cog/cog/0/1.tif"
SUBSET_DIM = 32


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
        ("SCALESIZE", f"x({SUBSET_DIM}),y({SUBSET_DIM})"),
        ("INTERPOLATION", "bilinear"),
    ]
    return f"{base_url}/ogc/wcs/{target.service}?{urllib.parse.urlencode(params)}"


def _imageserver_export_url(base_url: str) -> tuple[str, float]:
    service = os.getenv("HONUA_GDAL_GEOSERVICES_SERVICE") or os.getenv("HONUA_GDAL_SERVICE_ID", "test_service")
    if service == "browser_compat":
        bbox, expected = "-122.45,37.73,-122.38,37.80", BROWSER_COMPAT_PIXEL
    else:
        bbox, expected = "-122.5,37.7,-122.35,37.84", TEST_SERVICE_PIXEL
    query = urllib.parse.urlencode({
        "bbox": bbox,
        "bboxSR": "4326",
        "imageSR": "4326",
        "size": f"{SEEDED_RASTER_DIM},{SEEDED_RASTER_DIM}",
        "format": "tiff",
        "f": "image",
    })
    return f"{base_url}/rest/services/{service}/ImageServer/exportImage?{query}", expected


@pytest.mark.gdal_evidence("wcs", "read")
def test_wcs_subset_and_bilinear_interpolation_geotiff(request, ogr_run, tmp_path: Path) -> None:
    """WCS 2.0 GetCoverage with SUBSET, SCALESIZE, and INTERPOLATION=bilinear produces valid GeoTIFF."""
    base_url = request.getfixturevalue("base_url")
    target = resolve_wcs_target(base_url)
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
    assert_valid_geotiff_info(info, [SUBSET_DIM, SUBSET_DIM], target.expected_pixel)
    request.getfixturevalue("evidence_collector").record(
        "test_wcs_subset_and_bilinear_interpolation_geotiff", "wcs", "read", "pass"
    )


@pytest.mark.gdal_evidence("imageserver", "read")
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
    assert_valid_geotiff_info(info, [SEEDED_RASTER_DIM, SEEDED_RASTER_DIM], expected_pixel)
    request.getfixturevalue("evidence_collector").record(
        "test_imageserver_export_geotiff_via_gdalinfo", "imageserver", "read", "pass"
    )


@pytest.mark.gdal_evidence("cog", "read")
def test_published_cog_vsicurl_roundtrip(request, ogr_run, tmp_path: Path) -> None:
    """gdalinfo + windowed gdal_translate read the published COG over /vsicurl/."""
    base_url = request.getfixturevalue("base_url")
    cog_url = f"{base_url}{COG_ARTIFACT_PATH}"
    head_status = http_status(cog_url, method="HEAD")

    if head_status == HTTPStatus.NOT_FOUND and not os.getenv("HONUA_BASE_URL"):
        pytest.skip("Published COG artifact cog/0/1.tif is only seeded in the Docker compat lane.")
    assert head_status == HTTPStatus.OK, f"HEAD {cog_url} returned {head_status} (expected 200)."

    info_res = ogr_run(
        [
            "gdalinfo", "-json", "-mm",
            "--config", "GDAL_DISABLE_READDIR_ON_OPEN", "EMPTY_DIR",
            f"/vsicurl/{cog_url}",
        ],
        timeout=60.0,
    ).assert_success("gdalinfo could not open published COG over /vsicurl/")
    assert_valid_geotiff_info(json.loads(info_res.stdout), [SEEDED_RASTER_DIM, SEEDED_RASTER_DIM], TEST_SERVICE_PIXEL)

    window_tif = tmp_path / "cog-window.tif"
    ogr_run(
        [
            "gdal_translate",
            "--config", "GDAL_DISABLE_READDIR_ON_OPEN", "EMPTY_DIR",
            "-of", "GTiff",
            "-srcwin", "0", "0", str(SUBSET_DIM), str(SUBSET_DIM),
            f"/vsicurl/{cog_url}",
            str(window_tif),
        ],
        timeout=60.0,
    ).assert_success("Windowed gdal_translate failed on /vsicurl/ COG")

    window_info = json.loads(
        ogr_run(["gdalinfo", "-json", "-mm", str(window_tif)]).assert_success().stdout
    )
    assert_valid_geotiff_info(window_info, [SUBSET_DIM, SUBSET_DIM], TEST_SERVICE_PIXEL)
    request.getfixturevalue("evidence_collector").record(
        "test_published_cog_vsicurl_roundtrip", "cog", "read", "pass"
    )

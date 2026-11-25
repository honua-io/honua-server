# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""GDAL interoperability with the seeded WCS 2.0.1 raster."""

import json
import os

import pytest


@pytest.mark.integration
@pytest.mark.gdal
def test_wcs_reads_seeded_pixels(base_url, ogr_run, tmp_path, evidence_collector):
    service = os.getenv("HONUA_GDAL_WCS_SERVICE")
    if not service:
        pytest.skip("HONUA_GDAL_WCS_SERVICE requires the client compatibility seed")

    connection = f"WCS:{base_url}/ogc/wcs/{service}?version=2.0.1&coverage=coverage_2000"
    cache = tmp_path / "wcs-cache"
    cache.mkdir()
    raster = tmp_path / "coverage.tif"
    ogr_run([
        "gdal_translate", "-oo", f"CACHE={cache}", "-of", "GTiff", connection, str(raster),
    ], timeout=60.0).assert_success()

    result = ogr_run(["gdalinfo", "-json", "-mm", str(raster)]).assert_success()
    info = json.loads(result.stdout)
    assert info["size"] == [64, 64]
    band, = info["bands"]
    assert (band["computedMin"], band["computedMax"]) == (180.0, 180.0)
    evidence_collector.record("test_wcs_reads_seeded_pixels", "wcs", "read", "pass")

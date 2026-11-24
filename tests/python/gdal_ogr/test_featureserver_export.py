# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""GDAL/OGR interoperability: GeoServices FeatureServer, export to GeoPackage.

The ``export`` category is deliberately outside the cert envelope (convert-gdal-results.py);
a failure still lands in the raw report through the conftest hook.
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from .conftest import esrijson_dsn
from .geoservices_helpers import SEEDED_NAMES


@pytest.mark.integration
@pytest.mark.gdal
class TestFeatureServerExport:
    """ogr2ogr exports the FeatureServer layer to GeoPackage with every feature and its geometry type."""

    def test_exports_to_geopackage(self, featureserver_layer_url, ogr_run, tmp_path: Path):
        target = tmp_path / "featureserver.gpkg"
        ogr_run(["ogr2ogr", "-f", "GPKG", str(target), esrijson_dsn(featureserver_layer_url), "-nln", "points"]) \
            .assert_success("ogr2ogr could not export the FeatureServer layer to GeoPackage")
        layer = json.loads(ogr_run(["ogrinfo", "-json", "-so", str(target), "points"]).assert_success().stdout)["layers"][0]

        assert layer["featureCount"] == len(SEEDED_NAMES)
        assert layer["geometryFields"][0]["type"] == "Point"

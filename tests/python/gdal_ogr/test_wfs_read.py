# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
GDAL/OGR interoperability: WFS 2.0 — feature read.
"""

from __future__ import annotations

import json

import pytest
from shapely.geometry import shape

from .conftest import EvidenceCollector, OgrResult


@pytest.mark.integration
@pytest.mark.gdal
class TestWfsRead:
    """Verify that ogr2ogr can read features via the WFS driver."""

    def test_read_geojson(
        self,
        wfs_dsn: str,
        ogr_run,
        wfs_layer_name: str,
        evidence_collector: EvidenceCollector,
    ):
        """ogr2ogr reads WFS features as GeoJSON to stdout."""
        result: OgrResult = ogr_run(
            [
                "ogr2ogr", "-f", "GeoJSON",
                "/vsistdout/", wfs_dsn, wfs_layer_name,
            ],
        )
        result.assert_success("ogr2ogr WFS GeoJSON read failed")

        data = json.loads(result.stdout)
        assert data["type"] == "FeatureCollection"
        assert len(data["features"]) > 0, "WFS read returned no features"
        evidence_collector.record(
            "test_read_geojson", "wfs", "feature_read", "pass",
        )

    def test_features_have_properties(
        self,
        wfs_dsn: str,
        ogr_run,
        wfs_layer_name: str,
    ):
        """Returned WFS features contain expected properties."""
        result: OgrResult = ogr_run(
            [
                "ogr2ogr", "-f", "GeoJSON",
                "/vsistdout/", wfs_dsn, wfs_layer_name,
            ],
        )
        result.assert_success()

        data = json.loads(result.stdout)
        props = data["features"][0]["properties"]
        assert "name" in props, f"Missing 'name' property: {list(props.keys())}"

    def test_geometries_valid(
        self,
        wfs_dsn: str,
        ogr_run,
        wfs_layer_name: str,
    ):
        """Geometries in the WFS GeoJSON output are valid."""
        result: OgrResult = ogr_run(
            [
                "ogr2ogr", "-f", "GeoJSON",
                "/vsistdout/", wfs_dsn, wfs_layer_name,
            ],
        )
        result.assert_success()

        data = json.loads(result.stdout)
        for feature in data["features"]:
            geom_json = feature.get("geometry")
            if geom_json is None:
                continue
            geom = shape(geom_json)
            assert geom.is_valid, f"Invalid geometry: {geom.wkt[:200]}"

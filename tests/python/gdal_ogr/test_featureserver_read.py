# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""GDAL/OGR interoperability: GeoServices FeatureServer, feature read (ESRIJSON driver)."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from .conftest import EvidenceCollector, esrijson_dsn
from .geoservices_helpers import SEEDED_NAMES, feature_names, read_features


@pytest.mark.integration
@pytest.mark.gdal
class TestFeatureServerRead:
    """GDAL reads every seeded feature, with geometry, from a FeatureServer layer."""

    def test_reads_every_seeded_feature(self, featureserver_layer_url, ogr_run, evidence_collector: EvidenceCollector):
        features = read_features(ogr_run, featureserver_layer_url)

        assert sorted(feature_names(features)) == sorted(SEEDED_NAMES)
        assert {feature["geometry"]["type"] for feature in features} == {"Point"}
        evidence_collector.record("test_reads_every_seeded_feature", "featureserver", "read", "pass")

    def test_point_coordinates_survive_the_round_trip(self, featureserver_layer_url, ogr_run):
        features = read_features(ogr_run, featureserver_layer_url, outSR="4326")
        by_name = {feature["properties"]["name"]: feature["geometry"]["coordinates"] for feature in features}

        assert by_name["pt-alpha"] == pytest.approx([-122.4194, 37.7749], abs=1e-6)
        assert by_name["pt-gamma"] == pytest.approx([-122.4210, 37.7735], abs=1e-6)

    @pytest.fixture
    def exported_layer(self, featureserver_layer_url, ogr_run, tmp_path: Path) -> dict:
        target = tmp_path / "featureserver.gpkg"
        ogr_run(["ogr2ogr", "-f", "GPKG", str(target), esrijson_dsn(featureserver_layer_url), "-nln", "points"]) \
            .assert_success("ogr2ogr could not export the FeatureServer layer to GeoPackage")
        info = ogr_run(["ogrinfo", "-json", "-so", str(target), "points"]).assert_success()
        return json.loads(info.stdout)["layers"][0]

    def test_exports_to_geopackage(self, exported_layer, evidence_collector: EvidenceCollector):
        assert exported_layer["featureCount"] == len(SEEDED_NAMES)
        assert exported_layer["geometryFields"][0]["type"] == "Point"
        evidence_collector.record("test_exports_to_geopackage", "featureserver", "export_gpkg", "pass")

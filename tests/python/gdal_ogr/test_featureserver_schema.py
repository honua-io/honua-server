# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""GDAL/OGR interoperability: GeoServices FeatureServer, layer schema.

GDAL's ESRIJSON client opens one layer query; it cannot list services or layers, so this
lane certifies schema (CERT-SCHM-01) for FeatureServer, not discovery (CERT-DISC-01).
"""

from __future__ import annotations

import json

import pytest

from .conftest import EvidenceCollector, esrijson_dsn
from .geoservices_helpers import SEEDED_NAMES


@pytest.mark.integration
@pytest.mark.gdal
class TestFeatureServerSchema:
    """ogrinfo describes the FeatureServer layer: count, geometry type and fields."""

    @pytest.fixture
    def layer_summary(self, featureserver_layer_url, ogr_run) -> dict:
        result = ogr_run(["ogrinfo", "-json", "-so", "-al", esrijson_dsn(featureserver_layer_url)])
        result.assert_success("ogrinfo could not open the FeatureServer layer through ESRIJSON")
        return json.loads(result.stdout)["layers"][0]

    def test_reports_count_and_geometry_type(self, layer_summary, evidence_collector: EvidenceCollector):
        assert layer_summary["featureCount"] == len(SEEDED_NAMES)
        assert layer_summary["geometryFields"][0]["type"] == "Point"
        evidence_collector.record("test_reports_count_and_geometry_type", "featureserver", "schema", "pass")

    def test_schema_carries_the_seeded_fields(self, layer_summary, evidence_collector: EvidenceCollector):
        fields = {field["name"]: field["type"] for field in layer_summary["fields"]}

        assert fields.get("name") == "String", f"unexpected schema: {fields}"
        evidence_collector.record("test_schema_carries_the_seeded_fields", "featureserver", "schema_introspection", "pass")

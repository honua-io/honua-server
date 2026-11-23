# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""GDAL/OGR interoperability: GeoServices FeatureServer, server-side attribute and spatial filters."""

from __future__ import annotations

import pytest

from .conftest import EvidenceCollector
from .geoservices_helpers import feature_names, read_features

# Around pt-gamma (-122.4210 37.7735) only; pt-alpha and pt-beta lie outside.
GAMMA_ENVELOPE = "-122.4215,37.7730,-122.4205,37.7740"


@pytest.mark.integration
@pytest.mark.gdal
class TestFeatureServerQuery:
    """The server applies where and geometry filters that GDAL passes through the query URL."""

    def test_where_clause_selects_one_feature(self, featureserver_layer_url, ogr_run, evidence_collector: EvidenceCollector):
        features = read_features(ogr_run, featureserver_layer_url, where="name = 'pt-beta'")

        assert feature_names(features) == ["pt-beta"]
        evidence_collector.record("test_where_clause_selects_one_feature", "featureserver", "attribute_query", "pass")

    def test_envelope_selects_one_feature(self, featureserver_layer_url, ogr_run, evidence_collector: EvidenceCollector):
        features = read_features(
            ogr_run, featureserver_layer_url,
            geometry=GAMMA_ENVELOPE, geometryType="esriGeometryEnvelope",
            spatialRel="esriSpatialRelIntersects", inSR="4326",
        )

        assert feature_names(features) == ["pt-gamma"]
        evidence_collector.record("test_envelope_selects_one_feature", "featureserver", "spatial_query", "pass")

    def test_where_clause_matching_nothing_is_empty(self, featureserver_layer_url, ogr_run):
        assert read_features(ogr_run, featureserver_layer_url, where="name = 'no-such-point'") == []

# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""GDAL/OGR interoperability: GeoServices FeatureServer, feature read (ESRIJSON driver)."""

from __future__ import annotations

import pytest

from .conftest import EvidenceCollector
from .geoservices_helpers import SEEDED_NAMES, feature_names, read_features


@pytest.mark.integration
@pytest.mark.gdal
class TestFeatureServerRead:
    """GDAL reads every seeded feature from a FeatureServer layer."""

    def test_reads_every_seeded_feature(self, featureserver_layer_url, ogr_run, evidence_collector: EvidenceCollector):
        features = read_features(ogr_run, featureserver_layer_url)

        assert sorted(feature_names(features)) == sorted(SEEDED_NAMES)
        assert {feature["geometry"]["type"] for feature in features} == {"Point"}
        evidence_collector.record("test_reads_every_seeded_feature", "featureserver", "read", "pass")

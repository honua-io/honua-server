# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""GDAL/OGR interoperability: GeoServices FeatureServer, result paging."""

from __future__ import annotations

import pytest

from .conftest import EvidenceCollector
from .geoservices_helpers import SEEDED_NAMES, feature_names, read_features


@pytest.mark.integration
@pytest.mark.gdal
class TestFeatureServerPaging:
    """GDAL pages with resultOffset while the server reports exceededTransferLimit."""

    def test_one_record_pages_reach_every_feature(self, featureserver_layer_url, ogr_run, evidence_collector: EvidenceCollector):
        names = feature_names(read_features(ogr_run, featureserver_layer_url, resultRecordCount="1", orderByFields="name"))

        assert names == sorted(SEEDED_NAMES), "GDAL did not page through every record, or a page repeated one"
        evidence_collector.record("test_one_record_pages_reach_every_feature", "featureserver", "paging", "pass")

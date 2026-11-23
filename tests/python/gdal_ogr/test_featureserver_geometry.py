# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""GDAL/OGR interoperability: GeoServices FeatureServer, geometry fidelity."""

from __future__ import annotations

import pytest

from .conftest import EvidenceCollector
from .geoservices_helpers import read_features

SEEDED_POINTS = {"pt-alpha": [-122.4194, 37.7749], "pt-beta": [-122.4180, 37.7760], "pt-gamma": [-122.4210, 37.7735]}


@pytest.mark.integration
@pytest.mark.gdal
class TestFeatureServerGeometry:
    """Point coordinates survive the server's JSON and GDAL's ESRIJSON reader unchanged."""

    def test_seeded_coordinates_round_trip(self, featureserver_layer_url, ogr_run, evidence_collector: EvidenceCollector):
        features = read_features(ogr_run, featureserver_layer_url, outSR="4326")
        by_name = {feature["properties"]["name"]: feature["geometry"]["coordinates"] for feature in features}

        assert set(by_name) == set(SEEDED_POINTS)
        max_delta = 0.0
        for name, expected in SEEDED_POINTS.items():
            assert by_name[name] == pytest.approx(expected, rel=0, abs=1e-6), name
            max_delta = max(max_delta, *(abs(actual - target) for actual, target in zip(by_name[name], expected)))
        evidence_collector.record_geometry_delta("test_seeded_coordinates_round_trip", "featureserver", max_delta)

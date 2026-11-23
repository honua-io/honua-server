# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""GDAL/OGR interoperability: GeoServices MapServer export, rendered content."""

from __future__ import annotations

import pytest

from .conftest import EvidenceCollector
from .geoservices_helpers import DATA_WINDOW, EMPTY_WINDOW, band_spread


@pytest.mark.integration
@pytest.mark.gdal
class TestMapServerRender:
    """The export draws the seeded points over their extent and nothing outside the data."""

    def test_points_are_drawn_over_the_data_and_absent_elsewhere(self, mapserver_export, evidence_collector: EvidenceCollector):
        drawn = mapserver_export(DATA_WINDOW)
        empty = mapserver_export(EMPTY_WINDOW)

        assert band_spread(drawn) > 0, "the export over the seeded points is a uniform image"
        assert band_spread(empty) == 0, "the export outside the service extent drew something"
        evidence_collector.record("test_points_are_drawn_over_the_data_and_absent_elsewhere", "mapserver", "render", "pass")

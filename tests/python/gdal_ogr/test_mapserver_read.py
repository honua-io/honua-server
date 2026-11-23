# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""GDAL/OGR interoperability: GeoServices MapServer export, read through GDAL's AGS minidriver."""

from __future__ import annotations

import pytest

from .conftest import EvidenceCollector
from .geoservices_helpers import DATA_WINDOW, IMAGE_SIZE


@pytest.mark.integration
@pytest.mark.gdal
class TestMapServerRead:
    """GDAL fetches a MapServer export as a georeferenced RGBA image of the requested size."""

    def test_export_opens_as_rgba_at_the_requested_size(self, mapserver_export, evidence_collector: EvidenceCollector):
        info = mapserver_export(DATA_WINDOW)

        assert tuple(info["size"]) == IMAGE_SIZE
        assert len(info["bands"]) == 4
        evidence_collector.record("test_export_opens_as_rgba_at_the_requested_size", "mapserver", "read", "pass")

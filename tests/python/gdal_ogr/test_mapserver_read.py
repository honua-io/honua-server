# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""GDAL/OGR interoperability: GeoServices MapServer export, read through GDAL's AGS minidriver."""

from __future__ import annotations

from pathlib import Path

import pytest

from .conftest import EvidenceCollector
from .geoservices_helpers import DATA_WINDOW, IMAGE_SIZE, ags_service_xml, export_image


@pytest.mark.integration
@pytest.mark.gdal
class TestMapServerRead:
    """GDAL fetches a MapServer export as a georeferenced RGBA image of the requested size."""

    def test_export_opens_as_rgba_at_the_requested_size(self, mapserver_url, ogr_run, tmp_path: Path, evidence_collector: EvidenceCollector):
        info = export_image(ogr_run, ags_service_xml(mapserver_url, DATA_WINDOW), tmp_path / "export.png")

        assert tuple(info["size"]) == IMAGE_SIZE
        assert len(info["bands"]) == 4
        evidence_collector.record("test_export_opens_as_rgba_at_the_requested_size", "mapserver", "read", "pass")

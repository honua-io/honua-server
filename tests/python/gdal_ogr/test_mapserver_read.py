# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""GDAL/OGR interoperability: GeoServices MapServer export, read through GDAL's AGS minidriver."""

from __future__ import annotations

import json

import pytest

from .conftest import EvidenceCollector
from .geoservices_helpers import DATA_WINDOW, IMAGE_SIZE, export_url


@pytest.mark.integration
@pytest.mark.gdal
class TestMapServerRead:
    """GDAL opens the server's own MapServer export image at the requested size."""

    def test_export_image_opens_at_the_requested_size(self, mapserver_url, ogr_run, evidence_collector: EvidenceCollector):
        # /vsicurl/ reads the PNG the server returned, so size and bands come from the response.
        result = ogr_run(["gdalinfo", "-json", f"/vsicurl/{export_url(mapserver_url, DATA_WINDOW)}"], timeout=60.0)
        info = json.loads(result.assert_success("GDAL could not open the MapServer export image").stdout)

        assert tuple(info["size"]) == IMAGE_SIZE
        assert len(info["bands"]) in (3, 4), f"unexpected band layout: {len(info['bands'])}"
        evidence_collector.record("test_export_image_opens_at_the_requested_size", "mapserver", "read", "pass")

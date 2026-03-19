# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
GDAL/OGR interoperability: WFS 2.0 — attribute and spatial queries.
"""

from __future__ import annotations

import json

import httpx
import pytest

from .conftest import EvidenceCollector, OgrResult


@pytest.mark.integration
@pytest.mark.gdal
class TestWfsQuery:
    """Verify OGR attribute and spatial queries against the WFS driver."""

    def test_attribute_query(
        self,
        http_client: httpx.Client,
        wfs_dsn: str,
        ogr_run,
        wfs_layer_name: str,
        evidence_collector: EvidenceCollector,
    ):
        """ogr2ogr -where filters WFS features by attribute."""
        filter_xml = """
<fes:Filter xmlns:fes="http://www.opengis.net/fes/2.0">
  <fes:PropertyIsEqualTo>
    <fes:ValueReference>name</fes:ValueReference>
    <fes:Literal>alpha</fes:Literal>
  </fes:PropertyIsEqualTo>
</fes:Filter>
""".strip()
        server_response = http_client.get(
            "/wfs",
            params={
                "SERVICE": "WFS",
                "REQUEST": "GetFeature",
                "VERSION": "2.0.0",
                "TYPENAMES": wfs_layer_name,
                "OUTPUTFORMAT": "application/geo+json",
                "FILTER": filter_xml,
            },
        )
        assert server_response.status_code == 200, server_response.text

        server_data = server_response.json()
        server_features = server_data["features"]
        assert len(server_features) >= 1, "Server-side WFS filter returned no features"
        server_names = {f["properties"]["name"] for f in server_features}
        assert server_names == {"alpha"}, (
            f"Expected server-side WFS filter to return only 'alpha', got {server_names}"
        )

        result: OgrResult = ogr_run(
            [
                "ogr2ogr", "-f", "GeoJSON",
                "/vsistdout/", wfs_dsn, wfs_layer_name,
                "-where", "name = 'alpha'",
            ],
        )
        result.assert_success("WFS attribute query failed")

        data = json.loads(result.stdout)
        features = data["features"]
        assert len(features) >= 1, "WFS attribute query returned no features"
        names = {f["properties"]["name"] for f in features}
        assert "alpha" in names, f"Expected 'alpha' in WFS results, got {names}"
        evidence_collector.record(
            "test_attribute_query", "wfs", "attribute_query", "pass",
        )

    def test_spatial_query_bbox(
        self,
        wfs_dsn: str,
        ogr_run,
        wfs_layer_name: str,
        evidence_collector: EvidenceCollector,
    ):
        """ogr2ogr -spat filters WFS features by bounding box."""
        # Broad bbox covering San Francisco test data area
        result: OgrResult = ogr_run(
            [
                "ogr2ogr", "-f", "GeoJSON",
                "/vsistdout/", wfs_dsn, wfs_layer_name,
                "-spat", "-123.0", "37.0", "-122.0", "38.5",
            ],
        )
        result.assert_success("WFS spatial bbox query failed")

        data = json.loads(result.stdout)
        assert len(data["features"]) > 0, "WFS spatial query returned no features"
        evidence_collector.record(
            "test_spatial_query_bbox", "wfs", "spatial_query", "pass",
        )

    def test_spatial_query_empty_bbox(
        self,
        wfs_dsn: str,
        ogr_run,
        wfs_layer_name: str,
    ):
        """ogr2ogr -spat with bbox outside data area returns zero features."""
        result: OgrResult = ogr_run(
            [
                "ogr2ogr", "-f", "GeoJSON",
                "/vsistdout/", wfs_dsn, wfs_layer_name,
                "-spat", "0.0", "0.0", "1.0", "1.0",
            ],
        )
        result.assert_success("WFS spatial query with empty bbox failed")

        data = json.loads(result.stdout)
        assert len(data["features"]) == 0, (
            f"Expected 0 features for distant bbox, got {len(data['features'])}"
        )

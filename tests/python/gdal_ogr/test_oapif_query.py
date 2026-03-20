# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
GDAL/OGR interoperability: OGC API Features — attribute and spatial queries.
"""

from __future__ import annotations

import json

import httpx
import pytest

from .conftest import EvidenceCollector, OgrResult


@pytest.mark.integration
@pytest.mark.gdal
class TestOapifQuery:
    """Verify OGR attribute and spatial queries against the OAPIF driver."""

    def test_attribute_query(
        self,
        http_client: httpx.Client,
        oapif_dsn: str,
        ogr_run,
        test_collection_id: str,
        evidence_collector: EvidenceCollector,
    ):
        """ogr2ogr -where filters features by attribute."""
        server_response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={
                "filter": "name = 'alpha'",
                "filter-lang": "cql2-text",
            },
        )
        assert server_response.status_code == 200, server_response.text

        server_data = server_response.json()
        server_features = server_data["features"]
        assert len(server_features) >= 1, "Server-side OAPIF filter returned no features"
        server_names = {f["properties"]["name"] for f in server_features}
        assert server_names == {"alpha"}, (
            f"Expected server-side OAPIF filter to return only 'alpha', got {server_names}"
        )

        result: OgrResult = ogr_run(
            [
                "ogr2ogr", "-f", "GeoJSON",
                "/vsistdout/", oapif_dsn, test_collection_id,
                "-where", "name = 'alpha'",
            ],
        )
        result.assert_success("attribute query failed")

        data = json.loads(result.stdout)
        features = data["features"]
        assert len(features) >= 1, "Attribute query returned no features"
        names = {f["properties"]["name"] for f in features}
        assert "alpha" in names, f"Expected 'alpha' in results, got {names}"
        evidence_collector.record(
            "test_attribute_query", "oapif", "attribute_query", "pass",
        )

    def test_spatial_query_bbox(
        self,
        oapif_dsn: str,
        ogr_run,
        test_collection_id: str,
        evidence_collector: EvidenceCollector,
    ):
        """ogr2ogr -spat filters features by bounding box."""
        # Broad bbox covering San Francisco test data area
        result: OgrResult = ogr_run(
            [
                "ogr2ogr", "-f", "GeoJSON",
                "/vsistdout/", oapif_dsn, test_collection_id,
                "-spat", "-123.0", "37.0", "-122.0", "38.5",
            ],
        )
        result.assert_success("spatial bbox query failed")

        data = json.loads(result.stdout)
        assert len(data["features"]) > 0, "Spatial query returned no features"
        evidence_collector.record(
            "test_spatial_query_bbox", "oapif", "spatial_query", "pass",
        )

    def test_spatial_query_empty_bbox(
        self,
        oapif_dsn: str,
        ogr_run,
        test_collection_id: str,
    ):
        """ogr2ogr -spat with bbox outside data area returns zero features."""
        # Bbox in the Gulf of Guinea — no test data here
        result: OgrResult = ogr_run(
            [
                "ogr2ogr", "-f", "GeoJSON",
                "/vsistdout/", oapif_dsn, test_collection_id,
                "-spat", "0.0", "0.0", "1.0", "1.0",
            ],
        )
        result.assert_success("spatial query with empty bbox failed")

        data = json.loads(result.stdout)
        assert len(data["features"]) == 0, (
            f"Expected 0 features for distant bbox, got {len(data['features'])}"
        )

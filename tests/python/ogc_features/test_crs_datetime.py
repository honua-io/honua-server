# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Tests for CRS and datetime handling on OGC API Features items.
"""

import pytest
import httpx


CRS84 = "http://www.opengis.net/def/crs/OGC/1.3/CRS84"
EPSG4326 = "http://www.opengis.net/def/crs/EPSG/0/4326"


class TestCrsAndDatetime:
    """Tests for CRS and datetime parameters."""

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_crs_header(self, http_client: httpx.Client, test_collection_id: str):
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"crs": EPSG4326},
        )
        assert response.status_code == 200
        content_crs = response.headers.get("content-crs")
        assert content_crs is not None
        assert EPSG4326 in content_crs

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_crs_invalid(self, http_client: httpx.Client, test_collection_id: str):
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"crs": "http://example.com/crs/invalid"},
        )
        assert response.status_code == 400

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_datetime_instant(self, http_client: httpx.Client, test_collection_id: str):
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"datetime": "2024-01-10T12:00:00Z"},
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_datetime_interval(self, http_client: httpx.Client, test_collection_id: str):
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"datetime": "2024-01-01T00:00:00Z/2024-01-31T23:59:59Z"},
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_datetime_invalid(self, http_client: httpx.Client, test_collection_id: str):
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"datetime": "not-a-date"},
        )
        assert response.status_code == 400

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_filter_crs_requires_filter(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"filter-crs": CRS84},
        )
        assert response.status_code == 400

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_filter_crs_epsg4326(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        # EPSG:4326 uses north/east axis order; supply lat/lon
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={
                "filter": "S_INTERSECTS(shape, POINT(37.7749 -122.4194))",
                "filter-crs": EPSG4326,
            },
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_bbox_crs_epsg4326(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        # EPSG:4326 uses north/east axis order; bbox is minLat,minLon,maxLat,maxLon
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={
                "bbox": "37.7,-122.5,37.8,-122.4",
                "bbox-crs": EPSG4326,
            },
        )
        assert response.status_code == 200

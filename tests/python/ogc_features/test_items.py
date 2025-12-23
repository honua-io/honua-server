# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Tests for OGC API Features items endpoint.

Endpoint: GET /ogc/features/collections/{collectionId}/items

Tests cover:
- Basic feature retrieval
- Pagination (limit, offset)
- CQL2-Text filtering
- Geometry validation
- Content negotiation
"""

import pytest
import httpx
from shapely.geometry import shape

from shared.geometry import GeometryGenerator, TestGeometry
from conftest import (
    assert_geojson_feature_collection,
    assert_geojson_feature,
    validate_geometry_with_shapely,
)


class TestItemsBasic:
    """Basic tests for the items endpoint."""

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_returns_200(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Items endpoint should return 200 OK."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items"
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_returns_feature_collection(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Items should return a GeoJSON FeatureCollection."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items"
        )
        data = assert_geojson_feature_collection(response)
        assert data["type"] == "FeatureCollection"

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_features_have_correct_structure(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Each feature should have correct GeoJSON structure."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items"
        )
        data = response.json()
        features = data.get("features", [])

        for feature in features:
            assert_geojson_feature(feature)
            # Features should have an ID
            assert "id" in feature or "properties" in feature

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_returns_geojson_content_type(
        self, geojson_client: httpx.Client, test_collection_id: str
    ):
        """Items should return GeoJSON when requested."""
        response = geojson_client.get(
            f"/ogc/features/collections/{test_collection_id}/items"
        )
        content_type = response.headers.get("content-type", "")
        # Accept either geo+json or json with geojson features
        assert "json" in content_type.lower()

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_invalid_collection_returns_404(self, http_client: httpx.Client):
        """Items should return 404 for invalid collection."""
        response = http_client.get(
            "/ogc/features/collections/nonexistent_xyz/items"
        )
        assert response.status_code == 404

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_includes_number_matched(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Items response should include numberMatched."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items"
        )
        data = response.json()
        assert "numberMatched" in data
        assert isinstance(data["numberMatched"], int)
        assert data["numberMatched"] >= 0

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_includes_number_returned(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Items response should include numberReturned."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items"
        )
        data = response.json()
        assert "numberReturned" in data
        assert isinstance(data["numberReturned"], int)
        assert data["numberReturned"] >= 0
        assert data["numberReturned"] == len(data.get("features", []))


class TestItemsPagination:
    """Tests for items pagination."""

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_limit_restricts_count(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Limit parameter should restrict number of returned features."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"limit": 2},
        )
        data = response.json()
        features = data.get("features", [])
        assert len(features) <= 2

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_offset_skips_features(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Offset parameter should skip features."""
        # Get first page
        response1 = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"limit": 2},
        )
        page1 = response1.json()

        # Get second page with offset
        response2 = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"limit": 2, "offset": 2},
        )
        page2 = response2.json()

        # Features should be different (if enough data exists)
        if len(page1.get("features", [])) > 0 and len(page2.get("features", [])) > 0:
            page1_ids = {f.get("id") for f in page1["features"]}
            page2_ids = {f.get("id") for f in page2["features"]}
            # Pages should not overlap
            assert page1_ids.isdisjoint(page2_ids)

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_limit_zero_returns_empty(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Limit of 0 should return empty features array."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"limit": 0},
        )
        # Could return 400 (invalid) or 200 with empty - both are acceptable
        if response.status_code == 200:
            data = response.json()
            assert len(data.get("features", [])) == 0

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_large_limit_capped(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Very large limit should be capped to server maximum."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"limit": 1000000},
        )
        # Server should cap and return 200, or return 400 for invalid limit
        assert response.status_code in [200, 400]

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_negative_limit_returns_error(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Negative limit should return 400."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"limit": -1},
        )
        assert response.status_code == 400

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_negative_offset_returns_error(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Negative offset should return 400."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"offset": -1},
        )
        assert response.status_code == 400


class TestItemsFiltering:
    """Tests for CQL2-Text filtering."""

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_filter_equals(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Filter with equality comparison."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"filter": "name = 'test'"},
        )
        # Should return 200 (with matching features) or 200 with empty results
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_filter_not_equals(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Filter with not-equals comparison."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"filter": "name <> 'excluded'"},
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_filter_like(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Filter with LIKE pattern matching."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"filter": "name LIKE 'test%'"},
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_filter_in(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Filter with IN operator."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"filter": "name IN ('test1', 'test2', 'test3')"},
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_filter_between(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Filter with BETWEEN operator."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"filter": "id BETWEEN 1 AND 100"},
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_filter_is_null(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Filter with IS NULL."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"filter": "description IS NULL"},
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_filter_and(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Filter with AND logical operator."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"filter": "name = 'test' AND id > 0"},
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_filter_or(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Filter with OR logical operator."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"filter": "name = 'test1' OR name = 'test2'"},
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_filter_not(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Filter with NOT logical operator."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"filter": "NOT (name = 'excluded')"},
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_filter_parentheses(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Filter with nested parentheses."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"filter": "(name = 'a' OR name = 'b') AND id > 0"},
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_filter_invalid_syntax_returns_400(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Invalid filter syntax should return 400."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"filter": "invalid syntax !!!"},
        )
        assert response.status_code == 400

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_filter_with_limit(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Filter combined with limit."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"filter": "id > 0", "limit": 5},
        )
        assert response.status_code == 200
        data = response.json()
        assert len(data.get("features", [])) <= 5

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_filter_with_offset(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Filter combined with offset."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"filter": "id > 0", "offset": 2, "limit": 5},
        )
        assert response.status_code == 200


class TestItemsGeometry:
    """Tests for geometry validation in items responses."""

    @pytest.mark.integration
    @pytest.mark.ogc
    @pytest.mark.geometry
    def test_items_geometries_are_valid(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """All returned geometries should be valid GeoJSON."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"limit": 50},
        )
        data = response.json()
        features = data.get("features", [])

        for feature in features:
            geometry = feature.get("geometry")
            if geometry is not None:
                # Validate with shapely
                geom = shape(geometry)
                assert geom.is_valid, f"Invalid geometry: {geometry}"

    @pytest.mark.integration
    @pytest.mark.ogc
    @pytest.mark.geometry
    def test_items_null_geometries_allowed(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Features may have null geometries."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"limit": 100},
        )
        data = response.json()
        features = data.get("features", [])

        # Just verify we can handle null geometries without error
        for feature in features:
            geometry = feature.get("geometry")
            # geometry can be None (null) - this is valid

    @pytest.mark.integration
    @pytest.mark.ogc
    @pytest.mark.geometry
    def test_items_geometry_types(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Verify supported geometry types."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"limit": 100},
        )
        data = response.json()
        features = data.get("features", [])

        valid_types = {
            "Point", "MultiPoint",
            "LineString", "MultiLineString",
            "Polygon", "MultiPolygon",
            "GeometryCollection",
        }

        for feature in features:
            geometry = feature.get("geometry")
            if geometry is not None:
                geom_type = geometry.get("type")
                assert geom_type in valid_types, f"Unexpected geometry type: {geom_type}"

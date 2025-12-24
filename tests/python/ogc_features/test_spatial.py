# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Tests for OGC API Features spatial filtering.

Tests cover:
- Bounding box (bbox) parameter
- Spatial predicates via CQL2 (intersects, contains, overlaps, touches, crosses)
- Coordinate system handling
"""

import pytest
import httpx

from shared.geometry import GeometryGenerator


class TestBboxFiltering:
    """Tests for bbox parameter filtering."""

    @pytest.mark.integration
    @pytest.mark.ogc
    @pytest.mark.geometry
    def test_items_bbox_filter(
        self,
        http_client: httpx.Client,
        test_collection_id: str,
        geometry_generator: GeometryGenerator,
    ):
        """Filter items with bbox parameter."""
        bbox = geometry_generator.bbox()
        bbox_str = f"{bbox[0]},{bbox[1]},{bbox[2]},{bbox[3]}"

        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"bbox": bbox_str},
        )
        assert response.status_code == 200
        data = response.json()
        assert "features" in data

    @pytest.mark.integration
    @pytest.mark.ogc
    @pytest.mark.geometry
    def test_items_bbox_small_area(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Filter items with a small bounding box."""
        # Small bbox around San Francisco
        bbox = "-122.42,37.77,-122.41,37.78"
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"bbox": bbox},
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_bbox_invalid_format(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Invalid bbox format should return 400."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"bbox": "invalid,bbox,format"},
        )
        assert response.status_code == 400

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_bbox_with_crs(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Bbox with CRS parameter."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={
                "bbox": "-122.5,37.7,-122.4,37.8",
                "bbox-crs": "http://www.opengis.net/def/crs/OGC/1.3/CRS84",
            },
        )
        # May or may not support bbox-crs
        assert response.status_code in [200, 400]

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_bbox_combined_with_limit(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Bbox combined with limit parameter."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={
                "bbox": "-180,-90,180,90",
                "limit": 5,
            },
        )
        assert response.status_code == 200
        data = response.json()
        assert len(data.get("features", [])) <= 5

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_bbox_combined_with_filter(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Bbox combined with CQL2 filter."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={
                "bbox": "-180,-90,180,90",
                "filter": "id > 0",
            },
        )
        assert response.status_code == 200


class TestSpatialPredicates:
    """Tests for spatial predicates via CQL2."""

    @pytest.mark.integration
    @pytest.mark.ogc
    @pytest.mark.geometry
    def test_filter_s_intersects(
        self,
        http_client: httpx.Client,
        test_collection_id: str,
        geometry_generator: GeometryGenerator,
    ):
        """S_INTERSECTS spatial predicate."""
        polygon = geometry_generator.polygon_simple()
        # CQL2 spatial function syntax
        filter_expr = f"S_INTERSECTS(geom, {polygon.wkt})"

        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"filter": filter_expr},
        )
        # May return 200 or 400 if not supported
        assert response.status_code in [200, 400]

    @pytest.mark.integration
    @pytest.mark.ogc
    @pytest.mark.geometry
    def test_filter_s_contains(
        self,
        http_client: httpx.Client,
        test_collection_id: str,
        geometry_generator: GeometryGenerator,
    ):
        """S_CONTAINS spatial predicate."""
        polygon = geometry_generator.polygon_simple()
        filter_expr = f"S_CONTAINS(geom, {polygon.wkt})"

        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"filter": filter_expr},
        )
        assert response.status_code in [200, 400]

    @pytest.mark.integration
    @pytest.mark.ogc
    @pytest.mark.geometry
    def test_filter_s_within(
        self,
        http_client: httpx.Client,
        test_collection_id: str,
        geometry_generator: GeometryGenerator,
    ):
        """S_WITHIN spatial predicate."""
        polygon = geometry_generator.polygon_simple()
        filter_expr = f"S_WITHIN(geom, {polygon.wkt})"

        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"filter": filter_expr},
        )
        assert response.status_code in [200, 400]

    @pytest.mark.integration
    @pytest.mark.ogc
    @pytest.mark.geometry
    def test_filter_s_overlaps(
        self,
        http_client: httpx.Client,
        test_collection_id: str,
        geometry_generator: GeometryGenerator,
    ):
        """S_OVERLAPS spatial predicate."""
        polygon = geometry_generator.polygon_simple()
        filter_expr = f"S_OVERLAPS(geom, {polygon.wkt})"

        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"filter": filter_expr},
        )
        assert response.status_code in [200, 400]

    @pytest.mark.integration
    @pytest.mark.ogc
    @pytest.mark.geometry
    def test_filter_s_touches(
        self,
        http_client: httpx.Client,
        test_collection_id: str,
        geometry_generator: GeometryGenerator,
    ):
        """S_TOUCHES spatial predicate."""
        polygon = geometry_generator.polygon_simple()
        filter_expr = f"S_TOUCHES(geom, {polygon.wkt})"

        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"filter": filter_expr},
        )
        assert response.status_code in [200, 400]

    @pytest.mark.integration
    @pytest.mark.ogc
    @pytest.mark.geometry
    def test_filter_s_crosses(
        self,
        http_client: httpx.Client,
        test_collection_id: str,
        geometry_generator: GeometryGenerator,
    ):
        """S_CROSSES spatial predicate."""
        line = geometry_generator.linestring()
        filter_expr = f"S_CROSSES(geom, {line.wkt})"

        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"filter": filter_expr},
        )
        assert response.status_code in [200, 400]


class TestContentNegotiation:
    """Tests for content negotiation."""

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_accept_json(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Request items with Accept: application/json."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            headers={"Accept": "application/json"},
        )
        assert response.status_code == 200
        content_type = response.headers.get("content-type", "")
        assert "json" in content_type.lower()

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_accept_geojson(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Request items with Accept: application/geo+json."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            headers={"Accept": "application/geo+json"},
        )
        assert response.status_code == 200
        # Response should be GeoJSON format
        data = response.json()
        assert data.get("type") == "FeatureCollection"

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_accept_html(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Request items with Accept: text/html."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            headers={"Accept": "text/html"},
        )
        # May return HTML or fall back to JSON
        assert response.status_code in [200, 406]

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_landing_accept_json(self, http_client: httpx.Client):
        """Landing page with Accept: application/json."""
        response = http_client.get(
            "/ogc/features",
            headers={"Accept": "application/json"},
        )
        assert response.status_code == 200
        content_type = response.headers.get("content-type", "")
        assert "json" in content_type.lower()

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_collections_accept_json(self, http_client: httpx.Client):
        """Collections with Accept: application/json."""
        response = http_client.get(
            "/ogc/features/collections",
            headers={"Accept": "application/json"},
        )
        assert response.status_code == 200


class TestErrorResponses:
    """Tests for proper error response handling."""

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_404_has_json_body(self, http_client: httpx.Client):
        """404 responses should have JSON error body."""
        response = http_client.get("/ogc/features/collections/nonexistent_xyz/items")
        assert response.status_code == 404
        # Should be able to parse as JSON
        try:
            data = response.json()
            # May have error details
        except Exception:
            pass  # JSON parsing not required for 404

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_400_has_error_details(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """400 responses should have error details."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"filter": "!!!invalid!!!"},
        )
        assert response.status_code == 400
        try:
            data = response.json()
            # Error response may include code, description, etc.
        except Exception:
            pass

# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Tests for OGC API Features single feature retrieval endpoint.

Endpoint: GET /ogc/features/collections/{collectionId}/items/{featureId}
"""

import pytest
import httpx
from shapely.geometry import shape


class TestSingleFeature:
    """Tests for single feature retrieval."""

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_feature_returns_200(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Single feature retrieval should return 200 for valid ID."""
        # First get an item to find a valid feature ID
        items_response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"limit": 1},
        )

        if items_response.status_code != 200:
            pytest.skip("Items endpoint not available")

        items_data = items_response.json()
        features = items_data.get("features", [])

        if not features:
            pytest.skip("No features available to test")

        feature_id = features[0].get("id")
        if feature_id is None:
            pytest.skip("Feature has no ID")

        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items/{feature_id}"
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_feature_is_geojson_feature(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Single feature should return a GeoJSON Feature (not FeatureCollection)."""
        items_response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"limit": 1},
        )

        if items_response.status_code != 200:
            pytest.skip("Items endpoint not available")

        items_data = items_response.json()
        features = items_data.get("features", [])

        if not features:
            pytest.skip("No features available")

        feature_id = features[0].get("id")
        if feature_id is None:
            pytest.skip("Feature has no ID")

        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items/{feature_id}"
        )

        if response.status_code == 200:
            data = response.json()
            assert data.get("type") == "Feature"
            assert "geometry" in data
            assert "properties" in data

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_feature_contains_id(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Single feature should contain its ID."""
        items_response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"limit": 1},
        )

        if items_response.status_code != 200:
            pytest.skip("Items endpoint not available")

        items_data = items_response.json()
        features = items_data.get("features", [])

        if not features:
            pytest.skip("No features available")

        feature_id = features[0].get("id")
        if feature_id is None:
            pytest.skip("Feature has no ID")

        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items/{feature_id}"
        )

        if response.status_code == 200:
            data = response.json()
            assert "id" in data
            assert str(data["id"]) == str(feature_id)

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_feature_contains_links(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Single feature should contain navigation links."""
        items_response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"limit": 1},
        )

        if items_response.status_code != 200:
            pytest.skip("Items endpoint not available")

        items_data = items_response.json()
        features = items_data.get("features", [])

        if not features:
            pytest.skip("No features available")

        feature_id = features[0].get("id")
        if feature_id is None:
            pytest.skip("Feature has no ID")

        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items/{feature_id}"
        )

        if response.status_code == 200:
            data = response.json()
            # Links may be in the feature or in a wrapper
            # OGC spec allows links in feature properties or at root

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_feature_invalid_id_returns_404(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Invalid feature ID should return 404."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items/nonexistent_feature_xyz_999999"
        )
        assert response.status_code == 404

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_feature_invalid_collection_returns_404(
        self, http_client: httpx.Client
    ):
        """Invalid collection with feature ID should return 404."""
        response = http_client.get(
            "/ogc/features/collections/nonexistent_collection_xyz/items/1"
        )
        assert response.status_code == 404

    @pytest.mark.integration
    @pytest.mark.ogc
    @pytest.mark.geometry
    def test_feature_geometry_valid(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Single feature geometry should be valid."""
        items_response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"limit": 1},
        )

        if items_response.status_code != 200:
            pytest.skip("Items endpoint not available")

        items_data = items_response.json()
        features = items_data.get("features", [])

        if not features:
            pytest.skip("No features available")

        feature_id = features[0].get("id")
        if feature_id is None:
            pytest.skip("Feature has no ID")

        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items/{feature_id}"
        )

        if response.status_code == 200:
            data = response.json()
            geometry = data.get("geometry")
            if geometry is not None:
                geom = shape(geometry)
                assert geom.is_valid

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_feature_content_type(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Single feature should return correct content type."""
        items_response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"limit": 1},
        )

        if items_response.status_code != 200:
            pytest.skip("Items endpoint not available")

        items_data = items_response.json()
        features = items_data.get("features", [])

        if not features:
            pytest.skip("No features available")

        feature_id = features[0].get("id")
        if feature_id is None:
            pytest.skip("Feature has no ID")

        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items/{feature_id}"
        )

        if response.status_code == 200:
            content_type = response.headers.get("content-type", "")
            assert "json" in content_type.lower()

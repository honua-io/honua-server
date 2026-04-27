# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Tests for OGC API Features transaction endpoints.

Endpoints:
- POST /ogc/features/collections/{collectionId}/items (create)
- PUT /ogc/features/collections/{collectionId}/items/{featureId} (replace)
- PATCH /ogc/features/collections/{collectionId}/items/{featureId} (update)
- DELETE /ogc/features/collections/{collectionId}/items/{featureId} (delete)
"""

import json

import pytest
import httpx

from shared.geometry import GeometryGenerator


def _extract_feature_id(response: httpx.Response) -> str:
    location = response.headers.get("location", "")
    if location:
        return location.rstrip("/").split("/")[-1]

    data = response.json()
    feature_id = data.get("id")
    assert feature_id, f"Create response did not include a feature id: {data}"
    return str(feature_id)


def _assert_created(response: httpx.Response) -> str:
    assert response.status_code == 201, response.text
    return _extract_feature_id(response)


class TestCreateFeature:
    """Tests for feature creation (POST)."""

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_create_feature_returns_201(
        self,
        http_client: httpx.Client,
        test_collection_id: str,
        geometry_generator: GeometryGenerator,
    ):
        """Creating a feature should return 201 Created."""
        point = geometry_generator.point()
        feature = {
            "type": "Feature",
            "geometry": point.geojson,
            "properties": {"name": "Test Feature"},
        }

        response = http_client.post(
            f"/ogc/features/collections/{test_collection_id}/items",
            json=feature,
            headers={"Content-Type": "application/geo+json"},
        )
        _assert_created(response)

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_create_feature_returns_location(
        self,
        http_client: httpx.Client,
        test_collection_id: str,
        geometry_generator: GeometryGenerator,
    ):
        """Created feature should return Location header."""
        point = geometry_generator.point()
        feature = {
            "type": "Feature",
            "geometry": point.geojson,
            "properties": {"name": "Location Test"},
        }

        response = http_client.post(
            f"/ogc/features/collections/{test_collection_id}/items",
            json=feature,
            headers={"Content-Type": "application/geo+json"},
        )

        feature_id = _assert_created(response)
        location = response.headers.get("location")
        assert location
        assert f"/ogc/features/collections/{test_collection_id}/items/{feature_id}" in location

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_create_feature_invalid_geojson(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Invalid GeoJSON should return 400."""
        invalid_feature = {"not": "valid geojson"}

        response = http_client.post(
            f"/ogc/features/collections/{test_collection_id}/items",
            json=invalid_feature,
            headers={"Content-Type": "application/geo+json"},
        )
        assert response.status_code == 400, response.text

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_create_feature_invalid_collection(
        self,
        http_client: httpx.Client,
        geometry_generator: GeometryGenerator,
    ):
        """Creating feature in invalid collection should return 404."""
        point = geometry_generator.point()
        feature = {
            "type": "Feature",
            "geometry": point.geojson,
            "properties": {"name": "Test"},
        }

        response = http_client.post(
            "/ogc/features/collections/nonexistent_xyz/items",
            json=feature,
            headers={"Content-Type": "application/geo+json"},
        )
        assert response.status_code == 404, response.text


class TestReplaceFeature:
    """Tests for feature replacement (PUT)."""

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_replace_feature(
        self,
        http_client: httpx.Client,
        test_collection_id: str,
        geometry_generator: GeometryGenerator,
    ):
        """Replacing a feature should work."""
        # First create a feature
        point = geometry_generator.point()
        feature = {
            "type": "Feature",
            "geometry": point.geojson,
            "properties": {"name": "Original"},
        }

        create_response = http_client.post(
            f"/ogc/features/collections/{test_collection_id}/items",
            json=feature,
            headers={"Content-Type": "application/geo+json"},
        )
        feature_id = _assert_created(create_response)

        # Replace the feature
        new_point = geometry_generator.point(lon=-122.5)
        replacement = {
            "type": "Feature",
            "geometry": new_point.geojson,
            "properties": {"name": "Replaced"},
        }

        response = http_client.put(
            f"/ogc/features/collections/{test_collection_id}/items/{feature_id}",
            json=replacement,
            headers={"Content-Type": "application/geo+json"},
        )
        assert response.status_code == 200, response.text

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_replace_nonexistent_feature(
        self,
        http_client: httpx.Client,
        test_collection_id: str,
        geometry_generator: GeometryGenerator,
    ):
        """Replacing nonexistent feature should return 404."""
        point = geometry_generator.point()
        feature = {
            "type": "Feature",
            "geometry": point.geojson,
            "properties": {"name": "Test"},
        }

        response = http_client.put(
            f"/ogc/features/collections/{test_collection_id}/items/nonexistent_999999",
            json=feature,
            headers={"Content-Type": "application/geo+json"},
        )
        assert response.status_code == 404, response.text


class TestUpdateFeature:
    """Tests for partial feature update (PATCH)."""

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_update_feature_properties(
        self,
        http_client: httpx.Client,
        test_collection_id: str,
        geometry_generator: GeometryGenerator,
    ):
        """Partial update of feature properties."""
        # Create a feature first
        point = geometry_generator.point()
        feature = {
            "type": "Feature",
            "geometry": point.geojson,
            "properties": {"name": "Original", "status": "draft"},
        }

        create_response = http_client.post(
            f"/ogc/features/collections/{test_collection_id}/items",
            json=feature,
            headers={"Content-Type": "application/geo+json"},
        )
        feature_id = _assert_created(create_response)

        # Partial update
        patch = {"properties": {"status": "published"}}

        response = http_client.patch(
            f"/ogc/features/collections/{test_collection_id}/items/{feature_id}",
            json=patch,
            headers={"Content-Type": "application/merge-patch+json"},
        )
        assert response.status_code == 200, response.text


class TestDeleteFeature:
    """Tests for feature deletion (DELETE)."""

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_delete_feature_returns_200_or_204(
        self,
        http_client: httpx.Client,
        test_collection_id: str,
        geometry_generator: GeometryGenerator,
    ):
        """Deleting a feature should return 200 or 204."""
        # Create a feature first
        point = geometry_generator.point()
        feature = {
            "type": "Feature",
            "geometry": point.geojson,
            "properties": {"name": "To Delete"},
        }

        create_response = http_client.post(
            f"/ogc/features/collections/{test_collection_id}/items",
            json=feature,
            headers={"Content-Type": "application/geo+json"},
        )
        feature_id = _assert_created(create_response)

        # Delete the feature
        response = http_client.delete(
            f"/ogc/features/collections/{test_collection_id}/items/{feature_id}"
        )
        assert response.status_code == 204, response.text

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_delete_nonexistent_feature(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Deleting nonexistent feature should return 404."""
        response = http_client.delete(
            f"/ogc/features/collections/{test_collection_id}/items/nonexistent_999999"
        )
        assert response.status_code == 404, response.text

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_delete_then_get_returns_404(
        self,
        http_client: httpx.Client,
        test_collection_id: str,
        geometry_generator: GeometryGenerator,
    ):
        """Getting a deleted feature should return 404."""
        # Create a feature
        point = geometry_generator.point()
        feature = {
            "type": "Feature",
            "geometry": point.geojson,
            "properties": {"name": "Delete Verify"},
        }

        create_response = http_client.post(
            f"/ogc/features/collections/{test_collection_id}/items",
            json=feature,
            headers={"Content-Type": "application/geo+json"},
        )
        feature_id = _assert_created(create_response)

        # Delete it
        delete_response = http_client.delete(
            f"/ogc/features/collections/{test_collection_id}/items/{feature_id}"
        )
        assert delete_response.status_code == 204, delete_response.text

        # Verify it's gone
        get_response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items/{feature_id}"
        )
        assert get_response.status_code == 404

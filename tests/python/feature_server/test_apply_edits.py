# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Tests for GeoServices REST applyEdits endpoint.

Endpoint: POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits

Tests cover:
- Add operations (new features)
- Update operations (modify existing features)
- Delete operations (remove features)
- Error handling
"""

import json

import pytest
import httpx

from shared.geometry import GeometryGenerator


class TestApplyEditsAdd:
    """Tests for add operations in applyEdits."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_apply_edits_add_single_feature(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
        geometry_generator: GeometryGenerator,
    ):
        """Add a single feature via applyEdits."""
        point = geometry_generator.point("new_feature")
        esri_point = point.to_esri_json()

        adds = [
            {
                "geometry": esri_point,
                "attributes": {"name": "Test Feature"},
            }
        ]

        response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/applyEdits",
            data={"adds": json.dumps(adds), "f": "json"},
        )
        assert response.status_code == 200
        data = response.json()

        # Response should contain addResults
        assert "addResults" in data
        add_results = data["addResults"]
        assert len(add_results) == 1
        assert add_results[0].get("success") is True
        assert "objectId" in add_results[0]

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_apply_edits_add_multiple_features(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
        geometry_generator: GeometryGenerator,
    ):
        """Add multiple features in a single request."""
        adds = []
        for i in range(3):
            point = geometry_generator.point(f"feature_{i}", lon=-122.4 + i * 0.01)
            adds.append({
                "geometry": point.to_esri_json(),
                "attributes": {"name": f"Feature {i}"},
            })

        response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/applyEdits",
            data={"adds": json.dumps(adds), "f": "json"},
        )
        assert response.status_code == 200
        data = response.json()

        add_results = data.get("addResults", [])
        assert len(add_results) == 3
        for result in add_results:
            assert result.get("success") is True

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_apply_edits_add_without_geometry(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
    ):
        """Add feature without geometry (attributes only)."""
        adds = [
            {"attributes": {"name": "No Geometry Feature"}}
        ]

        response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/applyEdits",
            data={"adds": json.dumps(adds), "f": "json"},
        )
        # May succeed or fail depending on layer configuration
        assert response.status_code in [200, 400]

    @pytest.mark.integration
    @pytest.mark.featureserver
    @pytest.mark.geometry
    def test_apply_edits_add_polygon(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
        geometry_generator: GeometryGenerator,
    ):
        """Add a polygon feature."""
        polygon = geometry_generator.polygon_simple()
        esri_polygon = polygon.to_esri_json()

        adds = [
            {
                "geometry": esri_polygon,
                "attributes": {"name": "Polygon Feature"},
            }
        ]

        response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/applyEdits",
            data={"adds": json.dumps(adds), "f": "json"},
        )
        # May succeed or fail depending on layer geometry type
        assert response.status_code in [200, 400]


class TestApplyEditsUpdate:
    """Tests for update operations in applyEdits."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_apply_edits_update_attributes(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
        geometry_generator: GeometryGenerator,
    ):
        """Update feature attributes via applyEdits."""
        # First, add a feature to update
        point = geometry_generator.point("update_test")
        adds = [
            {
                "geometry": point.to_esri_json(),
                "attributes": {"name": "Original Name"},
            }
        ]

        add_response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/applyEdits",
            data={"adds": json.dumps(adds), "f": "json"},
        )

        if add_response.status_code != 200:
            pytest.skip("Add operation not supported")

        add_data = add_response.json()
        object_id = add_data["addResults"][0].get("objectId")

        if not object_id:
            pytest.skip("ObjectId not returned")

        # Now update the feature
        updates = [
            {
                "attributes": {
                    "OBJECTID": object_id,
                    "name": "Updated Name",
                }
            }
        ]

        update_response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/applyEdits",
            data={"updates": json.dumps(updates), "f": "json"},
        )
        assert update_response.status_code == 200
        update_data = update_response.json()

        update_results = update_data.get("updateResults", [])
        assert len(update_results) == 1
        assert update_results[0].get("success") is True

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_apply_edits_update_geometry(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
        geometry_generator: GeometryGenerator,
    ):
        """Update feature geometry via applyEdits."""
        # First, add a feature
        point1 = geometry_generator.point("geom_update", lon=-122.4)
        adds = [
            {
                "geometry": point1.to_esri_json(),
                "attributes": {"name": "Geometry Update Test"},
            }
        ]

        add_response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/applyEdits",
            data={"adds": json.dumps(adds), "f": "json"},
        )

        if add_response.status_code != 200:
            pytest.skip("Add operation not supported")

        add_data = add_response.json()
        object_id = add_data["addResults"][0].get("objectId")

        if not object_id:
            pytest.skip("ObjectId not returned")

        # Update the geometry
        point2 = geometry_generator.point("new_location", lon=-122.5)
        updates = [
            {
                "geometry": point2.to_esri_json(),
                "attributes": {"OBJECTID": object_id},
            }
        ]

        update_response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/applyEdits",
            data={"updates": json.dumps(updates), "f": "json"},
        )
        assert update_response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_apply_edits_update_nonexistent_returns_error(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
    ):
        """Update nonexistent feature should return error."""
        updates = [
            {
                "attributes": {
                    "OBJECTID": 999999999,
                    "name": "Should Fail",
                }
            }
        ]

        response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/applyEdits",
            data={"updates": json.dumps(updates), "f": "json"},
        )
        # Should return 200 with error in results, or 400
        if response.status_code == 200:
            data = response.json()
            update_results = data.get("updateResults", [])
            if update_results:
                # Result should indicate failure
                assert update_results[0].get("success") is False


class TestApplyEditsDelete:
    """Tests for delete operations in applyEdits."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_apply_edits_delete_single(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
        geometry_generator: GeometryGenerator,
    ):
        """Delete a single feature via applyEdits."""
        # First, add a feature to delete
        point = geometry_generator.point("delete_test")
        adds = [
            {
                "geometry": point.to_esri_json(),
                "attributes": {"name": "To Be Deleted"},
            }
        ]

        add_response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/applyEdits",
            data={"adds": json.dumps(adds), "f": "json"},
        )

        if add_response.status_code != 200:
            pytest.skip("Add operation not supported")

        add_data = add_response.json()
        object_id = add_data["addResults"][0].get("objectId")

        if not object_id:
            pytest.skip("ObjectId not returned")

        # Delete the feature
        response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/applyEdits",
            data={"deletes": json.dumps([object_id]), "f": "json"},
        )
        assert response.status_code == 200
        data = response.json()

        delete_results = data.get("deleteResults", [])
        assert len(delete_results) == 1
        assert delete_results[0].get("success") is True

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_apply_edits_delete_multiple(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
        geometry_generator: GeometryGenerator,
    ):
        """Delete multiple features in a single request."""
        # Add features to delete
        adds = []
        for i in range(3):
            point = geometry_generator.point(f"delete_{i}", lon=-122.4 + i * 0.01)
            adds.append({
                "geometry": point.to_esri_json(),
                "attributes": {"name": f"Delete Me {i}"},
            })

        add_response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/applyEdits",
            data={"adds": json.dumps(adds), "f": "json"},
        )

        if add_response.status_code != 200:
            pytest.skip("Add operation not supported")

        add_data = add_response.json()
        object_ids = [r.get("objectId") for r in add_data.get("addResults", [])]
        object_ids = [oid for oid in object_ids if oid is not None]

        if len(object_ids) < 3:
            pytest.skip("Not all adds succeeded")

        # Delete all features
        response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/applyEdits",
            data={"deletes": json.dumps(object_ids), "f": "json"},
        )
        assert response.status_code == 200
        data = response.json()

        delete_results = data.get("deleteResults", [])
        assert len(delete_results) == 3

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_apply_edits_delete_nonexistent(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
    ):
        """Delete nonexistent feature should handle gracefully."""
        response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/applyEdits",
            data={"deletes": json.dumps([999999999]), "f": "json"},
        )
        # Should return 200 with error in results, or handle gracefully
        if response.status_code == 200:
            data = response.json()
            delete_results = data.get("deleteResults", [])
            if delete_results:
                # May indicate failure or succeed silently
                pass


class TestApplyEditsCombined:
    """Tests for combined add/update/delete operations."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_apply_edits_combined_operations(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
        geometry_generator: GeometryGenerator,
    ):
        """Perform add, update, and delete in a single request."""
        # First create features to work with
        point1 = geometry_generator.point("update_target", lon=-122.41)
        point2 = geometry_generator.point("delete_target", lon=-122.42)

        setup_adds = [
            {"geometry": point1.to_esri_json(), "attributes": {"name": "Update Target"}},
            {"geometry": point2.to_esri_json(), "attributes": {"name": "Delete Target"}},
        ]

        setup_response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/applyEdits",
            data={"adds": json.dumps(setup_adds), "f": "json"},
        )

        if setup_response.status_code != 200:
            pytest.skip("Setup adds not supported")

        setup_data = setup_response.json()
        add_results = setup_data.get("addResults", [])

        if len(add_results) < 2:
            pytest.skip("Setup adds failed")

        update_id = add_results[0].get("objectId")
        delete_id = add_results[1].get("objectId")

        if not update_id or not delete_id:
            pytest.skip("ObjectIds not returned")

        # Combined operation: add new, update one, delete one
        new_point = geometry_generator.point("new_feature", lon=-122.43)
        combined_response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/applyEdits",
            data={
                "adds": json.dumps([
                    {"geometry": new_point.to_esri_json(), "attributes": {"name": "New Feature"}}
                ]),
                "updates": json.dumps([
                    {"attributes": {"OBJECTID": update_id, "name": "Updated"}}
                ]),
                "deletes": json.dumps([delete_id]),
                "f": "json",
            },
        )
        assert combined_response.status_code == 200
        data = combined_response.json()

        # Verify all operations present in response
        assert "addResults" in data
        assert "updateResults" in data
        assert "deleteResults" in data


class TestApplyEditsErrors:
    """Tests for error handling in applyEdits."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_apply_edits_invalid_json(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
    ):
        """Invalid JSON should return error."""
        response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/applyEdits",
            data={"adds": "not valid json", "f": "json"},
        )
        assert response.status_code == 400

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_apply_edits_invalid_layer(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        geometry_generator: GeometryGenerator,
    ):
        """applyEdits on invalid layer should return error."""
        point = geometry_generator.point("test")
        adds = [{"geometry": point.to_esri_json(), "attributes": {"name": "Test"}}]

        response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/99999/applyEdits",
            data={"adds": json.dumps(adds), "f": "json"},
        )
        assert response.status_code in [400, 404]

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_apply_edits_empty_request(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
    ):
        """Empty applyEdits request should handle gracefully."""
        response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/applyEdits",
            data={"f": "json"},
        )
        # May return 200 with empty results or 400
        assert response.status_code in [200, 400]

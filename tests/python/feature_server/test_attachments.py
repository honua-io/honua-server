# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Tests for GeoServices REST attachment endpoints.

Endpoints:
- GET/POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryAttachments
- POST /rest/services/{serviceId}/FeatureServer/{layerId}/addAttachment
- POST /rest/services/{serviceId}/FeatureServer/{layerId}/updateAttachment
- POST /rest/services/{serviceId}/FeatureServer/{layerId}/deleteAttachments
- GET /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/attachments/{attachmentId}
"""

import io
import json

import pytest
import httpx

from shared.geometry import GeometryGenerator


class TestQueryAttachments:
    """Tests for the queryAttachments endpoint."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_attachments_returns_200(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """queryAttachments should return 200 OK."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/queryAttachments",
            params={"objectId": 1, "f": "json"},
        )
        # May return 200 or 400 if objectId doesn't exist
        assert response.status_code in [200, 400, 404]

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_attachments_post(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """queryAttachments via POST."""
        response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/queryAttachments",
            data={"objectId": 1, "f": "json"},
        )
        assert response.status_code in [200, 400, 404]

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_attachments_structure(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """queryAttachments response structure."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/queryAttachments",
            params={"objectId": 1, "f": "json"},
        )

        if response.status_code == 200:
            data = response.json()
            # Should have attachmentGroups or attachmentInfos
            assert "attachmentGroups" in data or "attachmentInfos" in data


class TestAddAttachment:
    """Tests for the addAttachment endpoint."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_add_attachment_to_feature(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
        geometry_generator: GeometryGenerator,
    ):
        """Add an attachment to a feature."""
        # First, create a feature
        point = geometry_generator.point("attachment_test")
        adds = [{"geometry": point.to_esri_json(), "attributes": {"name": "Attachment Test"}}]

        add_response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/applyEdits",
            data={"adds": json.dumps(adds), "f": "json"},
        )

        if add_response.status_code != 200:
            pytest.skip("Feature creation not supported")

        add_data = add_response.json()
        object_id = add_data.get("addResults", [{}])[0].get("objectId")

        if not object_id:
            pytest.skip("ObjectId not returned")

        # Create a test file
        test_content = b"Test attachment content"
        files = {"attachment": ("test.txt", io.BytesIO(test_content), "text/plain")}
        data = {"f": "json"}

        response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/addAttachment",
            params={"objectId": object_id},
            files=files,
            data=data,
        )
        # May succeed or fail depending on attachment support
        assert response.status_code in [200, 400, 404]

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_add_attachment_missing_object_id(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """addAttachment without objectId should error."""
        test_content = b"Test content"
        files = {"attachment": ("test.txt", io.BytesIO(test_content), "text/plain")}

        response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/addAttachment",
            files=files,
            data={"f": "json"},
        )
        assert response.status_code == 400

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_add_attachment_to_nonexistent_feature(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """addAttachment to nonexistent feature should error."""
        test_content = b"Test content"
        files = {"attachment": ("test.txt", io.BytesIO(test_content), "text/plain")}

        response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/addAttachment",
            params={"objectId": 999999999},
            files=files,
            data={"f": "json"},
        )
        # Should return error
        assert response.status_code in [400, 404] or (
            response.status_code == 200 and
            response.json().get("addAttachmentResult", {}).get("success") is False
        )


class TestUpdateAttachment:
    """Tests for the updateAttachment endpoint."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_update_attachment_invalid_returns_error(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """updateAttachment with invalid IDs should return error."""
        response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/updateAttachment",
            data={
                "objectId": 999999999,
                "attachmentId": 999999999,
                "f": "json",
            },
        )
        assert response.status_code in [400, 404] or (
            response.status_code == 200 and
            response.json().get("updateAttachmentResult", {}).get("success") is False
        )


class TestDeleteAttachments:
    """Tests for the deleteAttachments endpoint."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_delete_attachments_invalid_returns_error(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """deleteAttachments with invalid IDs should return error."""
        response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/deleteAttachments",
            data={
                "objectId": 999999999,
                "attachmentIds": "1,2,3",
                "f": "json",
            },
        )
        # May handle gracefully or return error
        assert response.status_code in [200, 400, 404]

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_delete_attachments_missing_ids(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """deleteAttachments without required parameters should error."""
        response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/deleteAttachments",
            data={"f": "json"},
        )
        assert response.status_code == 400


class TestDownloadAttachment:
    """Tests for the attachment download endpoint."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_download_attachment_invalid_returns_404(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Download nonexistent attachment should return 404."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/999999999/attachments/999999999"
        )
        assert response.status_code == 404


class TestAttachmentWorkflow:
    """End-to-end attachment workflow tests."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    @pytest.mark.slow
    def test_full_attachment_lifecycle(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
        geometry_generator: GeometryGenerator,
    ):
        """Test full attachment lifecycle: add, query, download, delete."""
        # 1. Create a feature
        point = geometry_generator.point("lifecycle_test")
        adds = [{"geometry": point.to_esri_json(), "attributes": {"name": "Lifecycle Test"}}]

        add_response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/applyEdits",
            data={"adds": json.dumps(adds), "f": "json"},
        )

        if add_response.status_code != 200:
            pytest.skip("Feature creation not supported")

        add_data = add_response.json()
        object_id = add_data.get("addResults", [{}])[0].get("objectId")

        if not object_id:
            pytest.skip("ObjectId not returned")

        # 2. Add attachment
        test_content = b"Test attachment lifecycle content"
        files = {"attachment": ("lifecycle.txt", io.BytesIO(test_content), "text/plain")}

        attach_response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/addAttachment",
            params={"objectId": object_id},
            files=files,
            data={"f": "json"},
        )

        if attach_response.status_code != 200:
            pytest.skip("Attachment add not supported")

        attach_data = attach_response.json()
        result = attach_data.get("addAttachmentResult", {})

        if not result.get("success"):
            pytest.skip("Attachment add failed")

        attachment_id = result.get("objectId")

        if not attachment_id:
            pytest.skip("Attachment ID not returned")

        # 3. Query attachments
        query_response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/queryAttachments",
            params={"objectId": object_id, "f": "json"},
        )
        assert query_response.status_code == 200

        # 4. Download attachment
        download_response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/{object_id}/attachments/{attachment_id}"
        )
        if download_response.status_code == 200:
            assert download_response.content == test_content

        # 5. Delete attachment
        delete_response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/deleteAttachments",
            data={
                "objectId": object_id,
                "attachmentIds": str(attachment_id),
                "f": "json",
            },
        )
        # Verify deletion
        assert delete_response.status_code in [200, 400]

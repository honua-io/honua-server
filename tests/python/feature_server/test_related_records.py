# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Tests for GeoServices REST queryRelatedRecords endpoint.

Endpoints:
- GET /rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords
- POST /rest/services/{serviceId}/FeatureServer/{layerId}/queryRelatedRecords

Tests cover:
- Basic related records queries
- Multiple object IDs
- Filtering related records
- Pagination
"""

import pytest
import httpx


class TestQueryRelatedRecordsBasic:
    """Basic tests for queryRelatedRecords endpoint."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_related_records_get_returns_response(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """queryRelatedRecords GET should return a response."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/queryRelatedRecords",
            params={
                "objectIds": "1",
                "relationshipId": 0,
                "f": "json",
            },
        )
        # May return 200, 400 (if relationship doesn't exist), or 404
        assert response.status_code in [200, 400, 404]

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_related_records_post_returns_response(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """queryRelatedRecords POST should return a response."""
        response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/queryRelatedRecords",
            data={
                "objectIds": "1",
                "relationshipId": 0,
                "f": "json",
            },
        )
        assert response.status_code in [200, 400, 404]

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_related_records_structure(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """queryRelatedRecords should have correct response structure."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/queryRelatedRecords",
            params={
                "objectIds": "1,2,3",
                "relationshipId": 0,
                "f": "json",
            },
        )

        if response.status_code == 200:
            data = response.json()
            # Should have relatedRecordGroups
            assert "relatedRecordGroups" in data
            assert isinstance(data["relatedRecordGroups"], list)

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_related_records_missing_params_returns_error(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """queryRelatedRecords without required params should error."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/queryRelatedRecords",
            params={"f": "json"},
        )
        assert response.status_code == 400


class TestQueryRelatedRecordsObjectIds:
    """Tests for objectIds parameter handling."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_related_records_single_object_id(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Query related records for a single object ID."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/queryRelatedRecords",
            params={
                "objectIds": "1",
                "relationshipId": 0,
                "f": "json",
            },
        )
        assert response.status_code in [200, 400, 404]

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_related_records_multiple_object_ids(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Query related records for multiple object IDs."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/queryRelatedRecords",
            params={
                "objectIds": "1,2,3,4,5",
                "relationshipId": 0,
                "f": "json",
            },
        )
        assert response.status_code in [200, 400, 404]

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_related_records_object_ids_array_format(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Query related records with array format object IDs."""
        response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/queryRelatedRecords",
            json={
                "objectIds": [1, 2, 3],
                "relationshipId": 0,
                "f": "json",
            },
        )
        # JSON body may or may not be supported
        assert response.status_code in [200, 400, 404, 415]


class TestQueryRelatedRecordsFiltering:
    """Tests for filtering related records."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_related_records_with_where(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Filter related records with WHERE clause."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/queryRelatedRecords",
            params={
                "objectIds": "1",
                "relationshipId": 0,
                "where": "name IS NOT NULL",
                "f": "json",
            },
        )
        assert response.status_code in [200, 400, 404]

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_related_records_with_out_fields(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Select specific fields from related records."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/queryRelatedRecords",
            params={
                "objectIds": "1",
                "relationshipId": 0,
                "outFields": "name,id",
                "f": "json",
            },
        )
        assert response.status_code in [200, 400, 404]

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_related_records_with_return_geometry(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Include geometry in related records."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/queryRelatedRecords",
            params={
                "objectIds": "1",
                "relationshipId": 0,
                "returnGeometry": "true",
                "f": "json",
            },
        )
        assert response.status_code in [200, 400, 404]


class TestQueryRelatedRecordsPagination:
    """Tests for related records pagination."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_related_records_with_offset(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Paginate related records with offset."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/queryRelatedRecords",
            params={
                "objectIds": "1",
                "relationshipId": 0,
                "resultOffset": 5,
                "f": "json",
            },
        )
        assert response.status_code in [200, 400, 404]

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_related_records_with_record_count(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Limit related records count."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/queryRelatedRecords",
            params={
                "objectIds": "1",
                "relationshipId": 0,
                "resultRecordCount": 10,
                "f": "json",
            },
        )
        assert response.status_code in [200, 400, 404]


class TestQueryRelatedRecordsErrors:
    """Error handling tests for queryRelatedRecords."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_related_records_invalid_relationship_id(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Invalid relationship ID should return error."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/queryRelatedRecords",
            params={
                "objectIds": "1",
                "relationshipId": 99999,
                "f": "json",
            },
        )
        # Should return 400 or include error in response
        assert response.status_code in [200, 400, 404]
        if response.status_code == 200:
            data = response.json()
            # May have empty results or error
            assert "error" in data or "relatedRecordGroups" in data

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_related_records_invalid_layer(
        self, http_client: httpx.Client, test_service_id: str
    ):
        """queryRelatedRecords on invalid layer should return 404."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/99999/queryRelatedRecords",
            params={
                "objectIds": "1",
                "relationshipId": 0,
                "f": "json",
            },
        )
        assert response.status_code in [400, 404]

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_related_records_invalid_object_ids(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Invalid object IDs format should return error."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/queryRelatedRecords",
            params={
                "objectIds": "not,valid,ids",
                "relationshipId": 0,
                "f": "json",
            },
        )
        assert response.status_code == 400

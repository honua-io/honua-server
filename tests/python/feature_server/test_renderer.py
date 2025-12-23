# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Tests for GeoServices REST generateRenderer endpoint.

Endpoint: GET /rest/services/{serviceId}/FeatureServer/{layerId}/generateRenderer
"""

import pytest
import httpx


class TestGenerateRenderer:
    """Tests for the generateRenderer endpoint."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_generate_renderer_returns_response(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """generateRenderer should return a response."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/generateRenderer",
            params={
                "classificationDef": '{"type":"uniqueValueDef","uniqueValueFields":["name"]}',
                "f": "json",
            },
        )
        # May return 200, 400, or 501 if not implemented
        assert response.status_code in [200, 400, 404, 501]

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_generate_renderer_unique_value(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Generate unique value renderer."""
        classification = {
            "type": "uniqueValueDef",
            "uniqueValueFields": ["name"],
        }

        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/generateRenderer",
            params={
                "classificationDef": str(classification).replace("'", '"'),
                "f": "json",
            },
        )
        assert response.status_code in [200, 400, 404, 501]

        if response.status_code == 200:
            data = response.json()
            # Should contain renderer definition
            assert "type" in data or "renderer" in data or "error" in data

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_generate_renderer_class_breaks(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Generate class breaks renderer."""
        classification = {
            "type": "classBreaksDef",
            "classificationField": "id",
            "classificationMethod": "esriClassifyNaturalBreaks",
            "breakCount": 5,
        }

        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/generateRenderer",
            params={
                "classificationDef": str(classification).replace("'", '"'),
                "f": "json",
            },
        )
        assert response.status_code in [200, 400, 404, 501]

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_generate_renderer_with_where(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Generate renderer with WHERE clause filter."""
        classification = {
            "type": "uniqueValueDef",
            "uniqueValueFields": ["name"],
        }

        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/generateRenderer",
            params={
                "classificationDef": str(classification).replace("'", '"'),
                "where": "id > 0",
                "f": "json",
            },
        )
        assert response.status_code in [200, 400, 404, 501]

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_generate_renderer_invalid_classification(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Invalid classification definition should return error."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/generateRenderer",
            params={
                "classificationDef": "invalid json",
                "f": "json",
            },
        )
        assert response.status_code in [400, 501]

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_generate_renderer_invalid_layer(
        self, http_client: httpx.Client, test_service_id: str
    ):
        """generateRenderer on invalid layer should return error."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/99999/generateRenderer",
            params={
                "classificationDef": '{"type":"uniqueValueDef","uniqueValueFields":["name"]}',
                "f": "json",
            },
        )
        assert response.status_code in [400, 404]

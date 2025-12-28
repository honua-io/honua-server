# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Tests for OpenAPI specification endpoint.

Endpoint: GET /openapi.json
"""

import pytest
import httpx


class TestOpenApiSpec:
    """Tests for OpenAPI specification endpoint."""

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_openapi_returns_200(self, http_client: httpx.Client):
        response = http_client.get("/openapi.json")
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_openapi_content_type(self, http_client: httpx.Client):
        response = http_client.get("/openapi.json")
        content_type = response.headers.get("content-type", "")
        assert "json" in content_type.lower()

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_openapi_contains_paths(self, http_client: httpx.Client):
        response = http_client.get("/openapi.json")
        data = response.json()
        assert "openapi" in data
        assert "paths" in data
        paths = data.get("paths", {})
        assert "/ogc/features" in paths
        assert "/ogc/features/collections" in paths
        assert "/ogc/features/collections/{collectionId}/items" in paths

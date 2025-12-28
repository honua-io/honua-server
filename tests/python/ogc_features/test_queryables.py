# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Tests for OGC API Features queryables endpoint.

Endpoint: GET /ogc/features/collections/{collectionId}/queryables
"""

import pytest
import httpx


class TestQueryables:
    """Tests for queryables schema."""

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_queryables_returns_200(self, http_client: httpx.Client, test_collection_id: str):
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/queryables"
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_queryables_contains_properties(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/queryables"
        )
        data = response.json()
        assert data.get("type") == "object"
        assert "properties" in data
        props = data.get("properties", {})

        expected = {
            "name",
            "status",
            "count",
            "ratio",
            "active",
            "created_at",
            "event_date",
            "event_time",
            "uid",
        }
        for key in expected:
            assert key in props

        # JSON fields are not simple queryables
        assert "tags" not in props
        assert "numbers" not in props

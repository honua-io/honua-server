# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

import httpx
import pytest


class TestServiceQuery:
    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_service_query_get_returns_layer_results(
        self, http_client: httpx.Client, test_service_id: str
    ):
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/query",
            params={"where": "1=1", "f": "json"},
        )

        assert response.status_code == 200

        data = response.json()
        assert "layers" in data
        assert isinstance(data["layers"], list)
        assert len(data["layers"]) >= 1
        assert "id" in data["layers"][0]

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_service_query_post_returns_405(
        self, http_client: httpx.Client, test_service_id: str
    ):
        response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/query",
            data={"where": "1=1", "f": "json"},
        )

        assert response.status_code == 405
        assert "GET" in response.headers.get("allow", "")

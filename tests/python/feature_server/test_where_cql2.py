# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
CQL2-Text coverage for FeatureServer where parameter.
"""

import pytest
import httpx

from shared.cql2_cases import CQL2_TEXT_CASES

FEATURESERVER_CQL2_TEXT_CASES = [
    (name, expression)
    for name, expression in CQL2_TEXT_CASES
    if not name.startswith(("spatial_", "temporal_", "array_"))
]


class TestFeatureServerCql2Where:
    """CQL2-Text filter coverage for FeatureServer queries."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    @pytest.mark.parametrize("name, expression", FEATURESERVER_CQL2_TEXT_CASES)
    def test_query_where_cql2_text(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
        name: str,
        expression: str,
    ):
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": expression, "f": "json"},
        )
        assert response.status_code == 200

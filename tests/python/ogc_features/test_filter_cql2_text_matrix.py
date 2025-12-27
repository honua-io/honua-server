# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
CQL2-Text filter matrix for OGC API Features items.
"""

import pytest
import httpx

from shared.cql2_cases import CQL2_TEXT_CASES


class TestCql2TextFilters:
    """Matrix coverage for CQL2-Text filters."""

    @pytest.mark.integration
    @pytest.mark.ogc
    @pytest.mark.parametrize("name, expression", CQL2_TEXT_CASES)
    def test_items_filter_cql2_text(
        self,
        http_client: httpx.Client,
        test_collection_id: str,
        name: str,
        expression: str,
    ):
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"filter": expression},
        )
        assert response.status_code == 200

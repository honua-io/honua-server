# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Tests for CQL2-JSON filtering on OGC API Features items.
"""

import json

import pytest
import httpx

from shared.cql2_cases import CQL2_JSON_CASES


class TestCql2JsonFilters:
    """Tests for cql2-json filter-lang support."""

    @pytest.mark.integration
    @pytest.mark.ogc
    @pytest.mark.parametrize("name, payload", CQL2_JSON_CASES)
    def test_items_filter_cql2_json(
        self,
        http_client: httpx.Client,
        test_collection_id: str,
        name: str,
        payload: dict,
    ):
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={
                "filter": json.dumps(payload),
                "filter-lang": "cql2-json",
            },
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_filter_lang_invalid(self, http_client: httpx.Client, test_collection_id: str):
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"filter": "name = 'alpha'", "filter-lang": "unsupported"},
        )
        assert response.status_code == 400

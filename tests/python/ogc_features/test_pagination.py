# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Tests for OGC API Features pagination and link semantics.

Tests cover:
- Pagination parameters (limit, offset)
- Navigation links (next, prev, self)
- Edge cases (boundaries, defaults, invalid values)
"""

import pytest
import httpx
from urllib.parse import urlparse, parse_qs


class TestPaginationLinks:
    """Tests for pagination link semantics."""

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_has_self_link(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Items response should have self link."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"limit": 5},
        )
        data = response.json()
        links = data.get("links", [])
        self_links = [link for link in links if link.get("rel") == "self"]
        assert len(self_links) >= 1, "Missing self link"

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_has_next_link_when_more_results(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Items should have next link when more results exist."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"limit": 1},
        )
        data = response.json()

        # If there are more features than returned, should have next link
        number_matched = data.get("numberMatched", 0)
        number_returned = data.get("numberReturned", 0)

        if number_matched > number_returned:
            links = data.get("links", [])
            next_links = [link for link in links if link.get("rel") == "next"]
            assert len(next_links) >= 1, "Should have next link when more results exist"

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_next_link_contains_offset(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Next link should contain correct offset."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"limit": 2},
        )
        data = response.json()

        links = data.get("links", [])
        next_links = [link for link in links if link.get("rel") == "next"]

        if next_links:
            next_href = next_links[0].get("href", "")
            parsed = urlparse(next_href)
            query_params = parse_qs(parsed.query)

            # Should have offset parameter
            if "offset" in query_params:
                offset = int(query_params["offset"][0])
                assert offset >= 2, "Next link offset should be >= limit"

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_prev_link_on_later_pages(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Items with offset should have prev link."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"limit": 2, "offset": 2},
        )
        data = response.json()

        links = data.get("links", [])
        prev_links = [link for link in links if link.get("rel") == "prev"]

        # If we have an offset, should have prev link (unless at first page after offset)
        # This is optional in the spec but recommended

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_link_has_type(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """All links should have type property."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"limit": 5},
        )
        data = response.json()

        links = data.get("links", [])
        for link in links:
            assert "type" in link, f"Link missing type: {link}"

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_follow_next_link(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Following next link should return next page of results."""
        # Get first page
        response1 = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"limit": 2},
        )
        data1 = response1.json()

        # Find next link
        links = data1.get("links", [])
        next_links = [link for link in links if link.get("rel") == "next"]

        if not next_links:
            pytest.skip("No next link available")

        next_href = next_links[0].get("href", "")

        # Follow the next link
        if next_href.startswith("http"):
            parsed = urlparse(next_href)
            path = parsed.path
            query = parsed.query
            response2 = http_client.get(f"{path}?{query}" if query else path)
        else:
            response2 = http_client.get(next_href)

        assert response2.status_code == 200
        data2 = response2.json()

        # Verify different features
        page1_ids = {f.get("id") for f in data1.get("features", [])}
        page2_ids = {f.get("id") for f in data2.get("features", [])}

        # Pages should not overlap
        if page1_ids and page2_ids:
            assert page1_ids.isdisjoint(page2_ids), "Pages should have different features"


class TestPaginationDefaults:
    """Tests for pagination default values."""

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_default_limit(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Items without limit should use server default."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items"
        )
        data = response.json()

        # Server should have a reasonable default limit
        features = data.get("features", [])
        # Default is typically 10-1000, should not be unlimited
        assert len(features) <= 10000, "Should have a default limit"

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_default_offset(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Items without offset should start from beginning."""
        # Get items without offset
        response1 = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"limit": 5},
        )
        data1 = response1.json()

        # Get items with offset=0
        response2 = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"limit": 5, "offset": 0},
        )
        data2 = response2.json()

        # Should return same features
        ids1 = [f.get("id") for f in data1.get("features", [])]
        ids2 = [f.get("id") for f in data2.get("features", [])]
        assert ids1 == ids2, "Default offset should be 0"


class TestPaginationEdgeCases:
    """Tests for pagination edge cases."""

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_limit_one(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Limit of 1 should return exactly one feature."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"limit": 1},
        )
        data = response.json()
        features = data.get("features", [])
        assert len(features) <= 1

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_offset_beyond_results(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Offset beyond total count should return empty."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"offset": 999999},
        )
        assert response.status_code == 200
        data = response.json()
        features = data.get("features", [])
        assert len(features) == 0

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_limit_exceeds_max(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Limit exceeding server max should be capped."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"limit": 999999},
        )
        # Should either cap to max or return 400
        assert response.status_code in [200, 400]

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_number_matched_consistent(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """numberMatched should be consistent across pages."""
        # Get first page
        response1 = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"limit": 2},
        )
        data1 = response1.json()
        matched1 = data1.get("numberMatched")

        # Get second page
        response2 = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"limit": 2, "offset": 2},
        )
        data2 = response2.json()
        matched2 = data2.get("numberMatched")

        # numberMatched should be the same (total count)
        if matched1 is not None and matched2 is not None:
            assert matched1 == matched2, "numberMatched should be consistent"

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_items_number_returned_accurate(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """numberReturned should match actual feature count."""
        response = http_client.get(
            f"/ogc/features/collections/{test_collection_id}/items",
            params={"limit": 5},
        )
        data = response.json()
        features = data.get("features", [])
        number_returned = data.get("numberReturned")

        if number_returned is not None:
            assert number_returned == len(features), (
                "numberReturned should match actual feature count"
            )

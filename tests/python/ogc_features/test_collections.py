# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Tests for OGC API Features collections endpoints.

Endpoints:
- GET /ogc/features/collections
- GET /ogc/features/collections/{collectionId}
"""

import pytest
import httpx


class TestCollectionsList:
    """Tests for the collections list endpoint."""

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_collections_returns_200(self, http_client: httpx.Client):
        """Collections endpoint should return 200 OK."""
        response = http_client.get("/ogc/features/collections")
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_collections_contains_collections_array(self, http_client: httpx.Client):
        """Collections response should contain 'collections' array."""
        response = http_client.get("/ogc/features/collections")
        data = response.json()
        assert "collections" in data
        assert isinstance(data["collections"], list)

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_collections_contains_links(self, http_client: httpx.Client):
        """Collections response should contain links."""
        response = http_client.get("/ogc/features/collections")
        data = response.json()
        assert "links" in data
        assert isinstance(data["links"], list)

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_collections_has_self_link(self, http_client: httpx.Client):
        """Collections should have a 'self' link."""
        response = http_client.get("/ogc/features/collections")
        data = response.json()
        links = data.get("links", [])
        self_links = [link for link in links if link.get("rel") == "self"]
        assert len(self_links) >= 1, "Missing 'self' link"

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_each_collection_has_required_properties(self, http_client: httpx.Client):
        """Each collection should have required properties."""
        response = http_client.get("/ogc/features/collections")
        data = response.json()
        collections = data.get("collections", [])

        for collection in collections:
            # Required properties per OGC spec
            assert "id" in collection, f"Collection missing 'id': {collection}"
            assert "links" in collection, f"Collection missing 'links': {collection}"

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_each_collection_has_items_link(self, http_client: httpx.Client):
        """Each collection should have a link to items."""
        response = http_client.get("/ogc/features/collections")
        data = response.json()
        collections = data.get("collections", [])

        for collection in collections:
            links = collection.get("links", [])
            items_links = [link for link in links if link.get("rel") == "items"]
            assert len(items_links) >= 1, (
                f"Collection {collection.get('id')} missing 'items' link"
            )

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_collections_content_type(self, http_client: httpx.Client):
        """Collections should return application/json content type."""
        response = http_client.get("/ogc/features/collections")
        content_type = response.headers.get("content-type", "")
        assert "application/json" in content_type

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_collections_navigable_from_landing(self, http_client: httpx.Client):
        """Collections should be reachable from landing page."""
        # Get landing page
        landing = http_client.get("/ogc/features")
        landing_data = landing.json()

        # Find data/collections link
        links = landing_data.get("links", [])
        data_links = [link for link in links if link.get("rel") == "data"]
        assert len(data_links) >= 1

        # Navigate to collections
        data_href = data_links[0].get("href", "")
        if data_href.startswith("http"):
            from urllib.parse import urlparse
            path = urlparse(data_href).path
        else:
            path = data_href

        response = http_client.get(path)
        assert response.status_code == 200
        data = response.json()
        assert "collections" in data


class TestCollectionMetadata:
    """Tests for individual collection metadata endpoint."""

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_collection_returns_200(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Collection endpoint should return 200 for valid ID."""
        response = http_client.get(f"/ogc/features/collections/{test_collection_id}")
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_collection_contains_id(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Collection response should contain the collection ID."""
        response = http_client.get(f"/ogc/features/collections/{test_collection_id}")
        data = response.json()
        assert "id" in data
        assert data["id"] == test_collection_id

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_collection_contains_links(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Collection response should contain links."""
        response = http_client.get(f"/ogc/features/collections/{test_collection_id}")
        data = response.json()
        assert "links" in data
        assert isinstance(data["links"], list)

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_collection_has_self_link(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Collection should have a 'self' link."""
        response = http_client.get(f"/ogc/features/collections/{test_collection_id}")
        data = response.json()
        links = data.get("links", [])
        self_links = [link for link in links if link.get("rel") == "self"]
        assert len(self_links) >= 1, "Missing 'self' link"

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_collection_has_items_link(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Collection should have an 'items' link."""
        response = http_client.get(f"/ogc/features/collections/{test_collection_id}")
        data = response.json()
        links = data.get("links", [])
        items_links = [link for link in links if link.get("rel") == "items"]
        assert len(items_links) >= 1, "Missing 'items' link"

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_collection_invalid_id_returns_404(self, http_client: httpx.Client):
        """Collection endpoint should return 404 for invalid ID."""
        response = http_client.get("/ogc/features/collections/nonexistent_collection_xyz")
        assert response.status_code == 404

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_collection_content_type(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Collection should return application/json content type."""
        response = http_client.get(f"/ogc/features/collections/{test_collection_id}")
        content_type = response.headers.get("content-type", "")
        assert "application/json" in content_type

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_collection_may_have_extent(
        self, http_client: httpx.Client, test_collection_id: str
    ):
        """Collection may have spatial/temporal extent."""
        response = http_client.get(f"/ogc/features/collections/{test_collection_id}")
        data = response.json()

        # Extent is optional but if present should have correct structure
        if "extent" in data:
            extent = data["extent"]
            if "spatial" in extent:
                spatial = extent["spatial"]
                assert "bbox" in spatial or "crs" in spatial
            if "temporal" in extent:
                temporal = extent["temporal"]
                assert "interval" in temporal or "trs" in temporal

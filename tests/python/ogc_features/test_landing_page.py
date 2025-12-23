# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Tests for OGC API Features landing page endpoint.

Endpoint: GET /ogc/features
"""

import pytest
import httpx


class TestLandingPage:
    """Tests for the OGC API Features landing page."""

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_landing_page_returns_200(self, http_client: httpx.Client):
        """Landing page should return 200 OK."""
        response = http_client.get("/ogc/features")
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_landing_page_contains_title(self, http_client: httpx.Client):
        """Landing page should contain a title."""
        response = http_client.get("/ogc/features")
        data = response.json()
        assert "title" in data
        assert isinstance(data["title"], str)
        assert len(data["title"]) > 0

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_landing_page_contains_description(self, http_client: httpx.Client):
        """Landing page should contain a description."""
        response = http_client.get("/ogc/features")
        data = response.json()
        assert "description" in data

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_landing_page_contains_links(self, http_client: httpx.Client):
        """Landing page should contain links array."""
        response = http_client.get("/ogc/features")
        data = response.json()
        assert "links" in data
        assert isinstance(data["links"], list)
        assert len(data["links"]) > 0

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_landing_page_has_self_link(self, http_client: httpx.Client):
        """Landing page should have a 'self' link."""
        response = http_client.get("/ogc/features")
        data = response.json()
        links = data.get("links", [])
        self_links = [link for link in links if link.get("rel") == "self"]
        assert len(self_links) >= 1, "Missing 'self' link"
        assert self_links[0].get("href") is not None

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_landing_page_has_conformance_link(self, http_client: httpx.Client):
        """Landing page should have a link to conformance."""
        response = http_client.get("/ogc/features")
        data = response.json()
        links = data.get("links", [])
        conformance_links = [link for link in links if link.get("rel") == "conformance"]
        assert len(conformance_links) >= 1, "Missing 'conformance' link"
        assert "/conformance" in conformance_links[0].get("href", "")

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_landing_page_has_data_link(self, http_client: httpx.Client):
        """Landing page should have a link to collections (data)."""
        response = http_client.get("/ogc/features")
        data = response.json()
        links = data.get("links", [])
        data_links = [link for link in links if link.get("rel") == "data"]
        assert len(data_links) >= 1, "Missing 'data' link to collections"
        assert "/collections" in data_links[0].get("href", "")

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_landing_page_links_have_type(self, http_client: httpx.Client):
        """All landing page links should have a type property."""
        response = http_client.get("/ogc/features")
        data = response.json()
        links = data.get("links", [])
        for link in links:
            assert "type" in link, f"Link missing 'type': {link}"

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_landing_page_content_type(self, http_client: httpx.Client):
        """Landing page should return application/json content type."""
        response = http_client.get("/ogc/features")
        content_type = response.headers.get("content-type", "")
        assert "application/json" in content_type

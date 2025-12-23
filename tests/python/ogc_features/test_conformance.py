# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Tests for OGC API Features conformance endpoint.

Endpoint: GET /ogc/features/conformance
"""

import pytest
import httpx


# Required OGC API Features conformance classes
REQUIRED_CONFORMANCE_CLASSES = [
    "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/core",
]

# Optional but expected conformance classes
EXPECTED_CONFORMANCE_CLASSES = [
    "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/oas30",
    "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/geojson",
]


class TestConformance:
    """Tests for the OGC API Features conformance endpoint."""

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_conformance_returns_200(self, http_client: httpx.Client):
        """Conformance endpoint should return 200 OK."""
        response = http_client.get("/ogc/features/conformance")
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_conformance_contains_conforms_to(self, http_client: httpx.Client):
        """Conformance response should contain 'conformsTo' array."""
        response = http_client.get("/ogc/features/conformance")
        data = response.json()
        assert "conformsTo" in data
        assert isinstance(data["conformsTo"], list)

    @pytest.mark.integration
    @pytest.mark.ogc
    @pytest.mark.parametrize("conformance_class", REQUIRED_CONFORMANCE_CLASSES)
    def test_conformance_includes_required_classes(
        self, http_client: httpx.Client, conformance_class: str
    ):
        """Conformance should declare required OGC conformance classes."""
        response = http_client.get("/ogc/features/conformance")
        data = response.json()
        conforms_to = data.get("conformsTo", [])
        assert conformance_class in conforms_to, (
            f"Missing required conformance class: {conformance_class}"
        )

    @pytest.mark.integration
    @pytest.mark.ogc
    @pytest.mark.parametrize("conformance_class", EXPECTED_CONFORMANCE_CLASSES)
    def test_conformance_includes_expected_classes(
        self, http_client: httpx.Client, conformance_class: str
    ):
        """Conformance should declare expected OGC conformance classes."""
        response = http_client.get("/ogc/features/conformance")
        data = response.json()
        conforms_to = data.get("conformsTo", [])
        assert conformance_class in conforms_to, (
            f"Missing expected conformance class: {conformance_class}"
        )

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_conformance_content_type(self, http_client: httpx.Client):
        """Conformance should return application/json content type."""
        response = http_client.get("/ogc/features/conformance")
        content_type = response.headers.get("content-type", "")
        assert "application/json" in content_type

    @pytest.mark.integration
    @pytest.mark.ogc
    def test_conformance_is_navigable_from_landing(self, http_client: httpx.Client):
        """Conformance should be reachable from landing page link."""
        # Get landing page
        landing = http_client.get("/ogc/features")
        landing_data = landing.json()

        # Find conformance link
        links = landing_data.get("links", [])
        conformance_links = [link for link in links if link.get("rel") == "conformance"]
        assert len(conformance_links) >= 1

        # Navigate to conformance
        conformance_href = conformance_links[0].get("href", "")
        # Extract path from URL
        if conformance_href.startswith("http"):
            from urllib.parse import urlparse
            path = urlparse(conformance_href).path
        else:
            path = conformance_href

        response = http_client.get(path)
        assert response.status_code == 200
        data = response.json()
        assert "conformsTo" in data

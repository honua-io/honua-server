# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Tests for GeoServices REST service and layer metadata endpoints.

Endpoints:
- GET /rest/services/{serviceId}/FeatureServer
- GET /rest/services/{serviceId}/FeatureServer/{layerId}
"""

import pytest
import httpx


class TestServiceMetadata:
    """Tests for the FeatureServer service metadata endpoint."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_service_metadata_returns_200(
        self, http_client: httpx.Client, test_service_id: str
    ):
        """Service metadata should return 200 OK."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer"
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_service_metadata_contains_layers(
        self, http_client: httpx.Client, test_service_id: str
    ):
        """Service metadata should contain layers array."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer"
        )
        data = response.json()
        assert "layers" in data
        assert isinstance(data["layers"], list)

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_service_metadata_contains_service_description(
        self, http_client: httpx.Client, test_service_id: str
    ):
        """Service metadata should contain serviceDescription."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer"
        )
        data = response.json()
        # serviceDescription may be empty but should be present
        assert "serviceDescription" in data or "description" in data

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_service_metadata_layer_structure(
        self, http_client: httpx.Client, test_service_id: str
    ):
        """Each layer in service metadata should have required properties."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer"
        )
        data = response.json()
        layers = data.get("layers", [])

        for layer in layers:
            assert "id" in layer, f"Layer missing 'id': {layer}"
            assert "name" in layer, f"Layer missing 'name': {layer}"

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_service_metadata_current_version(
        self, http_client: httpx.Client, test_service_id: str
    ):
        """Service metadata should include currentVersion."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer"
        )
        data = response.json()
        # currentVersion is standard in Esri REST API
        assert "currentVersion" in data or "version" in data

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_service_metadata_invalid_service_returns_404(
        self, http_client: httpx.Client
    ):
        """Invalid service ID should return 404."""
        response = http_client.get(
            "/rest/services/nonexistent_service_xyz/FeatureServer"
        )
        assert response.status_code == 404

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_service_metadata_format_json(
        self, http_client: httpx.Client, test_service_id: str
    ):
        """Service metadata with f=json should return JSON."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer",
            params={"f": "json"},
        )
        assert response.status_code == 200
        content_type = response.headers.get("content-type", "")
        assert "json" in content_type.lower()


class TestLayerMetadata:
    """Tests for the FeatureServer layer metadata endpoint."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_layer_metadata_returns_200(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Layer metadata should return 200 OK."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}"
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_layer_metadata_contains_id(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Layer metadata should contain the layer ID."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}"
        )
        data = response.json()
        assert "id" in data
        assert data["id"] == test_layer_id

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_layer_metadata_contains_name(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Layer metadata should contain name."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}"
        )
        data = response.json()
        assert "name" in data

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_layer_metadata_contains_geometry_type(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Layer metadata should contain geometryType."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}"
        )
        data = response.json()
        assert "geometryType" in data
        # Valid Esri geometry types
        valid_types = [
            "esriGeometryPoint",
            "esriGeometryMultipoint",
            "esriGeometryPolyline",
            "esriGeometryPolygon",
            "esriGeometryEnvelope",
        ]
        assert data["geometryType"] in valid_types or data["geometryType"] is None

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_layer_metadata_contains_fields(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Layer metadata should contain fields array."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}"
        )
        data = response.json()
        assert "fields" in data
        assert isinstance(data["fields"], list)

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_layer_metadata_fields_structure(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Each field should have required properties."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}"
        )
        data = response.json()
        fields = data.get("fields", [])

        for field in fields:
            assert "name" in field, f"Field missing 'name': {field}"
            assert "type" in field, f"Field missing 'type': {field}"

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_layer_metadata_contains_extent(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Layer metadata should contain extent."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}"
        )
        data = response.json()
        # extent may be null if no features exist
        assert "extent" in data

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_layer_metadata_invalid_layer_returns_404(
        self, http_client: httpx.Client, test_service_id: str
    ):
        """Invalid layer ID should return 404."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/99999"
        )
        assert response.status_code == 404

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_layer_metadata_capabilities(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Layer should declare its capabilities."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}"
        )
        data = response.json()
        # capabilities may be a string or array
        assert "capabilities" in data

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_layer_metadata_spatial_reference(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Layer should include spatial reference information."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}"
        )
        data = response.json()
        # Check for spatialReference or within extent
        has_sr = "spatialReference" in data
        has_sr_in_extent = (
            "extent" in data and
            data["extent"] is not None and
            "spatialReference" in data.get("extent", {})
        )
        assert has_sr or has_sr_in_extent

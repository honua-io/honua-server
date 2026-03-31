# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Tests for GeoServices REST query endpoint.

Endpoints:
- GET /rest/services/{serviceId}/FeatureServer/{layerId}/query
- POST /rest/services/{serviceId}/FeatureServer/{layerId}/query

Tests cover:
- WHERE clause filtering (comparison, logical, LIKE, IN, BETWEEN, IS NULL)
- Spatial queries (geometry, geometryType, spatialRel)
- Output control (outFields, returnGeometry)
- Pagination (resultOffset, resultRecordCount)
- Response formats (json, geojson)
"""

import json

import pytest
import httpx
from shapely.geometry import shape

from shared.geometry import GeometryGenerator
from conftest import assert_esri_feature_set


class TestQueryBasic:
    """Basic tests for the query endpoint."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_get_returns_200(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Query GET should return 200 OK."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "1=1", "f": "json"},
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_post_returns_200(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Query POST should return 200 OK."""
        response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            data={"where": "1=1", "f": "json"},
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_returns_features(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Query should return features array."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "1=1", "f": "json"},
        )
        data = response.json()
        assert "features" in data
        assert isinstance(data["features"], list)

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_features_have_attributes(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Each feature should have attributes."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "1=1", "f": "json", "resultRecordCount": 10},
        )
        data = response.json()
        features = data.get("features", [])

        for feature in features:
            assert "attributes" in feature

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_invalid_layer_returns_error(
        self, http_client: httpx.Client, test_service_id: str
    ):
        """Query on invalid layer should return error."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/99999/query",
            params={"where": "1=1", "f": "json"},
        )
        assert response.status_code in [400, 404]


class TestQueryWhereClause:
    """Tests for WHERE clause filtering."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_where_equals(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Query with equality comparison."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "name = 'test'", "f": "json"},
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_where_not_equals(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Query with not-equals comparison."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "name <> 'excluded'", "f": "json"},
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_where_less_than(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Query with less-than comparison."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "count < 100", "f": "json"},
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_where_greater_than(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Query with greater-than comparison."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "count > 0", "f": "json"},
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_where_like(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Query with LIKE pattern matching."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "name LIKE 'test%'", "f": "json"},
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_where_in(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Query with IN operator."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "name IN ('test1', 'test2')", "f": "json"},
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_where_between(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Query with BETWEEN operator."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "count BETWEEN 1 AND 100", "f": "json"},
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_where_is_null(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Query with IS NULL."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "description IS NULL", "f": "json"},
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_where_and(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Query with AND logical operator."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "count > 0 AND name IS NOT NULL", "f": "json"},
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_where_or(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Query with OR logical operator."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "name = 'a' OR name = 'b'", "f": "json"},
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_where_not(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Query with NOT logical operator."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "NOT (name = 'excluded')", "f": "json"},
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_where_parentheses(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Query with nested parentheses."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "(name = 'a' OR name = 'b') AND count > 0", "f": "json"},
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_where_invalid_returns_error(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Invalid WHERE syntax should return error."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "invalid !!! syntax", "f": "json"},
        )
        assert response.status_code == 400


class TestQuerySpatial:
    """Tests for spatial query parameters."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    @pytest.mark.geometry
    def test_query_spatial_envelope(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
        geometry_generator: GeometryGenerator,
    ):
        """Query with envelope geometry filter."""
        bbox = geometry_generator.bbox()
        envelope = {
            "xmin": bbox[0],
            "ymin": bbox[1],
            "xmax": bbox[2],
            "ymax": bbox[3],
            "spatialReference": {"wkid": 4326},
        }
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={
                "where": "1=1",
                "geometry": json.dumps(envelope),
                "geometryType": "esriGeometryEnvelope",
                "spatialRel": "esriSpatialRelIntersects",
                "f": "json",
            },
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.featureserver
    @pytest.mark.geometry
    def test_query_spatial_point(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
        geometry_generator: GeometryGenerator,
    ):
        """Query with point geometry filter."""
        point = geometry_generator.point()
        esri_point = point.to_esri_json()
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={
                "where": "1=1",
                "geometry": json.dumps(esri_point),
                "geometryType": "esriGeometryPoint",
                "spatialRel": "esriSpatialRelIntersects",
                "f": "json",
            },
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.featureserver
    @pytest.mark.geometry
    def test_query_spatial_polygon(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
        geometry_generator: GeometryGenerator,
    ):
        """Query with polygon geometry filter."""
        polygon = geometry_generator.polygon_simple()
        esri_polygon = polygon.to_esri_json()
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={
                "where": "1=1",
                "geometry": json.dumps(esri_polygon),
                "geometryType": "esriGeometryPolygon",
                "spatialRel": "esriSpatialRelIntersects",
                "f": "json",
            },
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.featureserver
    @pytest.mark.parametrize(
        "spatial_rel",
        [
            "esriSpatialRelIntersects",
            "esriSpatialRelContains",
            "esriSpatialRelWithin",
            "esriSpatialRelEnvelopeIntersects",
        ],
    )
    def test_query_spatial_relationships(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
        geometry_generator: GeometryGenerator,
        spatial_rel: str,
    ):
        """Test different spatial relationships."""
        bbox = geometry_generator.bbox()
        envelope = {
            "xmin": bbox[0],
            "ymin": bbox[1],
            "xmax": bbox[2],
            "ymax": bbox[3],
            "spatialReference": {"wkid": 4326},
        }
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={
                "where": "1=1",
                "geometry": json.dumps(envelope),
                "geometryType": "esriGeometryEnvelope",
                "spatialRel": spatial_rel,
                "f": "json",
            },
        )
        assert response.status_code == 200


class TestQueryOutput:
    """Tests for output control parameters."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_out_fields_all(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """outFields=* should return all fields."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "1=1", "outFields": "*", "f": "json"},
        )
        data = response.json()
        features = data.get("features", [])
        if features:
            # Should have multiple attributes
            assert len(features[0].get("attributes", {})) > 0

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_out_fields_specific(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """outFields with specific fields should limit returned attributes."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "1=1", "outFields": "name", "f": "json"},
        )
        data = response.json()
        features = data.get("features", [])
        # Verify limited fields returned
        for feature in features:
            attrs = feature.get("attributes", {})
            # Should contain 'name' if it exists in schema
            # Other fields like OBJECTID may also be included

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_return_geometry_true(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """returnGeometry=true should include geometry."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "1=1", "returnGeometry": "true", "f": "json"},
        )
        data = response.json()
        features = data.get("features", [])
        for feature in features:
            # Features should have geometry (may be null for some records)
            assert "geometry" in feature

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_return_geometry_false(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """returnGeometry=false should exclude geometry."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "1=1", "returnGeometry": "false", "f": "json"},
        )
        data = response.json()
        features = data.get("features", [])
        for feature in features:
            # Geometry should be absent or null when returnGeometry=false
            assert "geometry" not in feature or feature.get("geometry") is None


class TestQueryPagination:
    """Tests for query pagination parameters."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_result_record_count(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """resultRecordCount should limit returned features."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "1=1", "resultRecordCount": 2, "f": "json"},
        )
        data = response.json()
        features = data.get("features", [])
        assert len(features) <= 2

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_result_offset(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """resultOffset should skip features."""
        # Get first page
        response1 = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "1=1", "resultRecordCount": 2, "f": "json"},
        )
        page1 = response1.json()

        # Get second page
        response2 = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={
                "where": "1=1",
                "resultRecordCount": 2,
                "resultOffset": 2,
                "f": "json",
            },
        )
        page2 = response2.json()

        # Verify different features
        if page1.get("features") and page2.get("features"):
            # Get OBJECTIDs if available
            page1_ids = {
                f.get("attributes", {}).get("OBJECTID")
                or f.get("attributes", {}).get("objectid")
                or f.get("attributes", {}).get("id")
                for f in page1["features"]
            }
            page2_ids = {
                f.get("attributes", {}).get("OBJECTID")
                or f.get("attributes", {}).get("objectid")
                or f.get("attributes", {}).get("id")
                for f in page2["features"]
            }
            # Pages should not overlap
            assert page1_ids.isdisjoint(page2_ids)


class TestQueryFormat:
    """Tests for response format options."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_format_json(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """f=json should return Esri JSON format."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "1=1", "f": "json"},
        )
        assert response.status_code == 200
        data = response.json()
        assert "features" in data
        # Esri JSON uses "attributes" not "properties"
        if data["features"]:
            assert "attributes" in data["features"][0]

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_format_geojson(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """f=geojson should return GeoJSON format."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "1=1", "f": "geojson"},
        )
        assert response.status_code == 200
        data = response.json()
        # GeoJSON uses FeatureCollection
        assert data.get("type") == "FeatureCollection"
        # GeoJSON uses "properties" not "attributes"
        if data.get("features"):
            assert "properties" in data["features"][0]


class TestQueryGeometry:
    """Tests for geometry handling in query responses."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    @pytest.mark.geometry
    def test_query_geometries_valid(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """All returned geometries should be valid."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={
                "where": "1=1",
                "returnGeometry": "true",
                "f": "geojson",
                "resultRecordCount": 50,
            },
        )
        data = response.json()
        features = data.get("features", [])

        for feature in features:
            geometry = feature.get("geometry")
            if geometry is not None:
                geom = shape(geometry)
                assert geom.is_valid, f"Invalid geometry: {geometry}"

    @pytest.mark.integration
    @pytest.mark.featureserver
    @pytest.mark.geometry
    def test_query_esri_geometry_format(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        """Esri JSON format should use Esri geometry structure."""
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={
                "where": "1=1",
                "returnGeometry": "true",
                "f": "json",
                "resultRecordCount": 10,
            },
        )
        data = response.json()
        features = data.get("features", [])

        for feature in features:
            geometry = feature.get("geometry")
            if geometry is not None:
                # Esri geometry uses x/y for points, rings for polygons, paths for lines
                has_point = "x" in geometry and "y" in geometry
                has_rings = "rings" in geometry
                has_paths = "paths" in geometry
                has_points = "points" in geometry
                assert any([has_point, has_rings, has_paths, has_points]), (
                    f"Unexpected Esri geometry structure: {geometry}"
                )

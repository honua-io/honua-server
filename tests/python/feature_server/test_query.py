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

import httpx
import pytest
from shapely.geometry import shape
from shared.geometry import GeometryGenerator
from shared.geoservices import assert_geoservices_error


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
        # PA-070/PA-117 (#2418): unknown layer -> HTTP 200 + {"error": {"code": 404}}.
        assert_geoservices_error(response, body_codes={400, 404})


# Authored independently of query responses: contrast both predicate outcomes,
# numeric boundaries, NULL versus empty text, and SQL three-valued logic.
WHERE_ROWS = {'a': {'name': 'test1', 'count': -5, 'description': None},
 'b': {'name': 'test2', 'count': 0, 'description': 'present'},
 'c': {'name': 'excluded', 'count': 1, 'description': ''},
 'd': {'name': 'other', 'count': 100, 'description': 'other'},
 'e': {'name': None, 'count': 150, 'description': None},
 'f': {'name': 'test99', 'count': 101, 'description': 'present'}}

WHERE_CASES = {'test_query_where_equals': ("name = 'test1'", ('a',)),
 'test_query_where_not_equals': ("name <> 'excluded'", ('a', 'b', 'd', 'f')),
 'test_query_where_less_than': ('count < 100', ('a', 'b', 'c')),
 'test_query_where_greater_than': ('count > 0', ('c', 'd', 'e', 'f')),
 'test_query_where_like': ("name LIKE 'test%'", ('a', 'b', 'f')),
 'test_query_where_in': ("name IN ('test1', 'test2')", ('a', 'b')),
 'test_query_where_between': ('count BETWEEN 1 AND 100', ('c', 'd')),
 'test_query_where_is_null': ('description IS NULL', ('a', 'e')),
 'test_query_where_and': ('count > 0 AND name IS NOT NULL', ('c', 'd', 'f')),
 'test_query_where_or': ("name = 'test1' OR name = 'excluded'", ('a', 'c')),
 'test_query_where_not': ("NOT (name = 'excluded')", ('a', 'b', 'd', 'f')),
 'test_query_where_parentheses': ("(name = 'test1' OR name = 'excluded') AND count > 0",
                                  ('c',))}


@pytest.fixture
def where_cohort(test_data, test_layer_id):
    """Add the small authored cohort to this test's existing worker schema."""
    cohort = {}
    try:
        for key, attributes in WHERE_ROWS.items():
            [(object_id,)] = test_data.fixture.execute_returning(
                "INSERT INTO features (layer_id, geometry, attributes) "
                "VALUES (%s, ST_SetSRID(ST_MakePoint(-122.4, 37.7), 4326), %s::jsonb) "
                "RETURNING objectid",
                test_data.schema, (test_layer_id, json.dumps(attributes)),
            )
            cohort[key] = {"objectid": object_id, **attributes}
        yield cohort
    finally:
        if cohort:
            test_data.fixture.execute(
                "DELETE FROM features WHERE layer_id = %s AND objectid = ANY(%s)",
                test_data.schema, (test_layer_id, [row["objectid"] for row in cohort.values()]),
            )


def assert_where_probe(http_client, service_id, layer_id, cohort, case_name):
    """Exercise the real HTTP probe and require the exact independently authored rows."""
    predicate, expected_keys = WHERE_CASES[case_name]
    object_ids = ",".join(str(row["objectid"]) for row in cohort.values())
    response = http_client.get(
        f"/rest/services/{service_id}/FeatureServer/{layer_id}/query",
        params={"where": f"objectid IN ({object_ids}) AND ({predicate})", "f": "json",
                "outFields": "objectid,name,count,description", "returnGeometry": "false"},
    )
    assert response.status_code == 200
    body = response.json()
    assert isinstance(body, dict) and "error" not in body, body
    assert body.get("exceededTransferLimit", False) is False, body
    features = body.get("features")
    assert isinstance(features, list) and len(features) == len(expected_keys), body
    actual = {}
    for feature in features:
        assert isinstance(feature, dict) and isinstance(feature.get("attributes"), dict), feature
        attributes = feature["attributes"]
        object_id = attributes.get("objectid")
        assert type(object_id) is int and object_id not in actual, attributes
        assert all(field in attributes for field in ("name", "count", "description")), attributes
        assert type(attributes["count"]) is int, attributes
        actual[object_id] = {field: attributes[field] for field in ("objectid", "name", "count", "description")}
    expected = {cohort[key]["objectid"]: cohort[key] for key in expected_keys}
    assert actual == expected, {"expected": expected, "actual": actual}


class TestQueryWhereClause:
    """Exact WHERE results for authored rows, never HTTP status alone."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_where_equals(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int, where_cohort
    ):
        assert_where_probe(http_client, test_service_id, test_layer_id, where_cohort,
                           "test_query_where_equals")

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_where_not_equals(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int, where_cohort
    ):
        assert_where_probe(http_client, test_service_id, test_layer_id, where_cohort,
                           "test_query_where_not_equals")

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_where_less_than(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int, where_cohort
    ):
        assert_where_probe(http_client, test_service_id, test_layer_id, where_cohort,
                           "test_query_where_less_than")

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_where_greater_than(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int, where_cohort
    ):
        assert_where_probe(http_client, test_service_id, test_layer_id, where_cohort,
                           "test_query_where_greater_than")

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_where_like(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int, where_cohort
    ):
        assert_where_probe(http_client, test_service_id, test_layer_id, where_cohort,
                           "test_query_where_like")

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_where_in(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int, where_cohort
    ):
        assert_where_probe(http_client, test_service_id, test_layer_id, where_cohort,
                           "test_query_where_in")

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_where_between(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int, where_cohort
    ):
        assert_where_probe(http_client, test_service_id, test_layer_id, where_cohort,
                           "test_query_where_between")

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_where_is_null(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int, where_cohort
    ):
        assert_where_probe(http_client, test_service_id, test_layer_id, where_cohort,
                           "test_query_where_is_null")

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_where_and(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int, where_cohort
    ):
        assert_where_probe(http_client, test_service_id, test_layer_id, where_cohort,
                           "test_query_where_and")

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_where_or(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int, where_cohort
    ):
        assert_where_probe(http_client, test_service_id, test_layer_id, where_cohort,
                           "test_query_where_or")

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_where_not(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int, where_cohort
    ):
        assert_where_probe(http_client, test_service_id, test_layer_id, where_cohort,
                           "test_query_where_not")

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_where_parentheses(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int, where_cohort
    ):
        assert_where_probe(http_client, test_service_id, test_layer_id, where_cohort,
                           "test_query_where_parentheses")

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
        # PA-070/PA-117 (#2418): invalid where -> HTTP 200 + {"error": {"code": 400}}.
        assert_geoservices_error(response, body_codes={400})


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
            # Requested outFields=name, so the field must be present.
            # Other fields like OBJECTID may also be included.
            assert "name" in attrs

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

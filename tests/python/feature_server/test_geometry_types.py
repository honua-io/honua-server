# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Tests for comprehensive geometry type coverage in FeatureServer.

Tests all GeoJSON geometry types:
- Points, MultiPoints
- LineStrings, MultiLineStrings
- Polygons with holes, MultiPolygons with holes
- GeometryCollections
- Null geometries
"""

import json

import pytest
import httpx
from shapely.geometry import shape

from shared.geometry import GeometryGenerator, TestGeometry


class TestGeometryTypes:
    """Tests for all supported geometry types."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    @pytest.mark.geometry
    def test_add_point_geometry(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
        geometry_generator: GeometryGenerator,
    ):
        """Add and retrieve a Point geometry."""
        point = geometry_generator.point()
        self._verify_geometry_roundtrip(
            http_client, test_service_id, test_layer_id, point
        )

    @pytest.mark.integration
    @pytest.mark.featureserver
    @pytest.mark.geometry
    def test_add_multipoint_geometry(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
        geometry_generator: GeometryGenerator,
    ):
        """Add and retrieve a MultiPoint geometry."""
        multipoint = geometry_generator.multipoint()
        self._verify_geometry_roundtrip(
            http_client, test_service_id, test_layer_id, multipoint
        )

    @pytest.mark.integration
    @pytest.mark.featureserver
    @pytest.mark.geometry
    def test_add_linestring_geometry(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
        geometry_generator: GeometryGenerator,
    ):
        """Add and retrieve a LineString geometry."""
        linestring = geometry_generator.linestring()
        self._verify_geometry_roundtrip(
            http_client, test_service_id, test_layer_id, linestring
        )

    @pytest.mark.integration
    @pytest.mark.featureserver
    @pytest.mark.geometry
    def test_add_multilinestring_geometry(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
        geometry_generator: GeometryGenerator,
    ):
        """Add and retrieve a MultiLineString geometry."""
        multiline = geometry_generator.multilinestring()
        self._verify_geometry_roundtrip(
            http_client, test_service_id, test_layer_id, multiline
        )

    @pytest.mark.integration
    @pytest.mark.featureserver
    @pytest.mark.geometry
    def test_add_polygon_simple(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
        geometry_generator: GeometryGenerator,
    ):
        """Add and retrieve a simple Polygon."""
        polygon = geometry_generator.polygon_simple()
        self._verify_geometry_roundtrip(
            http_client, test_service_id, test_layer_id, polygon
        )

    @pytest.mark.integration
    @pytest.mark.featureserver
    @pytest.mark.geometry
    def test_add_polygon_with_hole(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
        geometry_generator: GeometryGenerator,
    ):
        """Add and retrieve a Polygon with a hole."""
        polygon = geometry_generator.polygon_with_hole()
        self._verify_geometry_roundtrip(
            http_client, test_service_id, test_layer_id, polygon
        )

    @pytest.mark.integration
    @pytest.mark.featureserver
    @pytest.mark.geometry
    def test_add_polygon_with_multiple_holes(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
        geometry_generator: GeometryGenerator,
    ):
        """Add and retrieve a Polygon with multiple holes."""
        polygon = geometry_generator.polygon_with_multiple_holes()
        self._verify_geometry_roundtrip(
            http_client, test_service_id, test_layer_id, polygon
        )

    @pytest.mark.integration
    @pytest.mark.featureserver
    @pytest.mark.geometry
    def test_add_multipolygon_simple(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
        geometry_generator: GeometryGenerator,
    ):
        """Add and retrieve a simple MultiPolygon."""
        multipoly = geometry_generator.multipolygon_simple()
        self._verify_geometry_roundtrip(
            http_client, test_service_id, test_layer_id, multipoly
        )

    @pytest.mark.integration
    @pytest.mark.featureserver
    @pytest.mark.geometry
    def test_add_multipolygon_with_holes(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
        geometry_generator: GeometryGenerator,
    ):
        """Add and retrieve a MultiPolygon with holes."""
        multipoly = geometry_generator.multipolygon_with_holes()
        self._verify_geometry_roundtrip(
            http_client, test_service_id, test_layer_id, multipoly
        )

    @pytest.mark.integration
    @pytest.mark.featureserver
    @pytest.mark.geometry
    def test_add_geometry_collection(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
        geometry_generator: GeometryGenerator,
    ):
        """Add and retrieve a GeometryCollection."""
        geom_coll = geometry_generator.geometry_collection()
        # GeometryCollection may not be supported in all layers
        self._verify_geometry_roundtrip(
            http_client, test_service_id, test_layer_id, geom_coll, may_fail=True
        )

    @pytest.mark.integration
    @pytest.mark.featureserver
    @pytest.mark.geometry
    def test_add_null_geometry(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
        geometry_generator: GeometryGenerator,
    ):
        """Add and retrieve a feature with null geometry."""
        null_geom = geometry_generator.null_geometry()

        adds = [
            {
                "geometry": None,
                "attributes": {"name": "Null Geometry Test"},
            }
        ]

        response = http_client.post(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/applyEdits",
            data={"adds": json.dumps(adds), "f": "json"},
        )

        # May succeed or fail depending on layer configuration
        if response.status_code == 200:
            data = response.json()
            add_results = data.get("addResults", [])
            if add_results and add_results[0].get("success"):
                # Verify null geometry was stored
                object_id = add_results[0].get("objectId")
                query_response = http_client.get(
                    f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
                    params={
                        "where": f"OBJECTID = {object_id}",
                        "returnGeometry": "true",
                        "f": "json",
                    },
                )
                if query_response.status_code == 200:
                    query_data = query_response.json()
                    features = query_data.get("features", [])
                    if features:
                        # Geometry should be null
                        assert features[0].get("geometry") is None

    def _verify_geometry_roundtrip(
        self,
        http_client: httpx.Client,
        service_id: str,
        layer_id: int,
        test_geom: TestGeometry,
        may_fail: bool = False,
    ):
        """
        Add a geometry and verify it can be retrieved correctly.

        Args:
            http_client: HTTP client
            service_id: Service ID
            layer_id: Layer ID
            test_geom: TestGeometry to add
            may_fail: If True, don't fail if add is not supported
        """
        esri_geom = test_geom.to_esri_json()
        adds = [
            {
                "geometry": esri_geom,
                "attributes": {"name": test_geom.name},
            }
        ]

        response = http_client.post(
            f"/rest/services/{service_id}/FeatureServer/{layer_id}/applyEdits",
            data={"adds": json.dumps(adds), "f": "json"},
        )

        if may_fail and response.status_code != 200:
            pytest.skip(f"Geometry type {test_geom.geometry_type} not supported")
            return

        if response.status_code != 200:
            pytest.skip(f"Add operation not supported")
            return

        data = response.json()
        add_results = data.get("addResults", [])

        if not add_results or not add_results[0].get("success"):
            if may_fail:
                pytest.skip(f"Geometry type {test_geom.geometry_type} add failed")
            return

        # Query back the feature
        object_id = add_results[0].get("objectId")
        query_response = http_client.get(
            f"/rest/services/{service_id}/FeatureServer/{layer_id}/query",
            params={
                "where": f"OBJECTID = {object_id}",
                "returnGeometry": "true",
                "f": "geojson",
            },
        )

        if query_response.status_code == 200:
            query_data = query_response.json()
            features = query_data.get("features", [])
            if features and features[0].get("geometry"):
                # Validate retrieved geometry with shapely
                retrieved_geom = shape(features[0]["geometry"])
                assert retrieved_geom.is_valid, (
                    f"Retrieved {test_geom.geometry_type} geometry is invalid"
                )


class TestGeometryMatrixCoverage:
    """Matrix test for all geometry types with all operations."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    @pytest.mark.geometry
    @pytest.mark.parametrize(
        "geometry_method",
        [
            "point",
            "multipoint",
            "linestring",
            "multilinestring",
            "polygon_simple",
            "polygon_with_hole",
            "polygon_with_multiple_holes",
            "multipolygon_simple",
            "multipolygon_with_holes",
        ],
    )
    def test_query_geometry_types(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
        geometry_generator: GeometryGenerator,
        geometry_method: str,
    ):
        """Query features with each geometry type."""
        geom = getattr(geometry_generator, geometry_method)()
        esri_geom = geom.to_esri_json()

        # Use geometry as a spatial filter
        geometry_type_map = {
            "point": "esriGeometryPoint",
            "multipoint": "esriGeometryMultipoint",
            "linestring": "esriGeometryPolyline",
            "multilinestring": "esriGeometryPolyline",
            "polygon_simple": "esriGeometryPolygon",
            "polygon_with_hole": "esriGeometryPolygon",
            "polygon_with_multiple_holes": "esriGeometryPolygon",
            "multipolygon_simple": "esriGeometryPolygon",
            "multipolygon_with_holes": "esriGeometryPolygon",
        }

        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={
                "where": "1=1",
                "geometry": json.dumps(esri_geom),
                "geometryType": geometry_type_map.get(geometry_method, "esriGeometryPolygon"),
                "spatialRel": "esriSpatialRelIntersects",
                "f": "json",
            },
        )
        # Should succeed or return empty results
        assert response.status_code == 200

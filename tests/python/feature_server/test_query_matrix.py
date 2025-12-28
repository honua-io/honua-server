# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Matrix coverage for FeatureServer query parameters.
"""

import json

import pytest
import httpx
from pyproj import Transformer

from shared.geometry import GeometryGenerator


NON_DISTANCE_SPATIAL_RELS = [
    "esriSpatialRelIntersects",
    "esriSpatialRelContains",
    "esriSpatialRelWithin",
    "esriSpatialRelEnvelopeIntersects",
    "esriSpatialRelCrosses",
    "esriSpatialRelTouches",
    "esriSpatialRelOverlaps",
    "esriSpatialRelDisjoint",
    "esriSpatialRelEquals",
]

DISTANCE_SPATIAL_RELS = [
    "esriSpatialRelWithinDistance",
    "esriSpatialRelBeyondDistance",
]

DISTANCE_UNITS = [
    "esriSRUnit_Meter",
    "esriSRUnit_Foot",
    "esriSRUnit_Kilometer",
    "esriSRUnit_StatuteMile",
]

GEOMETRY_TYPE_CASES = [
    ("point", "esriGeometryPoint"),
    ("multipoint", "esriGeometryMultipoint"),
    ("linestring", "esriGeometryPolyline"),
    ("multilinestring", "esriGeometryPolyline"),
    ("polygon_simple", "esriGeometryPolygon"),
    ("polygon_with_hole", "esriGeometryPolygon"),
    ("multipolygon_simple", "esriGeometryPolygon"),
]


class TestSpatialRelMatrix:
    """Coverage for spatialRel variants."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    @pytest.mark.geometry
    @pytest.mark.parametrize("spatial_rel", NON_DISTANCE_SPATIAL_RELS)
    def test_query_spatial_rel_non_distance(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
        geometry_generator: GeometryGenerator,
        spatial_rel: str,
    ):
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

    @pytest.mark.integration
    @pytest.mark.featureserver
    @pytest.mark.geometry
    @pytest.mark.parametrize("spatial_rel", DISTANCE_SPATIAL_RELS)
    @pytest.mark.parametrize("unit", DISTANCE_UNITS)
    def test_query_spatial_rel_distance(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
        geometry_generator: GeometryGenerator,
        spatial_rel: str,
        unit: str,
    ):
        point = geometry_generator.point()
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={
                "where": "1=1",
                "geometry": json.dumps(point.to_esri_json()),
                "geometryType": "esriGeometryPoint",
                "spatialRel": spatial_rel,
                "distance": 1000,
                "units": unit,
                "f": "json",
            },
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.featureserver
    @pytest.mark.geometry
    def test_query_spatial_rel_distance_requires_distance(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
        geometry_generator: GeometryGenerator,
    ):
        point = geometry_generator.point()
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={
                "where": "1=1",
                "geometry": json.dumps(point.to_esri_json()),
                "geometryType": "esriGeometryPoint",
                "spatialRel": "esriSpatialRelWithinDistance",
                "f": "json",
            },
        )
        assert response.status_code == 400


class TestGeometryTypeMatrix:
    """Coverage for geometryType variants."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    @pytest.mark.geometry
    @pytest.mark.parametrize("geometry_method, geometry_type", GEOMETRY_TYPE_CASES)
    def test_query_geometry_type_matrix(
        self,
        http_client: httpx.Client,
        test_service_id: str,
        test_layer_id: int,
        geometry_generator: GeometryGenerator,
        geometry_method: str,
        geometry_type: str,
    ):
        geom = getattr(geometry_generator, geometry_method)()
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={
                "where": "1=1",
                "geometry": json.dumps(geom.to_esri_json()),
                "geometryType": geometry_type,
                "spatialRel": "esriSpatialRelIntersects",
                "f": "json",
            },
        )
        assert response.status_code == 200


class TestNearestCount:
    """Coverage for nearestCount and returnDistance."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    @pytest.mark.geometry
    def test_query_nearest_count_requires_geometry(
        self, http_client: httpx.Client, test_service_id: str, test_layer_id: int
    ):
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "1=1", "nearestCount": 3, "f": "json"},
        )
        assert response.status_code == 400

    @pytest.mark.integration
    @pytest.mark.featureserver
    @pytest.mark.geometry
    def test_query_nearest_count_with_distance(self, http_client: httpx.Client, test_service_id: str, test_layer_id: int):
        point = GeometryGenerator().point()
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={
                "where": "1=1",
                "geometry": json.dumps(point.to_esri_json()),
                "geometryType": "esriGeometryPoint",
                "nearestCount": 3,
                "returnDistance": "true",
                "f": "json",
            },
        )
        assert response.status_code == 200
        data = response.json()
        features = data.get("features", [])
        if features:
            attrs = features[0].get("attributes", {})
            assert "distance" in attrs


class TestInputOutputSpatialReference:
    """Coverage for inSR/outSR handling."""

    @pytest.mark.integration
    @pytest.mark.featureserver
    @pytest.mark.geometry
    def test_query_in_sr_3857(self, http_client: httpx.Client, test_service_id: str, test_layer_id: int):
        transformer = Transformer.from_crs("EPSG:4326", "EPSG:3857", always_xy=True)
        x, y = transformer.transform(-122.4194, 37.7749)
        esri_point = {"x": x, "y": y, "spatialReference": {"wkid": 3857}}

        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={
                "where": "1=1",
                "geometry": json.dumps(esri_point),
                "geometryType": "esriGeometryPoint",
                "inSR": "3857",
                "f": "json",
            },
        )
        assert response.status_code == 200

    @pytest.mark.integration
    @pytest.mark.featureserver
    @pytest.mark.geometry
    def test_query_out_sr_3857(self, http_client: httpx.Client, test_service_id: str, test_layer_id: int):
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={
                "where": "1=1",
                "returnGeometry": "true",
                "outSR": "3857",
                "f": "json",
            },
        )
        assert response.status_code == 200
        data = response.json()
        sr = data.get("spatialReference", {})
        assert sr.get("wkid") == 3857 or sr.get("latestWkid") == 3857

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_in_sr_invalid(self, http_client: httpx.Client, test_service_id: str, test_layer_id: int):
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "1=1", "inSR": "invalid", "f": "json"},
        )
        assert response.status_code == 400

    @pytest.mark.integration
    @pytest.mark.featureserver
    def test_query_out_sr_invalid(self, http_client: httpx.Client, test_service_id: str, test_layer_id: int):
        response = http_client.get(
            f"/rest/services/{test_service_id}/FeatureServer/{test_layer_id}/query",
            params={"where": "1=1", "outSR": "invalid", "f": "json"},
        )
        assert response.status_code == 400

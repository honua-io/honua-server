"""Unit tests for the proto adapter conversion functions."""
from __future__ import annotations

import pytest

from honua_sdk.grpc._generated.honua.v1 import feature_service_pb2 as pb2
from honua_sdk.grpc import _proto_adapter as adapter
from honua_sdk.grpc._models import (
    FieldType,
    GeometryType,
    QueryFeaturesRequest,
    SpatialReference,
    StatisticDefinition,
    StatisticType,
)


# ---------------------------------------------------------------------------
# to_proto_request
# ---------------------------------------------------------------------------


class TestToProtoRequest:
    """Tests for domain -> proto request conversion."""

    def test_basic_request(self) -> None:
        req = QueryFeaturesRequest(
            service_id="my-service",
            layer_id=3,
            where="population > 1000",
            return_geometry=True,
        )
        proto = adapter.to_proto_request(req)

        assert proto.service_id == "my-service"
        assert proto.layer_id == 3
        assert proto.where == "population > 1000"
        assert proto.return_geometry is True

    def test_out_fields_and_object_ids(self) -> None:
        req = QueryFeaturesRequest(
            service_id="svc",
            layer_id=0,
            out_fields=["name", "area"],
            object_ids=[1, 2, 3],
        )
        proto = adapter.to_proto_request(req)

        assert list(proto.out_fields) == ["name", "area"]
        assert list(proto.object_ids) == [1, 2, 3]

    def test_out_sr(self) -> None:
        req = QueryFeaturesRequest(
            service_id="svc",
            layer_id=0,
            out_sr=SpatialReference(wkid=3857, latest_wkid=3857),
        )
        proto = adapter.to_proto_request(req)

        assert proto.out_sr.wkid == 3857
        assert proto.out_sr.latest_wkid == 3857

    def test_pagination_and_ordering(self) -> None:
        req = QueryFeaturesRequest(
            service_id="svc",
            layer_id=0,
            result_offset=10,
            result_record_count=50,
            order_by="name ASC",
            return_distinct=True,
        )
        proto = adapter.to_proto_request(req)

        assert proto.result_offset == 10
        assert proto.result_record_count == 50
        assert proto.order_by == "name ASC"
        assert proto.return_distinct is True

    def test_count_ids_extent_flags(self) -> None:
        req = QueryFeaturesRequest(
            service_id="svc",
            layer_id=0,
            return_count_only=True,
            return_ids_only=True,
            return_extent_only=True,
        )
        proto = adapter.to_proto_request(req)

        assert proto.return_count_only is True
        assert proto.return_ids_only is True
        assert proto.return_extent_only is True

    def test_statistics(self) -> None:
        req = QueryFeaturesRequest(
            service_id="svc",
            layer_id=0,
            out_statistics=[
                StatisticDefinition(
                    on_statistic_field="area",
                    statistic_type=StatisticType.SUM,
                    out_statistic_field_name="total_area",
                ),
            ],
            group_by=["state"],
        )
        proto = adapter.to_proto_request(req)

        assert len(proto.out_statistics) == 1
        assert proto.out_statistics[0].on_statistic_field == "area"
        assert proto.out_statistics[0].statistic_type == pb2.STATISTIC_TYPE_SUM
        assert proto.out_statistics[0].out_statistic_field_name == "total_area"
        assert list(proto.group_by) == ["state"]

    def test_geometry_precision_and_offset(self) -> None:
        req = QueryFeaturesRequest(
            service_id="svc",
            layer_id=0,
            geometry_precision=6,
            max_allowable_offset=0.001,
        )
        proto = adapter.to_proto_request(req)

        assert proto.geometry_precision == 6
        assert proto.max_allowable_offset == pytest.approx(0.001)


# ---------------------------------------------------------------------------
# from_proto_response
# ---------------------------------------------------------------------------


class TestFromProtoResponse:
    """Tests for proto -> domain response conversion."""

    def test_standard_feature_response(
        self, proto_query_response: pb2.QueryFeaturesResponse
    ) -> None:
        result = adapter.from_proto_response(proto_query_response)

        assert result.object_id_field_name == "OBJECTID"
        assert result.geometry_type == GeometryType.POINT
        assert result.spatial_reference is not None
        assert result.spatial_reference.wkid == 4326
        assert len(result.fields) == 1
        assert result.fields[0].name == "name"
        assert result.fields[0].field_type == FieldType.STRING
        assert result.fields[0].length == 255
        assert result.fields[0].nullable is True
        assert len(result.features) == 1

        f = result.features[0]
        assert f.id == 42
        assert f.attributes["name"] == "test-park"
        assert f.attributes["area"] == pytest.approx(123.45)
        assert f.attributes["count"] == 7
        assert f.attributes["active"] is True
        assert f.attributes["missing"] is None
        assert f.geometry is not None
        assert f.geometry["x"] == pytest.approx(-122.4194)
        assert f.geometry["y"] == pytest.approx(37.7749)

    def test_count_only_response(self) -> None:
        resp = pb2.QueryFeaturesResponse()
        resp.count = 42

        result = adapter.from_proto_response(resp)
        assert result.count == 42
        assert result.features == []

    def test_ids_only_response(self) -> None:
        resp = pb2.QueryFeaturesResponse()
        resp.object_id_field_name = "OBJECTID"
        resp.object_ids.extend([1, 2, 3, 4, 5])

        result = adapter.from_proto_response(resp)
        assert result.object_ids == [1, 2, 3, 4, 5]
        assert result.object_id_field_name == "OBJECTID"
        assert result.features == []

    def test_extent_only_response(self) -> None:
        resp = pb2.QueryFeaturesResponse()
        resp.extent.xmin = -180.0
        resp.extent.ymin = -90.0
        resp.extent.xmax = 180.0
        resp.extent.ymax = 90.0
        resp.extent.spatial_reference.wkid = 4326

        result = adapter.from_proto_response(resp)
        assert result.extent is not None
        assert result.extent.xmin == pytest.approx(-180.0)
        assert result.extent.ymin == pytest.approx(-90.0)
        assert result.extent.xmax == pytest.approx(180.0)
        assert result.extent.ymax == pytest.approx(90.0)
        assert result.extent.spatial_reference is not None
        assert result.extent.spatial_reference.wkid == 4326

    def test_exceeded_transfer_limit(self) -> None:
        resp = pb2.QueryFeaturesResponse()
        resp.exceeded_transfer_limit = True

        result = adapter.from_proto_response(resp)
        assert result.exceeded_transfer_limit is True

    def test_empty_response(self) -> None:
        resp = pb2.QueryFeaturesResponse()

        result = adapter.from_proto_response(resp)
        assert result.features == []
        assert result.fields == []
        assert result.geometry_type == GeometryType.UNSPECIFIED
        assert result.spatial_reference is None


# ---------------------------------------------------------------------------
# from_proto_page
# ---------------------------------------------------------------------------


class TestFromProtoPage:
    """Tests for proto -> domain page conversion."""

    def test_page_with_features(
        self,
        proto_spatial_reference: pb2.SpatialReference,
        proto_point_feature: pb2.Feature,
    ) -> None:
        page = pb2.FeaturePage()
        page.object_id_field_name = "OBJECTID"
        page.geometry_type = pb2.GEOMETRY_TYPE_POINT
        page.spatial_reference.CopyFrom(proto_spatial_reference)

        fd = page.fields.add()
        fd.name = "name"
        fd.field_type = pb2.FIELD_TYPE_STRING
        fd.length = 255
        fd.nullable = True

        page.features.add().CopyFrom(proto_point_feature)
        page.is_last_page = False

        result = adapter.from_proto_page(page)

        assert result.object_id_field_name == "OBJECTID"
        assert result.geometry_type == GeometryType.POINT
        assert result.spatial_reference is not None
        assert len(result.features) == 1
        assert result.is_last_page is False

    def test_last_page(self) -> None:
        page = pb2.FeaturePage()
        page.is_last_page = True

        result = adapter.from_proto_page(page)
        assert result.is_last_page is True
        assert result.features == []


# ---------------------------------------------------------------------------
# Attribute conversion
# ---------------------------------------------------------------------------


class TestConvertAttribute:
    """Tests for individual attribute value conversion."""

    def test_string_value(self) -> None:
        attr = pb2.AttributeValue()
        attr.string_value = "hello"
        assert adapter._convert_attribute(attr) == "hello"

    def test_int32_value(self) -> None:
        attr = pb2.AttributeValue()
        attr.int32_value = 42
        assert adapter._convert_attribute(attr) == 42

    def test_int64_value(self) -> None:
        attr = pb2.AttributeValue()
        attr.int64_value = 9999999999
        assert adapter._convert_attribute(attr) == 9999999999

    def test_double_value(self) -> None:
        attr = pb2.AttributeValue()
        attr.double_value = 3.14
        assert adapter._convert_attribute(attr) == pytest.approx(3.14)

    def test_float_value(self) -> None:
        attr = pb2.AttributeValue()
        attr.float_value = 2.5
        assert adapter._convert_attribute(attr) == pytest.approx(2.5)

    def test_bool_value(self) -> None:
        attr = pb2.AttributeValue()
        attr.bool_value = True
        assert adapter._convert_attribute(attr) is True

    def test_datetime_value(self) -> None:
        attr = pb2.AttributeValue()
        attr.datetime_value = 1709251200000  # 2024-03-01 UTC ms
        assert adapter._convert_attribute(attr) == 1709251200000

    def test_bytes_value(self) -> None:
        attr = pb2.AttributeValue()
        attr.bytes_value = b"\x00\x01"
        assert adapter._convert_attribute(attr) == b"\x00\x01"

    def test_null_value(self) -> None:
        attr = pb2.AttributeValue()
        attr.null_value = pb2.NULL_VALUE
        assert adapter._convert_attribute(attr) is None

    def test_unset_value(self) -> None:
        attr = pb2.AttributeValue()
        assert adapter._convert_attribute(attr) is None


# ---------------------------------------------------------------------------
# Geometry conversion
# ---------------------------------------------------------------------------


class TestConvertGeometry:
    """Tests for geometry proto -> Esri JSON conversion."""

    def test_point(self) -> None:
        geom = pb2.Geometry()
        geom.point.x = -122.4194
        geom.point.y = 37.7749
        result = adapter._convert_geometry(geom)

        assert result == {"x": pytest.approx(-122.4194), "y": pytest.approx(37.7749)}

    def test_point_with_z(self) -> None:
        geom = pb2.Geometry()
        geom.point.x = 0.0
        geom.point.y = 0.0
        geom.point.z = 100.0
        result = adapter._convert_geometry(geom)

        assert result is not None
        assert result["z"] == pytest.approx(100.0)

    def test_multi_point(self) -> None:
        geom = pb2.Geometry()
        p1 = geom.multi_point.points.add()
        p1.x = 1.0
        p1.y = 2.0
        p2 = geom.multi_point.points.add()
        p2.x = 3.0
        p2.y = 4.0
        result = adapter._convert_geometry(geom)

        assert result == {"points": [[1.0, 2.0], [3.0, 4.0]]}

    def test_polyline(self, proto_polyline_feature: pb2.Feature) -> None:
        result = adapter._convert_geometry(proto_polyline_feature.geometry)

        assert result is not None
        assert "paths" in result
        assert len(result["paths"]) == 1
        assert result["paths"][0] == [[0.0, 0.0], [1.0, 1.0], [2.0, 2.0]]

    def test_polygon(self, proto_polygon_feature: pb2.Feature) -> None:
        result = adapter._convert_geometry(proto_polygon_feature.geometry)

        assert result is not None
        assert "rings" in result
        assert len(result["rings"]) == 1
        assert len(result["rings"][0]) == 5  # closed ring

    def test_multi_polygon(self, proto_multi_polygon_feature: pb2.Feature) -> None:
        result = adapter._convert_geometry(proto_multi_polygon_feature.geometry)

        assert result is not None
        assert "rings" in result
        # Two polygons, each with one ring -> 2 rings total
        assert len(result["rings"]) == 2

    def test_no_shape(self) -> None:
        geom = pb2.Geometry()
        result = adapter._convert_geometry(geom)
        assert result is None

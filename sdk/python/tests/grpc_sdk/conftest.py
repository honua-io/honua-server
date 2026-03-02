"""Shared fixtures for gRPC client tests."""
from __future__ import annotations

from unittest.mock import MagicMock

import pytest

from honua_sdk.grpc._generated.honua.v1 import feature_service_pb2 as pb2


@pytest.fixture
def proto_spatial_reference() -> pb2.SpatialReference:
    """Create a proto SpatialReference with common WGS-84 values."""
    sr = pb2.SpatialReference()
    sr.wkid = 4326
    sr.latest_wkid = 4326
    sr.wkt = ""
    return sr


@pytest.fixture
def proto_point_feature(proto_spatial_reference: pb2.SpatialReference) -> pb2.Feature:
    """Create a proto Feature with a point geometry and sample attributes."""
    f = pb2.Feature()
    f.id = 42
    f.attributes["name"].string_value = "test-park"
    f.attributes["area"].double_value = 123.45
    f.attributes["count"].int32_value = 7
    f.attributes["active"].bool_value = True
    f.attributes["missing"].null_value = pb2.NULL_VALUE

    f.geometry.point.x = -122.4194
    f.geometry.point.y = 37.7749
    return f


@pytest.fixture
def proto_polyline_feature() -> pb2.Feature:
    """Create a proto Feature with a polyline geometry."""
    f = pb2.Feature()
    f.id = 10

    path = f.geometry.polyline.paths.add()
    c1 = path.coords.add()
    c1.x = 0.0
    c1.y = 0.0
    c2 = path.coords.add()
    c2.x = 1.0
    c2.y = 1.0
    c3 = path.coords.add()
    c3.x = 2.0
    c3.y = 2.0
    return f


@pytest.fixture
def proto_polygon_feature() -> pb2.Feature:
    """Create a proto Feature with a polygon geometry."""
    f = pb2.Feature()
    f.id = 20

    ring = f.geometry.polygon.rings.add()
    for x, y in [(0, 0), (10, 0), (10, 10), (0, 10), (0, 0)]:
        c = ring.coords.add()
        c.x = float(x)
        c.y = float(y)
    return f


@pytest.fixture
def proto_multi_polygon_feature() -> pb2.Feature:
    """Create a proto Feature with a multi-polygon geometry."""
    f = pb2.Feature()
    f.id = 30

    poly1 = f.geometry.multi_polygon.polygons.add()
    ring1 = poly1.rings.add()
    for x, y in [(0, 0), (1, 0), (1, 1), (0, 1), (0, 0)]:
        c = ring1.coords.add()
        c.x = float(x)
        c.y = float(y)

    poly2 = f.geometry.multi_polygon.polygons.add()
    ring2 = poly2.rings.add()
    for x, y in [(5, 5), (6, 5), (6, 6), (5, 6), (5, 5)]:
        c = ring2.coords.add()
        c.x = float(x)
        c.y = float(y)
    return f


@pytest.fixture
def proto_query_response(
    proto_spatial_reference: pb2.SpatialReference,
    proto_point_feature: pb2.Feature,
) -> pb2.QueryFeaturesResponse:
    """Create a standard query response with one point feature."""
    resp = pb2.QueryFeaturesResponse()
    resp.object_id_field_name = "OBJECTID"
    resp.geometry_type = pb2.GEOMETRY_TYPE_POINT
    resp.spatial_reference.CopyFrom(proto_spatial_reference)

    fd = resp.fields.add()
    fd.name = "name"
    fd.field_type = pb2.FIELD_TYPE_STRING
    fd.length = 255
    fd.nullable = True

    resp.features.add().CopyFrom(proto_point_feature)
    return resp


@pytest.fixture
def mock_channel() -> MagicMock:
    """Create a mock gRPC channel."""
    channel = MagicMock()
    return channel

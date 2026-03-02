// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Sdk.Grpc.Conversion;
using Honua.Sdk.Grpc.Models;
using Proto = Honua.Server.Features.Grpc.Proto;

namespace Honua.Sdk.Grpc.Tests;

public class ProtoAdapterTests
{
    [Fact]
    public void ToProtoRequest_MinimalFields_MapsCorrectly()
    {
        var request = new QueryFeaturesRequest
        {
            ServiceId = "my-service",
            LayerId = 0,
        };

        var proto = ProtoAdapter.ToProtoRequest(request);

        Assert.Equal("my-service", proto.ServiceId);
        Assert.Equal(0, proto.LayerId);
        Assert.Equal("1=1", proto.Where);
        Assert.True(proto.ReturnGeometry);
        Assert.Empty(proto.ObjectIds);
        Assert.Empty(proto.OutFields);
        Assert.Empty(proto.OutStatistics);
        Assert.Empty(proto.GroupBy);
        Assert.Null(proto.OutSr);
    }

    [Fact]
    public void ToProtoRequest_AllFields_MapsCorrectly()
    {
        var request = new QueryFeaturesRequest
        {
            ServiceId = "svc",
            LayerId = 3,
            Where = "population > 100",
            ObjectIds = [1L, 2L, 3L],
            OutFields = ["name", "population"],
            ReturnGeometry = false,
            OutSr = new Models.SpatialReference { Wkid = 4326, LatestWkid = 4326, Wkt = "" },
            ResultOffset = 10,
            ResultRecordCount = 50,
            OrderBy = "name ASC",
            ReturnDistinct = true,
            ReturnCountOnly = true,
            ReturnIdsOnly = false,
            ReturnExtentOnly = false,
            OutStatistics =
            [
                new StatisticDefinition
                {
                    OnStatisticField = "population",
                    StatisticType = Models.StatisticType.Sum,
                    OutStatisticFieldName = "total_pop",
                },
            ],
            GroupBy = ["region"],
            GeometryPrecision = 6,
            MaxAllowableOffset = 0.001,
        };

        var proto = ProtoAdapter.ToProtoRequest(request);

        Assert.Equal("svc", proto.ServiceId);
        Assert.Equal(3, proto.LayerId);
        Assert.Equal("population > 100", proto.Where);
        Assert.Equal([1L, 2L, 3L], proto.ObjectIds);
        Assert.Equal(["name", "population"], proto.OutFields);
        Assert.False(proto.ReturnGeometry);
        Assert.NotNull(proto.OutSr);
        Assert.Equal(4326, proto.OutSr.Wkid);
        Assert.Equal(10, proto.ResultOffset);
        Assert.Equal(50, proto.ResultRecordCount);
        Assert.Equal("name ASC", proto.OrderBy);
        Assert.True(proto.ReturnDistinct);
        Assert.True(proto.ReturnCountOnly);
        Assert.False(proto.ReturnIdsOnly);
        Assert.False(proto.ReturnExtentOnly);
        Assert.Single(proto.OutStatistics);
        Assert.Equal("population", proto.OutStatistics[0].OnStatisticField);
        Assert.Equal(Proto.StatisticType.Sum, proto.OutStatistics[0].StatisticType);
        Assert.Equal("total_pop", proto.OutStatistics[0].OutStatisticFieldName);
        Assert.Equal(["region"], proto.GroupBy);
        Assert.Equal(6, proto.GeometryPrecision);
        Assert.Equal(0.001, proto.MaxAllowableOffset);
    }

    [Fact]
    public void FromProtoResponse_StandardFeatures_MapsCorrectly()
    {
        var feature = new Proto.Feature { Id = 42 };
        feature.Attributes.Add("name", new Proto.AttributeValue { StringValue = "test" });
        feature.Geometry = new Proto.Geometry
        {
            Point = new Proto.PointGeometry { X = 1.0, Y = 2.0 },
        };

        var protoResponse = new Proto.QueryFeaturesResponse
        {
            ObjectIdFieldName = "OBJECTID",
            GeometryType = Proto.GeometryType.Point,
            SpatialReference = new Proto.SpatialReference { Wkid = 4326 },
            ExceededTransferLimit = true,
        };
        protoResponse.Fields.Add(new Proto.FieldDefinition
        {
            Name = "name",
            FieldType = Proto.FieldType.String,
            Length = 255,
            Nullable = true,
        });
        protoResponse.Features.Add(feature);

        var result = ProtoAdapter.FromProtoResponse(protoResponse);

        Assert.Equal("OBJECTID", result.ObjectIdFieldName);
        Assert.Equal(Models.GeometryType.Point, result.GeometryType);
        Assert.NotNull(result.SpatialReference);
        Assert.Equal(4326, result.SpatialReference.Wkid);
        Assert.True(result.ExceededTransferLimit);
        Assert.Single(result.Fields);
        Assert.Equal("name", result.Fields[0].Name);
        Assert.Equal(Models.FieldType.String, result.Fields[0].FieldType);
        Assert.Equal(255, result.Fields[0].Length);
        Assert.True(result.Fields[0].Nullable);
        Assert.Single(result.Features);
        Assert.Equal(42L, result.Features[0].Id);
        Assert.Equal("test", result.Features[0].Attributes["name"]);
        Assert.NotNull(result.Features[0].Geometry);
        Assert.Equal(1.0, result.Features[0].Geometry!["x"]);
        Assert.Equal(2.0, result.Features[0].Geometry!["y"]);
    }

    [Fact]
    public void FromProtoResponse_CountOnly_MapsCorrectly()
    {
        var protoResponse = new Proto.QueryFeaturesResponse
        {
            Count = 1234,
        };

        var result = ProtoAdapter.FromProtoResponse(protoResponse);

        Assert.Equal(1234L, result.Count);
        Assert.Empty(result.Features);
    }

    [Fact]
    public void FromProtoResponse_ObjectIdsOnly_MapsCorrectly()
    {
        var protoResponse = new Proto.QueryFeaturesResponse
        {
            ObjectIdFieldName = "OBJECTID",
        };
        protoResponse.ObjectIds.AddRange([10L, 20L, 30L]);

        var result = ProtoAdapter.FromProtoResponse(protoResponse);

        Assert.Equal("OBJECTID", result.ObjectIdFieldName);
        Assert.Equal([10L, 20L, 30L], result.ObjectIds);
        Assert.Empty(result.Features);
    }

    [Fact]
    public void FromProtoResponse_ExtentOnly_MapsCorrectly()
    {
        var protoResponse = new Proto.QueryFeaturesResponse
        {
            Extent = new Proto.Extent
            {
                Xmin = -180.0,
                Ymin = -90.0,
                Xmax = 180.0,
                Ymax = 90.0,
                SpatialReference = new Proto.SpatialReference { Wkid = 4326 },
            },
        };

        var result = ProtoAdapter.FromProtoResponse(protoResponse);

        Assert.NotNull(result.Extent);
        Assert.Equal(-180.0, result.Extent.Xmin);
        Assert.Equal(-90.0, result.Extent.Ymin);
        Assert.Equal(180.0, result.Extent.Xmax);
        Assert.Equal(90.0, result.Extent.Ymax);
        Assert.NotNull(result.Extent.SpatialReference);
        Assert.Equal(4326, result.Extent.SpatialReference.Wkid);
    }

    [Fact]
    public void FromProtoPage_MapsCorrectly()
    {
        var feature = new Proto.Feature { Id = 1 };
        feature.Attributes.Add("val", new Proto.AttributeValue { Int32Value = 99 });

        var protoPage = new Proto.FeaturePage
        {
            ObjectIdFieldName = "FID",
            GeometryType = Proto.GeometryType.Polygon,
            SpatialReference = new Proto.SpatialReference { Wkid = 3857 },
            IsLastPage = true,
        };
        protoPage.Fields.Add(new Proto.FieldDefinition
        {
            Name = "val",
            FieldType = Proto.FieldType.Integer,
        });
        protoPage.Features.Add(feature);

        var result = ProtoAdapter.FromProtoPage(protoPage);

        Assert.Equal("FID", result.ObjectIdFieldName);
        Assert.Equal(Models.GeometryType.Polygon, result.GeometryType);
        Assert.NotNull(result.SpatialReference);
        Assert.Equal(3857, result.SpatialReference.Wkid);
        Assert.True(result.IsLastPage);
        Assert.Single(result.Fields);
        Assert.Equal("val", result.Fields[0].Name);
        Assert.Single(result.Features);
        Assert.Equal(1L, result.Features[0].Id);
        Assert.Equal(99, result.Features[0].Attributes["val"]);
    }

    [Fact]
    public void ConvertAttribute_StringValue_ReturnsString()
    {
        var attr = new Proto.AttributeValue { StringValue = "hello" };
        var result = ProtoAdapter.ConvertAttribute(attr);
        Assert.Equal("hello", result);
    }

    [Fact]
    public void ConvertAttribute_Int32Value_ReturnsInt()
    {
        var attr = new Proto.AttributeValue { Int32Value = 42 };
        var result = ProtoAdapter.ConvertAttribute(attr);
        Assert.Equal(42, result);
    }

    [Fact]
    public void ConvertAttribute_Int64Value_ReturnsLong()
    {
        var attr = new Proto.AttributeValue { Int64Value = 9999999999L };
        var result = ProtoAdapter.ConvertAttribute(attr);
        Assert.Equal(9999999999L, result);
    }

    [Fact]
    public void ConvertAttribute_DoubleValue_ReturnsDouble()
    {
        var attr = new Proto.AttributeValue { DoubleValue = 3.14 };
        var result = ProtoAdapter.ConvertAttribute(attr);
        Assert.Equal(3.14, result);
    }

    [Fact]
    public void ConvertAttribute_FloatValue_ReturnsCastToDouble()
    {
        var attr = new Proto.AttributeValue { FloatValue = 2.5f };
        var result = ProtoAdapter.ConvertAttribute(attr);
        Assert.IsType<double>(result);
        Assert.Equal((double)2.5f, (double)result!);
    }

    [Fact]
    public void ConvertAttribute_BoolValue_ReturnsBool()
    {
        var attr = new Proto.AttributeValue { BoolValue = true };
        var result = ProtoAdapter.ConvertAttribute(attr);
        Assert.Equal(true, result);
    }

    [Fact]
    public void ConvertAttribute_DatetimeValue_ReturnsLong()
    {
        var attr = new Proto.AttributeValue { DatetimeValue = 1700000000000L };
        var result = ProtoAdapter.ConvertAttribute(attr);
        Assert.Equal(1700000000000L, result);
    }

    [Fact]
    public void ConvertAttribute_NullValue_ReturnsNull()
    {
        var attr = new Proto.AttributeValue { NullValue = Proto.NullValue.NullValue };
        var result = ProtoAdapter.ConvertAttribute(attr);
        Assert.Null(result);
    }

    [Fact]
    public void ConvertAttribute_BytesValue_ReturnsNull()
    {
        var attr = new Proto.AttributeValue { BytesValue = Google.Protobuf.ByteString.CopyFrom([1, 2, 3]) };
        var result = ProtoAdapter.ConvertAttribute(attr);
        Assert.Null(result);
    }

    [Fact]
    public void ConvertGeometry_Point_ReturnsEsriJson()
    {
        var geom = new Proto.Geometry
        {
            Point = new Proto.PointGeometry { X = 10.5, Y = 20.3 },
        };

        var result = ProtoAdapter.ConvertGeometry(geom);

        Assert.NotNull(result);
        Assert.Equal(10.5, result["x"]);
        Assert.Equal(20.3, result["y"]);
        Assert.False(result.ContainsKey("z"));
    }

    [Fact]
    public void ConvertGeometry_PointWithZ_IncludesZ()
    {
        var geom = new Proto.Geometry
        {
            Point = new Proto.PointGeometry { X = 1.0, Y = 2.0, Z = 3.0 },
        };

        var result = ProtoAdapter.ConvertGeometry(geom);

        Assert.NotNull(result);
        Assert.Equal(1.0, result["x"]);
        Assert.Equal(2.0, result["y"]);
        Assert.Equal(3.0, result["z"]);
    }

    [Fact]
    public void ConvertGeometry_Polyline_ReturnsPaths()
    {
        var path = new Proto.CoordinateSequence();
        path.Coords.Add(new Proto.Coordinate { X = 0, Y = 0 });
        path.Coords.Add(new Proto.Coordinate { X = 1, Y = 1 });

        var geom = new Proto.Geometry
        {
            Polyline = new Proto.PolylineGeometry(),
        };
        geom.Polyline.Paths.Add(path);

        var result = ProtoAdapter.ConvertGeometry(geom);

        Assert.NotNull(result);
        Assert.True(result.ContainsKey("paths"));
        var paths = (List<object?>)result["paths"]!;
        Assert.Single(paths);
        var coords = (List<object?>)paths[0]!;
        Assert.Equal(2, coords.Count);
        var firstCoord = (List<object?>)coords[0]!;
        Assert.Equal(0.0, firstCoord[0]);
        Assert.Equal(0.0, firstCoord[1]);
    }

    [Fact]
    public void ConvertGeometry_Polygon_ReturnsRings()
    {
        var ring = new Proto.CoordinateSequence();
        ring.Coords.Add(new Proto.Coordinate { X = 0, Y = 0 });
        ring.Coords.Add(new Proto.Coordinate { X = 10, Y = 0 });
        ring.Coords.Add(new Proto.Coordinate { X = 10, Y = 10 });
        ring.Coords.Add(new Proto.Coordinate { X = 0, Y = 0 });

        var geom = new Proto.Geometry
        {
            Polygon = new Proto.PolygonGeometry(),
        };
        geom.Polygon.Rings.Add(ring);

        var result = ProtoAdapter.ConvertGeometry(geom);

        Assert.NotNull(result);
        Assert.True(result.ContainsKey("rings"));
        var rings = (List<object?>)result["rings"]!;
        Assert.Single(rings);
        var coords = (List<object?>)rings[0]!;
        Assert.Equal(4, coords.Count);
    }

    [Fact]
    public void ConvertGeometry_MultiPolygon_FlattensRings()
    {
        var ring1 = new Proto.CoordinateSequence();
        ring1.Coords.Add(new Proto.Coordinate { X = 0, Y = 0 });
        ring1.Coords.Add(new Proto.Coordinate { X = 1, Y = 0 });
        ring1.Coords.Add(new Proto.Coordinate { X = 0, Y = 0 });

        var ring2 = new Proto.CoordinateSequence();
        ring2.Coords.Add(new Proto.Coordinate { X = 10, Y = 10 });
        ring2.Coords.Add(new Proto.Coordinate { X = 11, Y = 10 });
        ring2.Coords.Add(new Proto.Coordinate { X = 10, Y = 10 });

        var poly1 = new Proto.PolygonGeometry();
        poly1.Rings.Add(ring1);
        var poly2 = new Proto.PolygonGeometry();
        poly2.Rings.Add(ring2);

        var geom = new Proto.Geometry
        {
            MultiPolygon = new Proto.MultiPolygonGeometry(),
        };
        geom.MultiPolygon.Polygons.Add(poly1);
        geom.MultiPolygon.Polygons.Add(poly2);

        var result = ProtoAdapter.ConvertGeometry(geom);

        Assert.NotNull(result);
        Assert.True(result.ContainsKey("rings"));
        var rings = (List<object?>)result["rings"]!;
        // Both polygons' rings are flattened into a single rings array
        Assert.Equal(2, rings.Count);
    }

    [Fact]
    public void ConvertGeometry_MultiPoint_ReturnsPoints()
    {
        var geom = new Proto.Geometry
        {
            MultiPoint = new Proto.MultiPointGeometry(),
        };
        geom.MultiPoint.Points.Add(new Proto.PointGeometry { X = 1, Y = 2 });
        geom.MultiPoint.Points.Add(new Proto.PointGeometry { X = 3, Y = 4 });

        var result = ProtoAdapter.ConvertGeometry(geom);

        Assert.NotNull(result);
        Assert.True(result.ContainsKey("points"));
        var points = (List<object?>)result["points"]!;
        Assert.Equal(2, points.Count);
        var first = (List<object?>)points[0]!;
        Assert.Equal(1.0, first[0]);
        Assert.Equal(2.0, first[1]);
    }

    [Fact]
    public void ConvertGeometry_None_ReturnsNull()
    {
        var geom = new Proto.Geometry();
        var result = ProtoAdapter.ConvertGeometry(geom);
        Assert.Null(result);
    }

    [Fact]
    public void FromProtoResponse_NullSpatialReference_ReturnsNull()
    {
        var protoResponse = new Proto.QueryFeaturesResponse
        {
            ObjectIdFieldName = "OBJECTID",
            GeometryType = Proto.GeometryType.Point,
        };

        var result = ProtoAdapter.FromProtoResponse(protoResponse);

        Assert.Null(result.SpatialReference);
    }

    [Fact]
    public void FromProtoResponse_NullExtent_ReturnsNull()
    {
        var protoResponse = new Proto.QueryFeaturesResponse();

        var result = ProtoAdapter.FromProtoResponse(protoResponse);

        Assert.Null(result.Extent);
    }

    [Fact]
    public void ConvertFeature_NullGeometry_ReturnsNullGeometry()
    {
        var feature = new Proto.Feature { Id = 1 };
        feature.Attributes.Add("key", new Proto.AttributeValue { StringValue = "val" });

        var result = ProtoAdapter.ConvertFeature(feature);

        Assert.Equal(1L, result.Id);
        Assert.Equal("val", result.Attributes["key"]);
        Assert.Null(result.Geometry);
    }

    [Fact]
    public void FromProtoPage_NullSpatialReference_ReturnsNull()
    {
        var protoPage = new Proto.FeaturePage
        {
            ObjectIdFieldName = "FID",
            IsLastPage = false,
        };

        var result = ProtoAdapter.FromProtoPage(protoPage);

        Assert.Null(result.SpatialReference);
        Assert.False(result.IsLastPage);
    }
}

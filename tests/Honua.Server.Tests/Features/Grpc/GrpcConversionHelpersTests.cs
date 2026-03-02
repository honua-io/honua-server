// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Grpc;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using DomainGeometryType = Honua.Core.Features.Catalog.Domain.GeometryType;
using Proto = Honua.Server.Features.Grpc.Proto;

namespace Honua.Server.Tests.Features.Grpc;

[Protocol(Protocols.Grpc)]
[Operation(Operations.Query)]
public sealed class GrpcConversionHelpersTests
{
    private static readonly WKBWriter WkbWriter = new();

    // ── ToFeatureQuery ──────────────────────────────────────────

    [UnitTest]
    public void ToFeatureQuery_EmptyRequest_ReturnsDefaultQuery()
    {
        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0
        };

        var query = GrpcConversionHelpers.ToFeatureQuery(request);

        query.Where.Should().BeNull();
        query.ObjectIds.Should().BeNull();
        query.OutFields.Should().BeNull();
        query.Offset.Should().BeNull();
        query.Limit.Should().BeNull();
        query.Distinct.Should().BeFalse();
        query.SpatialFilter.Should().BeNull();
        query.OrderBy.Should().BeNull();
        query.OutStatistics.Should().BeNull();
        query.GroupByFields.Should().BeNull();
    }

    [UnitTest]
    public void ToFeatureQuery_WithWhereClause_SetsWhere()
    {
        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            Where = "OBJECTID > 5"
        };

        var query = GrpcConversionHelpers.ToFeatureQuery(request);

        query.Where.Should().Be("OBJECTID > 5");
    }

    [UnitTest]
    public void ToFeatureQuery_WithPagination_SetsOffsetAndLimit()
    {
        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            ResultOffset = 10,
            ResultRecordCount = 50
        };

        var query = GrpcConversionHelpers.ToFeatureQuery(request);

        query.Offset.Should().Be(10);
        query.Limit.Should().Be(50);
    }

    [UnitTest]
    public void ToFeatureQuery_WithObjectIds_SetsObjectIds()
    {
        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
        };
        request.ObjectIds.AddRange(new long[] { 1, 2, 3 });

        var query = GrpcConversionHelpers.ToFeatureQuery(request);

        query.ObjectIds.Should().NotBeNull();
        query.ObjectIds!.Value.Should().BeEquivalentTo(new long[] { 1, 2, 3 });
    }

    [UnitTest]
    public void ToFeatureQuery_WithOrderBy_ParsesMultipleClauses()
    {
        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            OrderBy = "name ASC, population DESC"
        };

        var query = GrpcConversionHelpers.ToFeatureQuery(request);

        query.OrderBy.Should().NotBeNull();
        query.OrderBy!.Value.Should().HaveCount(2);
        query.OrderBy.Value[0].Field.Should().Be("name");
        query.OrderBy.Value[0].Ascending.Should().BeTrue();
        query.OrderBy.Value[1].Field.Should().Be("population");
        query.OrderBy.Value[1].Ascending.Should().BeFalse();
    }

    [UnitTest]
    public void ToFeatureQuery_WithStatistics_SetsOutStatistics()
    {
        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
        };
        request.OutStatistics.Add(new Proto.StatisticDefinition
        {
            OnStatisticField = "population",
            StatisticType = Proto.StatisticType.Sum,
            OutStatisticFieldName = "total_pop"
        });
        request.GroupBy.Add("state");

        var query = GrpcConversionHelpers.ToFeatureQuery(request);

        query.OutStatistics.Should().NotBeNull();
        query.OutStatistics!.Value.Should().HaveCount(1);
        query.OutStatistics.Value[0].OnStatisticField.Should().Be("population");
        query.OutStatistics.Value[0].StatisticType.Should().Be(StatisticType.Sum);
        query.OutStatistics.Value[0].OutStatisticFieldName.Should().Be("total_pop");
        query.GroupByFields.Should().NotBeNull();
        query.GroupByFields!.Value.Should().ContainSingle().Which.Should().Be("state");
    }

    // ── ToProtoFeature ──────────────────────────────────────────

    [UnitTest]
    public void ToProtoFeature_WithStringAttribute_MapsCorrectly()
    {
        var feature = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty
            .Add("name", "Test"));

        var proto = GrpcConversionHelpers.ToProtoFeature(feature);

        proto.Id.Should().Be(1);
        proto.Attributes.Should().ContainKey("name");
        proto.Attributes["name"].StringValue.Should().Be("Test");
    }

    [UnitTest]
    public void ToProtoFeature_WithIntAttribute_MapsToInt32()
    {
        var feature = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty
            .Add("count", 42));

        var proto = GrpcConversionHelpers.ToProtoFeature(feature);

        proto.Attributes["count"].Int32Value.Should().Be(42);
    }

    [UnitTest]
    public void ToProtoFeature_WithLongAttribute_MapsToInt64()
    {
        var feature = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty
            .Add("bignum", 9999999999L));

        var proto = GrpcConversionHelpers.ToProtoFeature(feature);

        proto.Attributes["bignum"].Int64Value.Should().Be(9999999999L);
    }

    [UnitTest]
    public void ToProtoFeature_WithDoubleAttribute_MapsToDouble()
    {
        var feature = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty
            .Add("area", 3.14));

        var proto = GrpcConversionHelpers.ToProtoFeature(feature);

        proto.Attributes["area"].DoubleValue.Should().Be(3.14);
    }

    [UnitTest]
    public void ToProtoFeature_WithBoolAttribute_MapsToBool()
    {
        var feature = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty
            .Add("active", true));

        var proto = GrpcConversionHelpers.ToProtoFeature(feature);

        proto.Attributes["active"].BoolValue.Should().BeTrue();
    }

    [UnitTest]
    public void ToProtoFeature_WithNullAttribute_MapsToNullValue()
    {
        var feature = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty
            .Add("missing", null));

        var proto = GrpcConversionHelpers.ToProtoFeature(feature);

        proto.Attributes["missing"].NullValue.Should().Be(Proto.NullValue.NullValue);
    }

    [UnitTest]
    public void ToProtoFeature_WithDateTimeAttribute_MapsToMillis()
    {
        var dt = new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var feature = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty
            .Add("created", dt));

        var proto = GrpcConversionHelpers.ToProtoFeature(feature);

        var expected = new DateTimeOffset(dt, TimeSpan.Zero).ToUnixTimeMilliseconds();
        proto.Attributes["created"].DatetimeValue.Should().Be(expected);
    }

    [UnitTest]
    public void ToProtoFeature_WithPointGeometry_MapsToPointGeometry()
    {
        var point = new Point(10.5, 20.3);
        var wkb = WkbWriter.Write(point);
        var feature = Feature.Create(1, wkb);

        var proto = GrpcConversionHelpers.ToProtoFeature(feature);

        proto.Geometry.Should().NotBeNull();
        proto.Geometry.Point.Should().NotBeNull();
        proto.Geometry.Point.X.Should().Be(10.5);
        proto.Geometry.Point.Y.Should().Be(20.3);
    }

    [UnitTest]
    public void ToProtoFeature_WithPolygonGeometry_MapsRings()
    {
        var factory = new GeometryFactory();
        var shell = factory.CreateLinearRing(new[]
        {
            new Coordinate(0, 0), new Coordinate(10, 0),
            new Coordinate(10, 10), new Coordinate(0, 10), new Coordinate(0, 0)
        });
        var polygon = factory.CreatePolygon(shell);
        var wkb = WkbWriter.Write(polygon);
        var feature = Feature.Create(1, wkb);

        var proto = GrpcConversionHelpers.ToProtoFeature(feature);

        proto.Geometry.Polygon.Should().NotBeNull();
        proto.Geometry.Polygon.Rings.Should().HaveCount(1);
        proto.Geometry.Polygon.Rings[0].Coords.Should().HaveCount(5);
    }

    [UnitTest]
    public void ToProtoFeature_WithLineStringGeometry_MapsToPolyline()
    {
        var factory = new GeometryFactory();
        var line = factory.CreateLineString(new[]
        {
            new Coordinate(0, 0), new Coordinate(5, 5), new Coordinate(10, 0)
        });
        var wkb = WkbWriter.Write(line);
        var feature = Feature.Create(1, wkb);

        var proto = GrpcConversionHelpers.ToProtoFeature(feature);

        proto.Geometry.Polyline.Should().NotBeNull();
        proto.Geometry.Polyline.Paths.Should().HaveCount(1);
        proto.Geometry.Polyline.Paths[0].Coords.Should().HaveCount(3);
    }

    [UnitTest]
    public void ToProtoFeature_WithMultiPolygonGeometry_MapsToMultiPolygon()
    {
        var factory = new GeometryFactory();
        var shell1 = factory.CreateLinearRing(new[]
        {
            new Coordinate(0, 0), new Coordinate(10, 0),
            new Coordinate(10, 10), new Coordinate(0, 10), new Coordinate(0, 0)
        });
        var shell2 = factory.CreateLinearRing(new[]
        {
            new Coordinate(20, 20), new Coordinate(30, 20),
            new Coordinate(30, 30), new Coordinate(20, 30), new Coordinate(20, 20)
        });
        var poly1 = factory.CreatePolygon(shell1);
        var poly2 = factory.CreatePolygon(shell2);
        var multiPolygon = factory.CreateMultiPolygon(new[] { poly1, poly2 });
        var wkb = WkbWriter.Write(multiPolygon);
        var feature = Feature.Create(1, wkb);

        var proto = GrpcConversionHelpers.ToProtoFeature(feature);

        proto.Geometry.Should().NotBeNull();
        proto.Geometry.MultiPolygon.Should().NotBeNull();
        proto.Geometry.MultiPolygon.Polygons.Should().HaveCount(2);
        proto.Geometry.MultiPolygon.Polygons[0].Rings.Should().HaveCount(1);
        proto.Geometry.MultiPolygon.Polygons[0].Rings[0].Coords.Should().HaveCount(5);
        proto.Geometry.MultiPolygon.Polygons[1].Rings.Should().HaveCount(1);
        proto.Geometry.MultiPolygon.Polygons[1].Rings[0].Coords.Should().HaveCount(5);
    }

    [UnitTest]
    public void ToProtoFeature_WithMultiPolygonWithHoles_PreservesHolesPerPolygon()
    {
        var factory = new GeometryFactory();
        var shell = factory.CreateLinearRing(new[]
        {
            new Coordinate(0, 0), new Coordinate(100, 0),
            new Coordinate(100, 100), new Coordinate(0, 100), new Coordinate(0, 0)
        });
        var hole = factory.CreateLinearRing(new[]
        {
            new Coordinate(10, 10), new Coordinate(20, 10),
            new Coordinate(20, 20), new Coordinate(10, 20), new Coordinate(10, 10)
        });
        var polyWithHole = factory.CreatePolygon(shell, new[] { hole });

        var shell2 = factory.CreateLinearRing(new[]
        {
            new Coordinate(200, 200), new Coordinate(300, 200),
            new Coordinate(300, 300), new Coordinate(200, 300), new Coordinate(200, 200)
        });
        var simplePolygon = factory.CreatePolygon(shell2);

        var multiPolygon = factory.CreateMultiPolygon(new[] { polyWithHole, simplePolygon });
        var wkb = WkbWriter.Write(multiPolygon);
        var feature = Feature.Create(1, wkb);

        var proto = GrpcConversionHelpers.ToProtoFeature(feature);

        proto.Geometry.MultiPolygon.Polygons.Should().HaveCount(2);
        // First polygon has exterior + 1 hole = 2 rings
        proto.Geometry.MultiPolygon.Polygons[0].Rings.Should().HaveCount(2);
        // Second polygon has exterior only = 1 ring
        proto.Geometry.MultiPolygon.Polygons[1].Rings.Should().HaveCount(1);
    }

    [UnitTest]
    public void ToProtoFeature_WithGeometryCollection_Throws()
    {
        var factory = new GeometryFactory();
        var point = factory.CreatePoint(new Coordinate(1, 2));
        var line = factory.CreateLineString(new[] { new Coordinate(0, 0), new Coordinate(1, 1) });
        var collection = factory.CreateGeometryCollection(new Geometry[] { point, line });
        var wkb = WkbWriter.Write(collection);
        var feature = Feature.Create(1, wkb);

        var act = () => GrpcConversionHelpers.ToProtoFeature(feature);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*GeometryCollection*not representable*");
    }

    [UnitTest]
    public void ToProtoFeature_WithNullGeometry_HasNoGeometry()
    {
        var feature = Feature.Create(1, null);

        var proto = GrpcConversionHelpers.ToProtoFeature(feature);

        proto.Geometry.Should().BeNull();
    }

    // ── Enum mappings ───────────────────────────────────────────

    [UnitTest]
    public void ToProtoGeometryType_AllValues_MapCorrectly()
    {
        GrpcConversionHelpers.ToProtoGeometryType(DomainGeometryType.Point)
            .Should().Be(Proto.GeometryType.Point);
        GrpcConversionHelpers.ToProtoGeometryType(DomainGeometryType.MultiPoint)
            .Should().Be(Proto.GeometryType.MultiPoint);
        GrpcConversionHelpers.ToProtoGeometryType(DomainGeometryType.LineString)
            .Should().Be(Proto.GeometryType.LineString);
        GrpcConversionHelpers.ToProtoGeometryType(DomainGeometryType.MultiLineString)
            .Should().Be(Proto.GeometryType.MultiLineString);
        GrpcConversionHelpers.ToProtoGeometryType(DomainGeometryType.Polygon)
            .Should().Be(Proto.GeometryType.Polygon);
        GrpcConversionHelpers.ToProtoGeometryType(DomainGeometryType.MultiPolygon)
            .Should().Be(Proto.GeometryType.MultiPolygon);
        GrpcConversionHelpers.ToProtoGeometryType(DomainGeometryType.None)
            .Should().Be(Proto.GeometryType.None);
    }

    [UnitTest]
    public void ToProtoField_MapsFieldProperties()
    {
        var field = new FieldDefinition("name", FieldType.String, Length: 255, Nullable: true);

        var proto = GrpcConversionHelpers.ToProtoField(field);

        proto.Name.Should().Be("name");
        proto.FieldType.Should().Be(Proto.FieldType.String);
        proto.Length.Should().Be(255);
        proto.Nullable.Should().BeTrue();
    }

    [UnitTest]
    public void ToProtoSpatialReference_MapsWkidAndWkt()
    {
        var sr = SpatialReference.Create(4326, 4326, null, null, "GEOGCS[\"WGS 84\"]");

        var proto = GrpcConversionHelpers.ToProtoSpatialReference(sr);

        proto.Wkid.Should().Be(4326);
        proto.LatestWkid.Should().Be(4326);
        proto.Wkt.Should().Be("GEOGCS[\"WGS 84\"]");
    }

    [UnitTest]
    public void ToProtoExtent_MapsCoordinatesAndSr()
    {
        var extent = FeatureExtent.Create(-180, -90, 180, 90, 4326);
        var sr = SpatialReference.Create(4326);

        var proto = GrpcConversionHelpers.ToProtoExtent(extent, sr);

        proto.Xmin.Should().Be(-180);
        proto.Ymin.Should().Be(-90);
        proto.Xmax.Should().Be(180);
        proto.Ymax.Should().Be(90);
        proto.SpatialReference.Wkid.Should().Be(4326);
    }

    // ── SpatialFilter round-trip ────────────────────────────────

    [UnitTest]
    public void ToFeatureQuery_WithSpatialFilter_ConvertsSpatialRelationship()
    {
        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            SpatialFilter = new Proto.SpatialFilter
            {
                Geometry = new Proto.Geometry
                {
                    Point = new Proto.PointGeometry { X = 10, Y = 20 }
                },
                SpatialRelationship = Proto.SpatialRelationship.Within,
                SpatialReference = new Proto.SpatialReference { Wkid = 4326 }
            }
        };

        var query = GrpcConversionHelpers.ToFeatureQuery(request);

        query.SpatialFilter.Should().NotBeNull();
        query.SpatialFilter!.Value.SpatialRelationship.Should().Be(SpatialRelationship.Within);
        query.SpatialFilter.Value.Srid.Should().Be(4326);
        query.SpatialFilter.Value.Geometry.Should().NotBeEmpty();
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Protocols.Grpc;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using DomainGeometryType = Honua.Core.Features.Catalog.Domain.GeometryType;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

[Protocol(TestProtocols.Grpc)]
[Operation(Operations.Query)]
public sealed class GrpcConversionHelpersTests
{
    private static readonly WKBWriter _wkbWriter = new();

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
    public void ToFeatureQuery_WithOutFieldsWildcard_TreatsAsAllFields()
    {
        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
        };
        request.OutFields.Add("*");

        var query = GrpcConversionHelpers.ToFeatureQuery(request);

        query.OutFields.Should().BeNull();
    }

    [UnitTest]
    public void ToFeatureQuery_WithCommaSeparatedOutFields_SetsOutFields()
    {
        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
        };
        request.OutFields.Add("name, population");

        var query = GrpcConversionHelpers.ToFeatureQuery(request);

        query.OutFields.Should().NotBeNull();
        query.OutFields!.Value.Should().Equal("name", "population");
    }

    [UnitTest]
    public void ToFeatureQuery_WithOutSr_SetsOutputSrid()
    {
        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            OutSr = new Proto.SpatialReference { Wkid = 3857 }
        };

        var query = GrpcConversionHelpers.ToFeatureQuery(request);

        query.OutputSrid.Should().Be(3857);
    }

    [UnitTest]
    public void ToFeatureQuery_WithOutSrLatestWkid_SetsOutputSrid()
    {
        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            OutSr = new Proto.SpatialReference { LatestWkid = 4326 }
        };

        var query = GrpcConversionHelpers.ToFeatureQuery(request);

        query.OutputSrid.Should().Be(4326);
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

    // ── ApplyEdits conversion ───────────────────────────────────

    [UnitTest]
    [Operation(Operations.ApplyEdits)]
    public void ToFeatureEditBatch_WithAddsUpdatesAndDeletes_MapsDomainBatch()
    {
        var request = new Proto.ApplyEditsRequest
        {
            ServiceId = "test",
            LayerId = 0,
            RollbackOnFailure = true
        };

        request.Adds.Add(new Proto.Feature
        {
            Attributes =
            {
                ["name"] = new Proto.AttributeValue { StringValue = "created" }
            },
            Geometry = new Proto.Geometry
            {
                Point = new Proto.PointGeometry { X = -157.86, Y = 21.31 }
            }
        });

        request.Updates.Add(new Proto.Feature
        {
            Attributes =
            {
                ["objectId"] = new Proto.AttributeValue { Int64Value = 42 },
                ["name"] = new Proto.AttributeValue { StringValue = "updated" }
            }
        });

        request.Deletes.Add(99);

        var batch = GrpcConversionHelpers.ToFeatureEditBatch(request);

        batch.RollbackOnFailure.Should().BeTrue();
        batch.Creates.Should().ContainSingle();
        batch.Updates.Should().ContainSingle();
        batch.Deletes.Should().BeEquivalentTo(new long[] { 99 });
        batch.Creates[0].Attributes["name"].Should().Be("created");
        batch.Creates[0].Geometry.Should().NotBeNullOrEmpty();
        batch.Updates[0].Id.Should().Be(42);
        batch.Updates[0].Attributes["name"].Should().Be("updated");
    }

    [UnitTest]
    [Operation(Operations.ApplyEdits)]
    public void ToFeatureEditBatch_UpdateWithoutIdOrObjectId_ThrowsArgumentException()
    {
        var request = new Proto.ApplyEditsRequest
        {
            ServiceId = "test",
            LayerId = 0
        };
        request.Updates.Add(new Proto.Feature
        {
            Attributes =
            {
                ["name"] = new Proto.AttributeValue { StringValue = "missing id" }
            }
        });

        var act = () => GrpcConversionHelpers.ToFeatureEditBatch(request);

        act.Should()
            .Throw<ArgumentException>()
            .WithMessage("*positive id*");
    }

    [UnitTest]
    [Operation(Operations.ApplyEdits)]
    public void ToProtoApplyEditsResponse_MapsOperationResultsAndRollbackError()
    {
        var result = FeatureEditResult.Success(
            createdCount: 1,
            updatedCount: 0,
            deletedCount: 1,
            createResults: ImmutableArray.Create(EditOperationResult.Success(101)),
            updateResults: ImmutableArray.Create(EditOperationResult.Failure("update failed", 1005, 55)),
            deleteResults: ImmutableArray.Create(EditOperationResult.Success(77)),
            wasRolledBack: true);

        var response = GrpcConversionHelpers.ToProtoApplyEditsResponse(result);

        response.AddResults.Should().ContainSingle();
        response.UpdateResults.Should().ContainSingle();
        response.DeleteResults.Should().ContainSingle();
        response.AddResults[0].Success.Should().BeTrue();
        response.AddResults[0].ObjectId.Should().Be(101);
        response.UpdateResults[0].Success.Should().BeFalse();
        response.UpdateResults[0].Error.Code.Should().Be(1005);
        response.UpdateResults[0].Error.Message.Should().Be("update failed");
        response.Error.Should().NotBeNull();
        response.Error.Code.Should().Be(1000);
        response.Error.Message.Should().Be("Operation rolled back.");
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
        var wkb = _wkbWriter.Write(point);
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
        var wkb = _wkbWriter.Write(polygon);
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
        var wkb = _wkbWriter.Write(line);
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
        var wkb = _wkbWriter.Write(multiPolygon);
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
        var wkb = _wkbWriter.Write(multiPolygon);
        var feature = Feature.Create(1, wkb);

        var proto = GrpcConversionHelpers.ToProtoFeature(feature);

        proto.Geometry.MultiPolygon.Polygons.Should().HaveCount(2);
        // First polygon has exterior + 1 hole = 2 rings
        proto.Geometry.MultiPolygon.Polygons[0].Rings.Should().HaveCount(2);
        // Second polygon has exterior only = 1 ring
        proto.Geometry.MultiPolygon.Polygons[1].Rings.Should().HaveCount(1);
    }

    [UnitTest]
    public void ToProtoFeature_WithMixedGeometryCollection_OmitsGeometryInsteadOfThrowing()
    {
        var factory = new GeometryFactory();
        var point = factory.CreatePoint(new Coordinate(1, 2));
        var line = factory.CreateLineString(new[] { new Coordinate(0, 0), new Coordinate(1, 1) });
        var collection = factory.CreateGeometryCollection(new Geometry[] { point, line });
        var wkb = _wkbWriter.Write(collection);
        var feature = Feature.Create(1, wkb);

        var proto = GrpcConversionHelpers.ToProtoFeature(feature);
        proto.Geometry.Should().BeNull();
    }

    [UnitTest]
    public void ToProtoFeature_WithPointGeometryCollection_ProjectsToMultiPoint()
    {
        var factory = new GeometryFactory();
        var p1 = factory.CreatePoint(new Coordinate(1, 2));
        var p2 = factory.CreatePoint(new Coordinate(3, 4));
        var collection = factory.CreateGeometryCollection(new Geometry[] { p1, p2 });
        var wkb = _wkbWriter.Write(collection);
        var feature = Feature.Create(1, wkb);

        var proto = GrpcConversionHelpers.ToProtoFeature(feature);

        proto.Geometry.Should().NotBeNull();
        proto.Geometry.MultiPoint.Should().NotBeNull();
        proto.Geometry.MultiPoint.Points.Should().HaveCount(2);
    }

    [UnitTest]
    public void ToProtoFeature_WithNullGeometry_HasNoGeometry()
    {
        var feature = Feature.Create(1, null);

        var proto = GrpcConversionHelpers.ToProtoFeature(feature);

        proto.Geometry.Should().BeNull();
    }

    [UnitTest]
    public void ToProtoFeature_IncludeGeometryFalse_OmitsGeometry()
    {
        var point = new Point(10.5, 20.3);
        var wkb = _wkbWriter.Write(point);
        var feature = Feature.Create(1, wkb);

        var proto = GrpcConversionHelpers.ToProtoFeature(feature, includeGeometry: false);

        proto.Geometry.Should().BeNull();
    }

    [UnitTest]
    public void CreateEffectiveGeometryLimits_UsesRequestOverrides()
    {
        var baseLimits = new GeometryLimits
        {
            MaxCoordinatePrecision = 8,
            SimplifyTolerance = null,
            MaxVerticesPerGeometry = 1000,
            MaxGeometrySize = 1_000_000
        };

        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            GeometryPrecision = 3,
            MaxAllowableOffset = 2.5
        };

        var effective = GrpcConversionHelpers.CreateEffectiveGeometryLimits(baseLimits, request);

        effective.MaxCoordinatePrecision.Should().Be(3);
        effective.SimplifyTolerance.Should().Be(2.5);
        effective.MaxVerticesPerGeometry.Should().Be(0);
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

    [UnitTest]
    public void ToFeatureQuery_WithSpatialFilterLatestWkid_UsesLatestWkidAsFallbackSrid()
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
                SpatialReference = new Proto.SpatialReference { LatestWkid = 4326 }
            }
        };

        var query = GrpcConversionHelpers.ToFeatureQuery(request);

        query.SpatialFilter.Should().NotBeNull();
        query.SpatialFilter!.Value.Srid.Should().Be(4326);
    }

    [UnitTest]
    public void ToFeatureQuery_WithMultiPolygonSpatialFilter_ConvertsGeometry()
    {
        var poly1 = new Proto.PolygonGeometry();
        poly1.Rings.Add(new Proto.CoordinateSequence
        {
            Coords =
            {
                new Proto.Coordinate { X = 0, Y = 0 },
                new Proto.Coordinate { X = 10, Y = 0 },
                new Proto.Coordinate { X = 10, Y = 10 },
                new Proto.Coordinate { X = 0, Y = 10 },
                new Proto.Coordinate { X = 0, Y = 0 }
            }
        });

        var poly2 = new Proto.PolygonGeometry();
        poly2.Rings.Add(new Proto.CoordinateSequence
        {
            Coords =
            {
                new Proto.Coordinate { X = 20, Y = 20 },
                new Proto.Coordinate { X = 30, Y = 20 },
                new Proto.Coordinate { X = 30, Y = 30 },
                new Proto.Coordinate { X = 20, Y = 30 },
                new Proto.Coordinate { X = 20, Y = 20 }
            }
        });

        var multiPoly = new Proto.MultiPolygonGeometry();
        multiPoly.Polygons.Add(poly1);
        multiPoly.Polygons.Add(poly2);

        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            SpatialFilter = new Proto.SpatialFilter
            {
                Geometry = new Proto.Geometry { MultiPolygon = multiPoly },
                SpatialRelationship = Proto.SpatialRelationship.Intersects
            }
        };

        var query = GrpcConversionHelpers.ToFeatureQuery(request);

        query.SpatialFilter.Should().NotBeNull();
        query.SpatialFilter!.Value.Geometry.Should().NotBeEmpty();

        // Round-trip: WKB should deserialize back to a MultiPolygon with 2 polygons
        var reader = new WKBReader();
        var ntsGeom = reader.Read(query.SpatialFilter.Value.Geometry);
        ntsGeom.Should().BeOfType<MultiPolygon>();
        ((MultiPolygon)ntsGeom).NumGeometries.Should().Be(2);
    }
}

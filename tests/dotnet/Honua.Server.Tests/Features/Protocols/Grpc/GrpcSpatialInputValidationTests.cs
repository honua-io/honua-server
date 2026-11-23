// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Grpc.Core;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.Protocols.Grpc;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

[Protocol(TestProtocols.Grpc)]
[Operation(Operations.Query)]
public sealed class GrpcSpatialInputValidationTests
{
    private static readonly double[] _square = [0, 0, 1, 0, 1, 1, 0, 1, 0, 0];
    private static readonly long[] _deltaPath = [0L, 0L, 2L, 2L];
    private static readonly double[] _packedPath = [10.0, 20.0, 11.0, 21.0];

    [UnitTest]
    public void ToFeatureQuery_SpatialFilterWithoutGeometry_IsRejectedInsteadOfDropped()
    {
        var act = () => GrpcConversionHelpers.ToFeatureQuery(Query(new Proto.SpatialFilter
        {
            SpatialRelationship = Proto.SpatialRelationship.Intersects
        }));

        act.Should().Throw<RpcException>().Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [UnitTheory]
    [InlineData(999, 0, 0)]
    [InlineData(1, -5, 0)]
    [InlineData(1, double.NaN, 0)]
    [InlineData(1, 0, -1)]
    public void ToFeatureQuery_InvalidSpatialFilterValues_AreRejected(int relationship, double distance, int nearestCount)
    {
        var filter = PointFilter(0, 0);
        filter.SpatialRelationship = (Proto.SpatialRelationship)relationship;
        filter.Distance = distance;
        filter.NearestCount = nearestCount;

        var act = () => GrpcConversionHelpers.ToFeatureQuery(Query(filter));

        act.Should().Throw<RpcException>().Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [UnitTest]
    public void ToFeatureQuery_UnknownDistanceUnit_IsRejected()
    {
        var filter = PointFilter(0, 0);
        filter.Distance = 1;
        filter.DistanceUnit = (Proto.DistanceUnit)99;

        var act = () => GrpcConversionHelpers.ToFeatureQuery(Query(filter));

        act.Should().Throw<RpcException>().Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [UnitTest]
    public void ToFeatureQuery_NonFinitePoint_IsRejected()
    {
        var act = () => GrpcConversionHelpers.ToFeatureQuery(Query(PointFilter(double.NaN, double.PositiveInfinity)));

        act.Should().Throw<RpcException>().Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [UnitTheory]
    [InlineData(Proto.DistanceUnit.NauticalMiles, 3704.0)]
    [InlineData(Proto.DistanceUnit.SurveyFeet, 1200.0)]
    public void ToFeatureQuery_UnitsWithoutStoreEnum_AreScaledToMeters(Proto.DistanceUnit unit, double expectedMeters)
    {
        var filter = PointFilter(0, 0);
        filter.SpatialRelationship = Proto.SpatialRelationship.WithinDistance;
        filter.Distance = unit == Proto.DistanceUnit.NauticalMiles ? 2 : 3937;
        filter.DistanceUnit = unit;

        var spatial = GrpcConversionHelpers.ToFeatureQuery(Query(filter)).SpatialFilter!.Value;

        spatial.DistanceUnit.Should().Be(DistanceUnit.Meters);
        spatial.Distance.Should().BeApproximately(expectedMeters, 1e-6);
    }

    [UnitTest]
    public void ToFeatureQuery_PackedOrdinatesPolygon_MatchesPerVertexPolygon()
    {
        var perVertex = new Proto.CoordinateSequence();
        perVertex.Coords.AddRange(Enumerable.Range(0, _square.Length / 2)
            .Select(i => new Proto.Coordinate { X = _square[i * 2], Y = _square[i * 2 + 1] }));
        var packed = new Proto.CoordinateSequence { Stride = 2 };
        packed.Ordinates.AddRange(_square);

        var expected = PolygonFilterGeometry(perVertex);
        var actual = PolygonFilterGeometry(packed);

        actual.EqualsExact(expected).Should().BeTrue();
        actual.Area.Should().Be(1);
    }

    [UnitTheory]
    [InlineData(3u, "m", "M")]
    [InlineData(3u, "", "Z")]
    [InlineData(4u, "", "ZM")]
    [InlineData(0u, "z", "Z")]
    public void PackedOrdinates_DecodesZAndM(uint stride, string flags, string expectedDimensions)
    {
        var sequence = new Proto.CoordinateSequence { Stride = stride, HasZ = flags.Contains('z'), HasM = flags.Contains('m') };
        var perVertex = 2 + expectedDimensions.Length;
        sequence.Ordinates.AddRange(Enumerable.Range(1, perVertex * 2).Select(i => (double)i));

        var second = GrpcPackedOrdinates.Decode(sequence)[1];

        second.X.Should().Be(perVertex + 1);
        second.Y.Should().Be(perVertex + 2);
        double.IsNaN(second.Z).Should().Be(!expectedDimensions.Contains('Z'));
        double.IsNaN(second.M).Should().Be(!expectedDimensions.Contains('M'));
    }

    [UnitTheory]
    [InlineData(2u, 5)]
    [InlineData(5u, 10)]
    public void PackedOrdinates_StrideMismatch_IsRejected(uint stride, int count)
    {
        var sequence = new Proto.CoordinateSequence { Stride = stride };
        sequence.Ordinates.AddRange(Enumerable.Repeat(1.0, count));

        var act = () => GrpcPackedOrdinates.Decode(sequence);

        act.Should().Throw<RpcException>().Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [UnitTest]
    public void ToFeatureEditBatch_DeltaCoordsGeometry_IsRejectedInsteadOfWrittenEmpty()
    {
        var path = new Proto.CoordinateSequence { Stride = 2 };
        path.DeltaCoords.AddRange(_deltaPath);
        var polyline = new Proto.PolylineGeometry();
        polyline.Paths.Add(path);
        var request = new Proto.ApplyEditsRequest { ServiceId = "test", LayerId = 0 };
        request.Adds.Add(new Proto.Feature { Geometry = new Proto.Geometry { Polyline = polyline } });

        var act = () => GrpcConversionHelpers.ToFeatureEditBatch(request);

        act.Should().Throw<RpcException>().Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [UnitTest]
    public void ToFeatureEditBatch_PackedPolyline_KeepsItsVertices()
    {
        var path = new Proto.CoordinateSequence { Stride = 2 };
        path.Ordinates.AddRange(_packedPath);
        var polyline = new Proto.PolylineGeometry();
        polyline.Paths.Add(path);
        var request = new Proto.ApplyEditsRequest { ServiceId = "test", LayerId = 0 };
        request.Adds.Add(new Proto.Feature { Geometry = new Proto.Geometry { Polyline = polyline } });

        var batch = GrpcConversionHelpers.ToFeatureEditBatch(request);

        var geometry = new WKBReader().Read(batch.Creates[0].Geometry!);
        geometry.NumPoints.Should().Be(2);
        geometry.Coordinates[1].Should().Be(new Coordinate(11, 21));
    }

    private static Geometry PolygonFilterGeometry(Proto.CoordinateSequence ring)
    {
        var polygon = new Proto.PolygonGeometry();
        polygon.Rings.Add(ring);
        var filter = new Proto.SpatialFilter
        {
            Geometry = new Proto.Geometry { Polygon = polygon },
            SpatialRelationship = Proto.SpatialRelationship.Intersects
        };
        var spatial = GrpcConversionHelpers.ToFeatureQuery(Query(filter)).SpatialFilter!.Value;
        return new WKBReader().Read(spatial.Geometry);
    }

    private static Proto.SpatialFilter PointFilter(double x, double y) => new()
    {
        Geometry = new Proto.Geometry { Point = new Proto.PointGeometry { X = x, Y = y } },
        SpatialRelationship = Proto.SpatialRelationship.Intersects
    };

    private static Proto.QueryFeaturesRequest Query(Proto.SpatialFilter filter) => new()
    {
        ServiceId = "test",
        LayerId = 0,
        SpatialFilter = filter
    };
}

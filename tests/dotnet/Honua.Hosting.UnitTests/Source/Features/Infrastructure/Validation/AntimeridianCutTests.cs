// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Geometry.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Geometries;
using Honua.Infrastructure.Validation;
using Honua.TestKit.Attributes;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure.Validation;

/// <summary>
/// Import and applyEdits share <see cref="AntimeridianCutter"/>. A date-line ring is stored
/// on the short side. A ring that does not cross, including one that only touches ±180, is not rewritten.
/// </summary>
public sealed class AntimeridianCutTests
{
    private const string Crossing = "POLYGON ((170 0, -170 0, -170 10, 170 10, 170 0))";
    private const string Touching = "POLYGON ((170 0, 180 0, 180 10, 170 10, 170 0))";
    private const string TouchingWest = "POLYGON ((-180 0, -170 0, -170 10, -180 10, -180 0))";
    private const string Inland = "POLYGON ((10 0, 20 0, 20 10, 10 10, 10 0))";

    [UnitTest]
    public void Cut_CrossingRing_KeepsOnlyTheShortPacificSide()
    {
        var cut = AntimeridianCutter.Cut(Geographic(Crossing));

        cut.Should().BeOfType<MultiPolygon>();
        cut.Area.Should().BeApproximately(200d, 1d);
        MaxLongitudeSpan(cut).Should().BeLessThanOrEqualTo(20d);
    }

    [UnitTest]
    public void Cut_RingThatDoesNotCrossOrOnlyTouchesTheAntimeridian_IsUnchanged()
    {
        foreach (var wkt in new[] { Inland, Touching, TouchingWest })
        {
            var geometry = Geographic(wkt);
            AntimeridianCutter.Cut(geometry).Should().BeSameAs(geometry, wkt);
        }
    }

    [UnitTest]
    public void Cut_ProjectedRing_IsUnchanged()
    {
        var geometry = Geographic(Crossing);
        geometry.SRID = 3857;

        AntimeridianCutter.Cut(geometry).Should().BeSameAs(geometry);
    }

    [UnitTest]
    public void Cut_MixedCollection_KeepsPointsAndLines()
    {
        var polygon = Geographic(Crossing);
        var point = new Point(new Coordinate(10, 10)) { SRID = 4326 };
        var line = new LineString([new Coordinate(1, 1), new Coordinate(2, 2)]) { SRID = 4326 };
        var collection = new GeometryCollection([polygon, point, line]) { SRID = 4326 };

        var cut = AntimeridianCutter.Cut(collection);

        cut.Should().BeOfType<GeometryCollection>();
        cut.NumGeometries.Should().Be(3);
        cut.GetGeometryN(0).Should().BeOfType<MultiPolygon>();
        cut.GetGeometryN(0).Area.Should().BeApproximately(200d, 1d);
        cut.GetGeometryN(1).Should().BeOfType<Point>();
        cut.GetGeometryN(1).Coordinate.X.Should().Be(10d);
        cut.GetGeometryN(1).Coordinate.Y.Should().Be(10d);
        cut.GetGeometryN(2).Should().BeOfType<LineString>();
        cut.GetGeometryN(2).NumPoints.Should().Be(2);
        cut.SRID.Should().Be(4326);
    }

    [UnitTest]
    public void CutStoredGeometry_WebMercatorSource_CutsInTheGeographicTarget()
    {
        var mercator = WebMercator(Crossing);
        var inland = WebMercator(Inland);

        var cut = AntimeridianCutter.CutStoredGeometry(mercator, 4326);
        var unchanged = AntimeridianCutter.CutStoredGeometry(inland, 4326);

        cut.SRID.Should().Be(4326);
        cut.Should().BeOfType<MultiPolygon>();
        MaxLongitudeSpan(cut).Should().BeLessThanOrEqualTo(20d);
        cut.Area.Should().BeApproximately(200d, 1d);
        unchanged.Should().BeSameAs(inland);
        unchanged.SRID.Should().Be(3857);
    }

    [UnitTest]
    public async Task ApplyEdits_InsertsTheSameCutImportWouldStore()
    {
        var validator = CreateValidator(GeometryValidationResult.Success());
        var input = new WKBWriter().Write(new WKTReader().Read(Crossing));

        var edited = await validator.ValidateGeometryAsync(
            Resource(MetadataV2SpatialReference.Wgs84), input, CancellationToken.None);
        var imported = AntimeridianCutter.Cut(Geographic(Crossing));

        edited.IsValid.Should().BeTrue();
        var stored = new WKBReader().Read(edited.Geometry!);
        stored.Should().BeOfType<MultiPolygon>();
        MaxLongitudeSpan(stored).Should().BeLessThanOrEqualTo(20d);
        stored.Area.Should().BeApproximately(imported.Area, 1e-6);
    }

    [UnitTest]
    public async Task ApplyEdits_LeavesANonCrossingPolygonUnchanged()
    {
        var validator = CreateValidator(GeometryValidationResult.Success());
        var input = new WKBWriter().Write(new WKTReader().Read(Touching));

        var result = await validator.ValidateGeometryAsync(
            Resource(MetadataV2SpatialReference.Wgs84), input, CancellationToken.None);

        result.IsValid.Should().BeTrue();
        result.Geometry.Should().BeSameAs(input);
    }

    [UnitTest]
    public async Task ApplyEdits_CutThatExceedsTheVertexLimit_IsRejected()
    {
        var geometryValidator = Substitute.For<IGeometryValidator>();
        geometryValidator.ValidateCompleteAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GeometryValidationResult.Success()));
        geometryValidator.ValidateWkb(Arg.Any<byte[]?>())
            .Returns(GeometryValidationResult.Failure(
                ValidationErrorCode.TooManyVertices,
                "Geometry has too many vertices"));
        var validator = new FeatureMutationValidator(geometryValidator);
        var input = new WKBWriter().Write(new WKTReader().Read(Crossing));

        var result = await validator.ValidateGeometryAsync(
            Resource(MetadataV2SpatialReference.Wgs84), input, CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("too many vertices");
        result.Geometry.Should().BeNull();
        geometryValidator.Received(1).ValidateWkb(Arg.Any<byte[]?>());
    }

    [UnitTest]
    public async Task ApplyEdits_ProjectedLayer_DoesNotCut()
    {
        var validator = CreateValidator(GeometryValidationResult.Success());
        var input = new WKBWriter().Write(new WKTReader().Read(Crossing));

        var result = await validator.ValidateGeometryAsync(
            Resource(MetadataV2SpatialReference.WebMercator), input, CancellationToken.None);

        result.Geometry.Should().BeSameAs(input);
    }

    private static Geometry Geographic(string wkt)
    {
        var geometry = new WKTReader().Read(wkt);
        geometry.SRID = 4326;
        return geometry;
    }

    private static Geometry WebMercator(string wkt)
    {
        var geographic = Geographic(wkt);
        var copy = geographic.Copy();
        copy.Apply(new LonLatToWebMercatorFilter());
        copy.SRID = 3857;
        return copy;
    }

    private sealed class LonLatToWebMercatorFilter : ICoordinateSequenceFilter
    {
        public bool Done => false;

        public bool GeometryChanged => true;

        public void Filter(CoordinateSequence seq, int i)
        {
            var (x, y) = WebMercatorMath.LonLatToWebMercator(seq.GetX(i), seq.GetY(i));
            seq.SetX(i, x);
            seq.SetY(i, y);
        }
    }

    private static double MaxLongitudeSpan(Geometry geometry)
    {
        var max = 0d;
        for (var i = 0; i < geometry.NumGeometries; i++)
        {
            if (geometry.GetGeometryN(i) is not Polygon polygon)
            {
                continue;
            }

            max = Math.Max(max, RingSpan(polygon.ExteriorRing));
        }

        return max;
    }

    private static double RingSpan(LineString ring)
    {
        var max = 0d;
        for (var i = 1; i < ring.NumPoints; i++)
        {
            max = Math.Max(max, Math.Abs(ring.GetCoordinateN(i).X - ring.GetCoordinateN(i - 1).X));
        }

        return max;
    }

    private static FeatureMutationValidator CreateValidator(GeometryValidationResult validationResult)
    {
        var geometryValidator = Substitute.For<IGeometryValidator>();
        geometryValidator.ValidateCompleteAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(validationResult));
        geometryValidator.ValidateWkb(Arg.Any<byte[]?>())
            .Returns(GeometryValidationResult.Success());
        return new FeatureMutationValidator(geometryValidator);
    }

    private static MetadataV2Resource Resource(MetadataV2SpatialReference spatialReference) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = "layer", Name = "layer" },
        Spatial = new MetadataV2ResourceSpatial
        {
            SpatialReference = spatialReference,
            GeometryType = MetadataV2GeometryType.Polygon
        }
    };
}

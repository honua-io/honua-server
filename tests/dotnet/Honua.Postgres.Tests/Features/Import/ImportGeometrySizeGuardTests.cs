// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FileImport.Services;
using NetTopologySuite.Geometries;

namespace Honua.Postgres.Tests.Features.Import;

/// <summary>
/// Unit tests for <see cref="ImportGeometrySizeGuard"/> — the bounded memory guard that rejects
/// oversized geometries before they are serialized to WKB, preventing import OOM crashes (#1626).
/// </summary>
public sealed class ImportGeometrySizeGuardTests
{
    private static readonly GeometryFactory Factory = new();

    [Fact]
    public void Check_WithNullGeometry_IsWithinLimits()
    {
        var result = ImportGeometrySizeGuard.Check(null, maxVertices: 10, maxRings: 10);
        result.IsWithinLimits.Should().BeTrue();
        result.Status.Should().Be(ImportGeometrySizeStatus.WithinLimits);
    }

    [Fact]
    public void Check_WithSmallPolygon_IsWithinLimits()
    {
        var polygon = CreateSquare();
        var result = ImportGeometrySizeGuard.Check(polygon, maxVertices: 10_000, maxRings: 100);
        result.IsWithinLimits.Should().BeTrue();
    }

    [Fact]
    public void Check_WithTooManyVertices_RejectsWithGuidance()
    {
        // A ring with more vertices than the limit (an island-scale outline in miniature).
        var bigRing = CreateRing(vertexCount: 50);

        var result = ImportGeometrySizeGuard.Check(bigRing, maxVertices: 10, maxRings: 100);

        result.IsWithinLimits.Should().BeFalse();
        result.Status.Should().Be(ImportGeometrySizeStatus.TooManyVertices);
        result.Message.Should().NotBeNull();
        result.Message.Should().Contain("vertex count");
        result.Message.Should().Contain("explodecollections");
    }

    [Fact]
    public void Check_WithTooManyRings_RejectsWithGuidance()
    {
        // A multipolygon of several single-ring squares exceeds a small ring limit.
        var polygons = new Polygon[5];
        for (var i = 0; i < polygons.Length; i++)
        {
            polygons[i] = CreateSquare(offset: i * 10.0);
        }

        var multiPolygon = Factory.CreateMultiPolygon(polygons);

        var result = ImportGeometrySizeGuard.Check(multiPolygon, maxVertices: 10_000, maxRings: 3);

        result.IsWithinLimits.Should().BeFalse();
        result.Status.Should().Be(ImportGeometrySizeStatus.TooManyRings);
        result.Message.Should().Contain("ring count");
    }

    [Fact]
    public void Check_WithZeroLimits_DisablesChecks()
    {
        var bigRing = CreateRing(vertexCount: 5_000);
        var result = ImportGeometrySizeGuard.Check(bigRing, maxVertices: 0, maxRings: 0);
        result.IsWithinLimits.Should().BeTrue();
    }

    [Fact]
    public void CountRings_CountsExteriorAndInteriorRings()
    {
        var shell = CreateLinearRing(0, 0, 100);
        var hole = CreateLinearRing(10, 10, 5);
        var polygonWithHole = Factory.CreatePolygon(shell, [hole]);

        ImportGeometrySizeGuard.CountRings(polygonWithHole).Should().Be(2);
    }

    [Fact]
    public void CountRings_NestedGeometryCollection_SumsAllRings()
    {
        var square = CreateSquare();
        var collection = Factory.CreateGeometryCollection([square, square]);
        ImportGeometrySizeGuard.CountRings(collection).Should().Be(2);
    }

    private static Polygon CreateSquare(double offset = 0)
        => Factory.CreatePolygon(CreateLinearRing(offset, offset, 1));

    private static LineString CreateRing(int vertexCount)
    {
        // A closed ring with the requested number of distinct vertices (approximately).
        var coords = new Coordinate[vertexCount + 1];
        for (var i = 0; i < vertexCount; i++)
        {
            coords[i] = new Coordinate(i, i % 2);
        }

        coords[vertexCount] = new Coordinate(coords[0].X, coords[0].Y);
        return Factory.CreateLineString(coords);
    }

    private static LinearRing CreateLinearRing(double x, double y, double size)
        => Factory.CreateLinearRing(
        [
            new Coordinate(x, y),
            new Coordinate(x + size, y),
            new Coordinate(x + size, y + size),
            new Coordinate(x, y + size),
            new Coordinate(x, y),
        ]);
}

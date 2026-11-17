// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Protocols.GeoServices.GeometryService.Services;
using Honua.TestKit.Attributes;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GeometryService;

// The segment pairs that could cross, counted before an overlay runs. In process; the
// component host is not needed.
[Protocol(TestProtocols.GeometryService)]
public sealed class GeometryServiceOverlayBudgetTests
{
    private static readonly GeometryFactory _factory = new();

    private static readonly long _signedInDefault = new GeometryLimits().MaxOverlayCandidatePairs;

    [UnitTest]
    [Operation(Operations.Intersect)]
    public void Grid_CountsEveryCrossing()
    {
        var count = OverlayCandidatePairs.Count([Rows(30), Columns(30)], long.MaxValue);

        count.Should().BeGreaterThanOrEqualTo(30 * 30);
    }

    [UnitTest]
    [Operation(Operations.Intersect)]
    public void GridAtTheVertexLimit_StopsSoonAfterTheLimitWithBoundedAllocation()
    {
        // 25,000 two-point paths in one geometry: 50,000 vertices, about 156 million crossings.
        var grid = _factory.CreateMultiLineString([.. Rows(12_500).Geometries
            .Concat(Columns(12_500).Geometries)
            .Cast<LineString>()]);
        const long limit = 500_000;

        var before = GC.GetAllocatedBytesForCurrentThread();
        var count = OverlayCandidatePairs.Count([grid], limit);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        count.Should().BeGreaterThan(limit);
        count.Should().BeLessThanOrEqualTo(limit + 1 + (OverlayCandidatePairs.MaxChainSegments * OverlayCandidatePairs.MaxChainSegments));
        allocated.Should().BeLessThan(32L * 1024 * 1024, "the count holds the input's chains and index, never the crossings");
    }

    [UnitTest]
    [Operation(Operations.Union)]
    public void PartsOfOneGeometry_AreCountedAgainstEachOther()
    {
        var crossing = _factory.CreateMultiLineString([.. Rows(20).Geometries
            .Concat(Columns(20).Geometries)
            .Cast<LineString>()]);

        OverlayCandidatePairs.Count([crossing], long.MaxValue).Should().BeGreaterThanOrEqualTo(20 * 20);
    }

    [UnitTest]
    [Operation(Operations.Intersect)]
    public void OverlappingEnvelopesWithoutCrossings_AreCountedAsWork()
    {
        // Parallel diagonals 0.1 apart: no line crosses another, but every segment envelope
        // meets its neighbours'. The count bounds the work of finding crossings, not only them.
        var diagonals = Enumerable.Range(0, 200)
            .Select(i => _factory.CreateLineString([new Coordinate(i * 0.1, 0), new Coordinate((i * 0.1) + 100, 100)]))
            .ToArray();

        OverlayCandidatePairs.Count([_factory.CreateMultiLineString(diagonals)], 10_000).Should().BeGreaterThan(10_000);
    }

    [UnitTest]
    [Operation(Operations.Intersect)]
    public void TwoChainsAlongsideEachOther_CountTheirSegmentPairs()
    {
        // Two staircases of 64 steps, each one monotone chain, a quarter step apart: four chain
        // visits, and every step meets its neighbours in the other staircase.
        var lower = Staircase(0);
        var upper = Staircase(0.25);

        OverlayCandidatePairs.Count([lower, upper], long.MaxValue).Should().BeGreaterThan(64);
    }

    [UnitTest]
    [Operation(Operations.Intersect)]
    public void OverlappingDetailedPolygons_FitTheNotSignedInBudget()
    {
        // Two 50,000-vertex circles, half overlapping: an ordinary overlay at the vertex limit.
        var first = Circle(0, 50_000);
        var second = Circle(500, 50_000);

        OverlayCandidatePairs.Count([first, second], long.MaxValue)
            .Should().BeLessThan(_signedInDefault / GeometryServiceOverlayBudget.NotSignedInDivisor);
    }

    [UnitTest]
    [Operation(Operations.Union)]
    public void AdjacentParcels_FitTheNotSignedInBudget()
    {
        // 2,500 squares sharing their edges, unioned in one request.
        var parcels = Enumerable.Range(0, 2_500)
            .Select(i => (Geometry)_factory.ToGeometry(new Envelope(i % 50, (i % 50) + 1, i / 50, (i / 50) + 1)))
            .ToArray();

        OverlayCandidatePairs.Count(parcels, long.MaxValue)
            .Should().BeLessThan(_signedInDefault / GeometryServiceOverlayBudget.NotSignedInDivisor);
    }

    [UnitTest]
    [Operation(Operations.Intersect)]
    public void Budget_IsAQuarterForCallersNotSignedIn()
    {
        GeometryServiceOverlayBudget.Create(GeometryServiceTestContexts.Create(GeometryServiceCaller.SignedIn))
            .PerOperation.Should().Be(_signedInDefault);
        GeometryServiceOverlayBudget.Create(GeometryServiceTestContexts.Create(GeometryServiceCaller.NotSignedIn))
            .PerOperation.Should().Be(_signedInDefault / GeometryServiceOverlayBudget.NotSignedInDivisor);
    }

    [UnitTest]
    [Operation(Operations.Intersect)]
    public void Budget_FollowsTheConfiguredLimitUpToTheCeiling()
    {
        var limits = new LimitsOptions();
        limits.Geometry.MaxOverlayCandidatePairs = 40_000;
        GeometryServiceOverlayBudget.Create(GeometryServiceTestContexts.Create(GeometryServiceCaller.SignedIn, limits))
            .PerOperation.Should().Be(40_000);

        limits.Geometry.MaxOverlayCandidatePairs = long.MaxValue;
        GeometryServiceOverlayBudget.Create(GeometryServiceTestContexts.Create(GeometryServiceCaller.SignedIn, limits))
            .PerOperation.Should().Be(GeometryServiceOverlayBudget.PerOperationCeiling);

        GeometryServiceOverlayBudget.Create(GeometryServiceTestContexts.CreateWithoutLimits())
            .PerOperation.Should().Be(_signedInDefault / GeometryServiceOverlayBudget.NotSignedInDivisor);
    }

    [UnitTest]
    [Operation(Operations.Intersect)]
    public void Check_RefusesInputsThatCrossTooOften()
    {
        var budget = new GeometryServiceOverlayBudget(500);
        var writer = new WKBWriter();
        var rows = writer.Write(Rows(30));
        var columns = writer.Write(Columns(30));

        budget.Check(Rows(30));
        var act = () => budget.Check(rows, columns);

        act.Should().Throw<GeometryServiceLimitException>()
            .WithMessage(GeometryServiceOverlayBudget.RefusalMessage)
            .Which.Should().BeAssignableTo<ArgumentException>("handlers answer it as a 400");
    }

    private static MultiLineString Rows(int count)
        => _factory.CreateMultiLineString(Enumerable.Range(0, count)
            .Select(i => _factory.CreateLineString([new Coordinate(-1, i), new Coordinate(count, i)]))
            .ToArray());

    private static MultiLineString Columns(int count)
        => _factory.CreateMultiLineString(Enumerable.Range(0, count)
            .Select(i => _factory.CreateLineString([new Coordinate(i, -1), new Coordinate(i, count)]))
            .ToArray());

    private static LineString Staircase(double offset)
        => _factory.CreateLineString([.. Enumerable.Range(0, OverlayCandidatePairs.MaxChainSegments + 1)
            .Select(i => new Coordinate(i + offset, i + (i % 2 * 0.5)))]);

    private static Polygon Circle(double centerX, int vertices)
    {
        var ring = new Coordinate[vertices + 1];
        for (var i = 0; i < vertices; i++)
        {
            var angle = 2 * Math.PI * i / vertices;
            ring[i] = new Coordinate(centerX + (1_000 * Math.Cos(angle)), 1_000 * Math.Sin(angle));
        }

        ring[vertices] = ring[0].Copy();
        return _factory.CreatePolygon(ring);
    }
}

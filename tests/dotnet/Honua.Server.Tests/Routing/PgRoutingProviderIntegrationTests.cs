// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Routing.Features.Routing.Domain;
using Honua.Routing.Features.Routing.Providers;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Honua.Server.Tests.Routing;

/// <summary>
/// Gated integration tests that validate the real <see cref="PgRoutingProvider"/>
/// SQL (<c>pgr_dijkstra</c> / <c>pgr_drivingDistance</c>) against a live
/// pgRouting database seeded with a deterministic 3x3 lattice
/// (<see cref="PgRoutingFixture.SeedSql"/>). Opt-in only: set
/// <c>HONUA_ROUTING_TEST=1</c> to run (the heavy <c>pgrouting/pgrouting</c> image
/// is never pulled on the default Fast tier). Point at an existing pgRouting DB
/// (e.g. <c>docker/routing/compose.yml</c>) via <c>HONUA_ROUTING_TEST_DB_URL</c>
/// to skip the Testcontainer. See issue #1266 and ADR-0050.
/// </summary>
/// <remarks>
/// Seed network (vertices 1-9, every edge cost = 1):
/// <code>
///   7 -- 8 -- 9        (0,.02) (.01,.02) (.02,.02)
///   |    |    |
///   4 -- 5 -- 6        (0,.01) (.01,.01) (.02,.01)
///   |    |    |
///   1 -- 2 -- 3        (0,0)   (.01,0)   (.02,0)
/// </code>
/// </remarks>
public sealed class PgRoutingProviderIntegrationTests : IClassFixture<PgRoutingFixture>
{
    private const string RoutingTestEnv = "HONUA_ROUTING_TEST";

    private readonly PgRoutingFixture _fixture;
    private readonly PgRoutingProvider _provider;

    /// <summary>
    /// Initializes a new instance of the <see cref="PgRoutingProviderIntegrationTests"/> class.
    /// </summary>
    /// <param name="fixture">The seeded pgRouting fixture.</param>
    public PgRoutingProviderIntegrationTests(PgRoutingFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _fixture = fixture;
        var connectionProvider = new FixtureDatabaseConnectionProvider(fixture.DataSource, fixture.ConnectionString);
        _provider = new PgRoutingProvider(connectionProvider, NullLogger<PgRoutingProvider>.Instance);
    }

    [RoutingTest(RoutingTestEnv)]
    public async Task SolveRoute_BetweenTwoNodes_ReturnsConnectedPathWithPositiveLength()
    {
        // Stops snap to the SW corner (vertex 1) and NE corner (vertex 9). The
        // least-cost path through the uniform-cost lattice is 4 hops (cost 4).
        var request = new RouteSolveRequest(
        [
            new RoutePoint(0.00, 0.00),
            new RoutePoint(0.02, 0.02),
        ]);

        var result = await _provider.SolveRouteAsync(request, CancellationToken.None);

        Assert.True(result.Solved, "Expected a solved route between the corner vertices.");
        Assert.Contains("LineString", result.RouteGeometryGeoJson, StringComparison.Ordinal);
        // 4 uniform-cost hops.
        Assert.Equal(4.0, result.TotalTimeMinutes, precision: 3);
        // Four ~1.11 km geodesic grid steps => ~4.44 km.
        Assert.InRange(result.TotalLengthMeters, 4000, 5000);
        Assert.NotEmpty(result.Directions);
    }

    [RoutingTest(RoutingTestEnv)]
    public async Task SolveServiceArea_WithBreaks_ReturnsPolygonsPerBreak()
    {
        // Facility at vertex 1 (SW corner). Within cost 1: vertices {1,2,4};
        // within cost 2: {1,2,3,4,5,7}. Both reachable sets have >= 3
        // non-collinear vertices, so the concave hull is a polygon.
        var request = new ServiceAreaSolveRequest(
            Facilities: [new RoutePoint(0.00, 0.00)],
            Breaks: [1.0, 2.0],
            TravelDirection: ServiceAreaTravelDirection.FromFacility);

        var result = await _provider.SolveServiceAreaAsync(request, CancellationToken.None);

        Assert.Equal(2, result.Polygons.Count);

        var first = result.Polygons[0];
        Assert.Equal(0, first.FacilityId);
        Assert.Equal(0.0, first.FromBreak);
        Assert.Equal(1.0, first.ToBreak);
        Assert.Contains("Polygon", first.GeometryGeoJson, StringComparison.Ordinal);

        var second = result.Polygons[1];
        Assert.Equal(0, second.FacilityId);
        Assert.Equal(1.0, second.FromBreak);
        Assert.Equal(2.0, second.ToBreak);
        Assert.Contains("Polygon", second.GeometryGeoJson, StringComparison.Ordinal);
    }

    [RoutingTest(RoutingTestEnv)]
    public async Task SolveRoute_OutSrid3857_SnapsRequestSridOrdinatesAndReturnsGeometry()
    {
        // The lattice is stored in 4326. Reproject the SW (0,0) and NE (.02,.02)
        // corners to Web Mercator (3857) ordinates and submit them with OutSrid=3857.
        // Because OutSrid is treated as the input SRID too, the provider must
        // transform the probe points back to 4326 before snapping. A hardcoded-4326
        // snap would interpret these large metric values as lon/lat and snap wrong.
        var (swX, swY) = Wgs84ToWebMercator(0.0, 0.0);
        var (neX, neY) = Wgs84ToWebMercator(0.02, 0.02);

        var request = new RouteSolveRequest(
        [
            new RoutePoint(swX, swY),
            new RoutePoint(neX, neY),
        ],
            OutSrid: 3857);

        var result = await _provider.SolveRouteAsync(request, CancellationToken.None);

        Assert.True(result.Solved, "Expected a solved route for the reprojected 3857 corners.");
        Assert.Contains("LineString", result.RouteGeometryGeoJson, StringComparison.Ordinal);
        Assert.Equal(4.0, result.TotalTimeMinutes, precision: 3);
        // Geodesic length is computed in meters regardless of output SRID.
        Assert.InRange(result.TotalLengthMeters, 4000, 5000);
    }

    [RoutingTest(RoutingTestEnv)]
    public async Task SolveRoute_UnreachableStopPair_ReturnsUnsolvedResult()
    {
        // Insert an isolated vertex (no incident edges) far from the lattice and
        // route to it. pgr_dijkstra finds no path => the provider returns an
        // unsolved result (empty geometry, Solved=false), exercising the no-solve
        // branch.
        await using var conn = await _fixture.DataSource.OpenConnectionAsync(CancellationToken.None);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO public.ways_vertices_pgr (id, the_geom)
                VALUES (9999, ST_SetSRID(ST_MakePoint(10.0, 10.0), 4326))
                ON CONFLICT (id) DO NOTHING;
                """;
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }

        try
        {
            var request = new RouteSolveRequest(
            [
                new RoutePoint(0.0, 0.0),    // snaps to vertex 1
                new RoutePoint(10.0, 10.0),  // snaps to the isolated vertex 9999
            ]);

            var result = await _provider.SolveRouteAsync(request, CancellationToken.None);

            Assert.False(result.Solved, "Routing to an isolated vertex must not solve.");
            Assert.Equal(string.Empty, result.RouteGeometryGeoJson);
            Assert.Empty(result.Directions);
        }
        finally
        {
            await using var cleanup = conn.CreateCommand();
            cleanup.CommandText = "DELETE FROM public.ways_vertices_pgr WHERE id = 9999;";
            await cleanup.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    [RoutingTest(RoutingTestEnv)]
    public async Task SolveServiceArea_TinyBreak_DoesNotEmitDegenerateEmptyRingsPolygon()
    {
        // A break of 0.5 over the uniform cost-1 lattice reaches only the facility
        // vertex itself (no neighbor within cost 0.5). The reachable set has a
        // single vertex, which cannot form a polygon, so the provider must SKIP the
        // ring rather than emit a feature with empty rings.
        var request = new ServiceAreaSolveRequest(
            Facilities: [new RoutePoint(0.00, 0.00)],
            Breaks: [0.5],
            TravelDirection: ServiceAreaTravelDirection.FromFacility);

        var result = await _provider.SolveServiceAreaAsync(request, CancellationToken.None);

        // No polygon should be emitted for the degenerate reachable set.
        Assert.Empty(result.Polygons);
    }

    [RoutingTest(RoutingTestEnv)]
    public async Task SolveServiceArea_ToFacility_ExercisesReversedGraphAndSolves()
    {
        // The seeded lattice is symmetric (cost == reverse_cost), so ToFacility and
        // FromFacility cover the same vertices; this test proves the reversed-graph
        // SQL path (source/target swapped) is valid and still solves.
        var facilities = new[] { new RoutePoint(0.00, 0.00) };

        var fromResult = await _provider.SolveServiceAreaAsync(
            new ServiceAreaSolveRequest(facilities, [2.0], ServiceAreaTravelDirection.FromFacility),
            CancellationToken.None);

        var toResult = await _provider.SolveServiceAreaAsync(
            new ServiceAreaSolveRequest(facilities, [2.0], ServiceAreaTravelDirection.ToFacility),
            CancellationToken.None);

        Assert.Single(fromResult.Polygons);
        Assert.Single(toResult.Polygons);
        Assert.Contains("Polygon", toResult.Polygons[0].GeometryGeoJson, StringComparison.Ordinal);
    }

    private static (double X, double Y) Wgs84ToWebMercator(double lon, double lat)
    {
        const double originShift = 20037508.342789244 / 180.0;
        var x = lon * originShift;
        var y = Math.Log(Math.Tan((90.0 + lat) * Math.PI / 360.0)) / (Math.PI / 180.0) * originShift;
        return (x, y);
    }
}

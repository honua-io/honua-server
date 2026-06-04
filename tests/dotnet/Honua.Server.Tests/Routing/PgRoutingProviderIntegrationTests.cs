// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Routing.Features.Routing.Domain;
using Honua.Routing.Features.Routing.Providers;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
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

    private readonly PgRoutingProvider _provider;

    /// <summary>
    /// Initializes a new instance of the <see cref="PgRoutingProviderIntegrationTests"/> class.
    /// </summary>
    /// <param name="fixture">The seeded pgRouting fixture.</param>
    public PgRoutingProviderIntegrationTests(PgRoutingFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
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
}

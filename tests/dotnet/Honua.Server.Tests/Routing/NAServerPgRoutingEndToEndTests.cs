// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Providers;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Honua.Server.Tests.Routing;

/// <summary>
/// Gated end-to-end integration tests that exercise the full GeoServices NAServer
/// HTTP path over the <em>real</em> <see cref="PgRoutingProvider"/> (not the
/// in-memory test double used by the protocol-level
/// <c>NAServerEndpointTests</c>). The booted server runs against its normal
/// Postgres fixture, but the registered <see cref="IRoutingProvider"/> is replaced
/// with a real <see cref="PgRoutingProvider"/> bound to a separate, seeded
/// pgRouting database (<see cref="PgRoutingFixture"/>). This proves the whole
/// stack: HTTP request → NAServer adapter (parameter translation) → real
/// <see cref="PgRoutingProvider"/> SQL → pgRouting → Esri-shaped JSON response.
/// </summary>
/// <remarks>
/// Opt-in only: set <c>HONUA_ROUTING_TEST=1</c> to run (the heavy
/// <c>pgrouting/pgrouting</c> image is never pulled on the default Fast tier).
/// See issue #1266 and ADR-0050. Seed network (vertices 1-9, every edge cost = 1):
/// <code>
///   7 -- 8 -- 9        (0,.02) (.01,.02) (.02,.02)
///   |    |    |
///   4 -- 5 -- 6        (0,.01) (.01,.01) (.02,.01)
///   |    |    |
///   1 -- 2 -- 3        (0,0)   (.01,0)   (.02,0)
/// </code>
/// </remarks>
public sealed class NAServerPgRoutingEndToEndTests : IClassFixture<PgRoutingFixture>, IAsyncLifetime
{
    private const string RoutingTestEnv = "HONUA_ROUTING_TEST";

    // Same serviceId path segment used by the protocol-level NAServerEndpointTests.
    private const string ServiceId = "Routing";

    private readonly WebAppFixture _fixture;

    /// <summary>
    /// Initializes a new instance of the <see cref="NAServerPgRoutingEndToEndTests"/> class.
    /// </summary>
    /// <param name="routingFixture">The seeded pgRouting fixture (separate DB).</param>
    public NAServerPgRoutingEndToEndTests(PgRoutingFixture routingFixture)
    {
        ArgumentNullException.ThrowIfNull(routingFixture);

        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IRoutingProvider>();
                services.AddScoped<IRoutingProvider>(_ => new PgRoutingProvider(
                    new FixtureDatabaseConnectionProvider(
                        routingFixture.DataSource,
                        routingFixture.ConnectionString),
                    NullLogger<PgRoutingProvider>.Instance));
            });
    }

    /// <inheritdoc />
    public Task InitializeAsync() => _fixture.InitializeAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [RoutingTest(RoutingTestEnv)]
    public async Task RouteSolve_OverRealPgRouting_ReturnsEsriRouteWithPaths()
    {
        // Two stops at the SW (0,0 -> vertex 1) and NE (.02,.02 -> vertex 9) corners.
        var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("stops", "0.0,0.0;0.02,0.02"),
            new KeyValuePair<string, string>("returnRoutes", "true"),
            new KeyValuePair<string, string>("returnDirections", "true"),
        ]);

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{ServiceId}/NAServer/Route/solve",
            payload,
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(CancellationToken.None));
        var root = document.RootElement;

        var feature = root.GetProperty("routes").GetProperty("features")[0];

        // Geometry is present and non-empty (the exact path may vary among
        // equal-cost routes through the uniform lattice).
        var paths = feature.GetProperty("geometry").GetProperty("paths");
        paths.GetArrayLength().Should().BeGreaterThan(0);
        paths[0].GetArrayLength().Should().BeGreaterThan(0);

        var attributes = feature.GetProperty("attributes");
        attributes.GetProperty("Total_Length").GetDouble().Should().BeGreaterThan(0);
        attributes.GetProperty("Total_TravelTime").GetDouble().Should().BeGreaterThan(0);
    }

    [RoutingTest(RoutingTestEnv)]
    public async Task ServiceAreaSolve_OverRealPgRouting_ReturnsSaPolygons()
    {
        // One facility at vertex 1 (SW corner) with breaks at cost 1 and 2.
        var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("facilities", "0.0,0.0"),
            new KeyValuePair<string, string>("defaultBreaks", "1,2"),
        ]);

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{ServiceId}/NAServer/ServiceArea/solveServiceArea",
            payload,
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(CancellationToken.None));
        var features = document.RootElement.GetProperty("saPolygons").GetProperty("features");

        // One facility x two breaks => two concentric polygons.
        features.GetArrayLength().Should().Be(2);

        var first = features[0].GetProperty("attributes");
        first.GetProperty("FromBreak").GetDouble().Should().Be(0);
        first.GetProperty("ToBreak").GetDouble().Should().Be(1);

        var second = features[1].GetProperty("attributes");
        second.GetProperty("FromBreak").GetDouble().Should().Be(1);
        second.GetProperty("ToBreak").GetDouble().Should().Be(2);

        features[0].GetProperty("geometry").GetProperty("rings").GetArrayLength().Should().BeGreaterThan(0);
        features[1].GetProperty("geometry").GetProperty("rings").GetArrayLength().Should().BeGreaterThan(0);
    }
}

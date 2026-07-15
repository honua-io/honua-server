// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Capability-honesty guard (#2807): every manifest feature flag advertised by
/// <c>GET /api/v1/admin/capabilities</c> must correspond to whether a matching route is actually
/// registered in the deployed <see cref="EndpointDataSource"/>. This prevents recurrence of the
/// defect where the handshake advertised removed manifest apply/dry-run/prune endpoints (deleted in
/// the #1035 cutover), which would send SDKs that branch on the flags into a 404.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Metadata)]
public sealed class AdminCapabilitiesRouteHonestyTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    /// <summary>
    /// Maps each advertised manifest capability flag to a predicate that reports whether a
    /// route implementing it is actually registered. The advertised value must equal the
    /// route-existence reality in both directions: advertise <c>true</c> only when the route
    /// exists, and if the route is (re)introduced the flag must be flipped to match.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Func<HashSet<string>, bool>> _manifestFlagRouteMatchers =
        new Dictionary<string, Func<HashSet<string>, bool>>(StringComparer.Ordinal)
        {
            // Read-only GitOps manifest export (GET .../gitops-manifest) survived #1035.
            ["manifestExport"] = routes => routes.Any(r =>
                r.StartsWith("GET ", StringComparison.OrdinalIgnoreCase) &&
                r.Contains("gitops-manifest", StringComparison.OrdinalIgnoreCase)),
            // Mutating manifest endpoints removed in #1035 — no registered route.
            ["manifestApply"] = routes => routes.Any(r =>
                r.Contains("manifest", StringComparison.OrdinalIgnoreCase) &&
                r.Contains("apply", StringComparison.OrdinalIgnoreCase)),
            ["manifestDryRun"] = routes => routes.Any(r =>
                r.Contains("manifest", StringComparison.OrdinalIgnoreCase) &&
                (r.Contains("dry-run", StringComparison.OrdinalIgnoreCase) ||
                 r.Contains("dryrun", StringComparison.OrdinalIgnoreCase))),
            ["manifestPrune"] = routes => routes.Any(r =>
                r.Contains("manifest", StringComparison.OrdinalIgnoreCase) &&
                r.Contains("prune", StringComparison.OrdinalIgnoreCase)),
        };

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/capabilities")]
    public async Task AdvertisedManifestCapabilities_MatchRegisteredRoutes()
    {
        var response = await _fixture.Client.GetAsync("/api/v1/admin/capabilities");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var features = document.RootElement
            .GetProperty("data")
            .GetProperty("compatibility")
            .GetProperty("features");

        var deployedRoutes = CollectDeployedRoutes();

        foreach (var (flag, routeExists) in _manifestFlagRouteMatchers)
        {
            var advertised = features.GetProperty(flag).GetBoolean();
            var hasRoute = routeExists(deployedRoutes);

            advertised.Should().Be(
                hasRoute,
                "capability flag '{0}' must match whether a route implementing it is registered " +
                "(advertise true only when the route exists). Update AdminInfoEndpoints.HandleGetCapabilities " +
                "or the corresponding endpoint mapping so the handshake stays honest.",
                flag);
        }
    }

    private HashSet<string> CollectDeployedRoutes()
    {
        var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in _fixture.Services.GetServices<EndpointDataSource>())
        {
            foreach (var endpoint in source.Endpoints)
            {
                if (endpoint is not RouteEndpoint routeEndpoint)
                {
                    continue;
                }

                var pattern = routeEndpoint.RoutePattern.RawText;
                if (string.IsNullOrEmpty(pattern))
                {
                    continue;
                }

                var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                    ?? new[] { HttpMethods.Get };
                foreach (var method in methods)
                {
                    routes.Add($"{method.ToUpperInvariant()} {pattern}");
                }
            }
        }

        return routes;
    }
}

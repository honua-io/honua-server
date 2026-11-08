// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Primitives;

namespace Honua.Server.Startup;

// Retired hosted UIs stay blocked even when a stale asset manifest is loaded.
internal static class HostedBlazorAssetHelpers
{
    public static void FilterHostedBlazorStaticAssetEndpoints(WebApplication app)
    {
        string[] blockedPrefixes = ["admin/", "samples/stac-ops/"];

        var routeBuilder = (IEndpointRouteBuilder)app;
        var dataSources = routeBuilder.DataSources.ToArray();

        routeBuilder.DataSources.Clear();
        foreach (var dataSource in dataSources)
        {
            routeBuilder.DataSources.Add(new HostedBlazorStaticAssetFilterDataSource(
                dataSource,
                blockedPrefixes));
        }
    }

    private sealed class HostedBlazorStaticAssetFilterDataSource(
        EndpointDataSource innerDataSource,
        IReadOnlyCollection<string> blockedPrefixes) : EndpointDataSource
    {
        private readonly EndpointDataSource _innerDataSource = innerDataSource;
        private readonly IReadOnlyCollection<string> _blockedPrefixes = blockedPrefixes;

        public override IReadOnlyList<Endpoint> Endpoints => FilterEndpoints();

        public override IChangeToken GetChangeToken() => _innerDataSource.GetChangeToken();

        private IReadOnlyList<Endpoint> FilterEndpoints()
        {
            if (_blockedPrefixes.Count == 0)
            {
                return _innerDataSource.Endpoints;
            }

            return _innerDataSource.Endpoints
                .Where(ShouldKeepEndpoint)
                .ToArray();
        }

        private bool ShouldKeepEndpoint(Endpoint endpoint)
        {
            if (endpoint is not RouteEndpoint routeEndpoint)
            {
                return true;
            }

            var route = routeEndpoint.RoutePattern.RawText;
            if (string.IsNullOrEmpty(route))
            {
                return true;
            }

            return !_blockedPrefixes.Any(blockedPrefix => route.StartsWith(blockedPrefix, StringComparison.OrdinalIgnoreCase));
        }
    }
}

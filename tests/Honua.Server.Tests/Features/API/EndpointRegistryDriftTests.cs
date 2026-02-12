// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.API;

/// <summary>
/// Verifies that EndpointRegistry stays in sync with actually deployed endpoints.
/// Prevents drift where new endpoints are added to the app but not tracked in the registry.
/// </summary>
public sealed class EndpointRegistryDriftTests : IDisposable
{
    private static readonly Regex _routeConstraintRegex =
        new(@"\{([^{}:]+):[^{}]+\}", RegexOptions.Compiled);

    private readonly TestWebApplicationFactory _factory = new();

    /// <summary>
    /// Endpoints deployed in the application that are intentionally excluded from the registry.
    /// These are infrastructure/internal endpoints that don't need integration test coverage tracking.
    /// </summary>
    private static readonly HashSet<string> _excludedExactPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        // ASP.NET infrastructure
        "GET /openapi/{documentName}.json",
        // Debug/test endpoints conditionally mapped
        "GET /test-error",
        "GET /test-memory-tracking",
        "GET /test-performance",
        "GET /test-slow",
        // File serving (internal)
        "GET /api/files/{**path}",
        // Admin UI fallback route
        "GET /{**path}",
        "GET /{*path}",
    };

    [Fact]
    [Trait("Category", "Architecture")]
    public void AllDeployedEndpoints_AreTrackedInRegistry()
    {
        using var _ = _factory.CreateClient();
        var endpointSources = _factory.Services.GetServices<EndpointDataSource>();
        var deployedEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in endpointSources)
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

                // Normalize: ensure leading slash
                if (!pattern.StartsWith('/'))
                {
                    pattern = "/" + pattern;
                }

                var httpMethods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
                if (httpMethods == null || httpMethods.Count == 0)
                {
                    continue;
                }

                foreach (var method in httpMethods)
                {
                    var key = $"{method.ToUpperInvariant()} {NormalizePath(pattern)}";
                    if (!IsExcluded(key))
                    {
                        deployedEndpoints.Add(key);
                    }
                }
            }
        }

        var registeredEndpoints = new HashSet<string>(
            EndpointRegistry.All.Select(e => $"{e.Method.ToUpperInvariant()} {NormalizePath(e.Path)}"),
            StringComparer.OrdinalIgnoreCase);

        var untracked = deployedEndpoints
            .Where(e => !registeredEndpoints.Contains(e))
            .OrderBy(e => e)
            .ToArray();

        untracked.Should().BeEmpty(
            "every deployed endpoint must be tracked in EndpointRegistry.All. " +
            "Add missing endpoints to src/Honua.Server/EndpointRegistry.cs");
    }

    public void Dispose() => _factory.Dispose();

    private static string NormalizePath(string pattern)
    {
        var normalized = pattern.Trim();
        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        // Normalize version segment template to concrete v1 format used by EndpointRegistry.
        normalized = normalized.Replace("{version:apiVersion}", "1", StringComparison.OrdinalIgnoreCase);

        // Normalize constrained route tokens (e.g., {id:guid}, {layerId:int}) to plain token names.
        normalized = _routeConstraintRegex.Replace(normalized, "{$1}");

        if (normalized.Length > 1 && normalized[^1] == '/')
        {
            normalized = normalized.TrimEnd('/');
        }

        return normalized;
    }

    private static bool IsExcluded(string endpointKey)
    {
        if (_excludedExactPatterns.Contains(endpointKey))
        {
            return true;
        }

        var separatorIndex = endpointKey.IndexOf(' ');
        if (separatorIndex < 0 || separatorIndex == endpointKey.Length - 1)
        {
            return false;
        }

        var path = endpointKey[(separatorIndex + 1)..];

        if (!ShouldTrackPath(path))
        {
            return true;
        }

        // Static web assets from ASP.NET and Blazor host integration.
        if (path.StartsWith("/_content/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/_framework/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool ShouldTrackPath(string path)
    {
        return path.Equals("/csp-violation-report", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/openapi.json", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/healthz/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/odata", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/ogc/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/rest/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/tiles/", StringComparison.OrdinalIgnoreCase);
    }
}

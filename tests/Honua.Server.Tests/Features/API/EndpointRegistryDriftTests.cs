// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;
using Honua.Server;
using Honua.Server.Features.Wfs20;
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

    [Fact]
    [Trait("Category", "Architecture")]
    public void AllDeployedGrpcMethods_AreTrackedInOperationRegistry()
    {
        using var _ = _factory.CreateClient();
        var endpointSources = _factory.Services.GetServices<EndpointDataSource>();
        var deployedGrpcMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

                // Detect gRPC endpoints by checking for gRPC-specific metadata.
                // ASP.NET Core gRPC endpoints carry metadata from Grpc.* assemblies
                // alongside standard HttpMethodMetadata (POST).
                var isGrpcEndpoint = endpoint.Metadata
                    .Any(m => m.GetType().FullName?.StartsWith("Grpc.", StringComparison.Ordinal) == true);

                if (!isGrpcEndpoint)
                {
                    continue;
                }

                // Normalize: strip leading slash to get the package.Service/Method
                // format matching OperationRegistry entries.
                var normalizedPattern = pattern.TrimStart('/');

                // Exclude gRPC infrastructure (health checks, reflection, unimplemented handler)
                if (normalizedPattern.StartsWith("grpc.health.", StringComparison.OrdinalIgnoreCase) ||
                    normalizedPattern.StartsWith("grpc.reflection.", StringComparison.OrdinalIgnoreCase) ||
                    normalizedPattern.Contains("{unimplemented", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                deployedGrpcMethods.Add(normalizedPattern);
            }
        }

        var registeredGrpcOperations = OperationRegistry.All
            .Where(op => string.Equals(op.Protocol, "Grpc", StringComparison.OrdinalIgnoreCase))
            .Select(op => op.Operation)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var untracked = deployedGrpcMethods
            .Where(m => !registeredGrpcOperations.Contains(m))
            .OrderBy(m => m)
            .ToArray();

        untracked.Should().BeEmpty(
            "every deployed gRPC method must be tracked in OperationRegistry.All. " +
            "Add missing methods to src/Honua.Server/OperationRegistry.cs");

        var stale = registeredGrpcOperations
            .Where(op => !deployedGrpcMethods.Contains(op))
            .OrderBy(op => op)
            .ToArray();

        stale.Should().BeEmpty(
            "every Grpc entry in OperationRegistry.All must correspond to a deployed gRPC method. " +
            "Remove stale entries from src/Honua.Server/OperationRegistry.cs or re-deploy the service");
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void AllImplementedWfsOperations_AreTrackedInOperationRegistry()
    {
        var registeredWfsOperations = OperationRegistry.All
            .Where(op => string.Equals(op.Protocol, "WFS-2.0", StringComparison.OrdinalIgnoreCase))
            .Select(op => op.Operation)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var untracked = Wfs20DispatcherEndpoint.ImplementedOperations
            .Where(op => !registeredWfsOperations.Contains(op))
            .OrderBy(op => op)
            .ToArray();

        untracked.Should().BeEmpty(
            "every implemented WFS operation must be tracked in OperationRegistry.All. " +
            "Add missing operations to src/Honua.Server/OperationRegistry.cs");

        var stale = registeredWfsOperations
            .Where(op => !Wfs20DispatcherEndpoint.ImplementedOperations.Contains(op))
            .OrderBy(op => op)
            .ToArray();

        stale.Should().BeEmpty(
            "every WFS-2.0 entry in OperationRegistry.All must have a corresponding " +
            "implementation in the dispatcher. Remove stale entries from " +
            "src/Honua.Server/OperationRegistry.cs or implement the operation");
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
               path.Equals("/metrics", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/wfs", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/docs", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/healthz/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/odata", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/ogc/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/rest/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/tiles/", StringComparison.OrdinalIgnoreCase);
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Honua.Server;
using Honua.Server.Features.Protocols.Ogc.Classic.Wfs20;
using Honua.TestKit;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.API;

/// <summary>
/// Verifies that EndpointRegistry stays in sync with actually deployed endpoints.
/// Prevents drift where new endpoints are added to the app but not tracked in the registry.
/// </summary>
[Collection("Database")]
public sealed class EndpointRegistryDriftTests : IAsyncLifetime
{
    private static readonly Regex _routeConstraintRegex =
        new(@"\{([^{}:]+):[^{}]+\}", RegexOptions.Compiled);
    private static readonly Regex _integrationTestAttributeRegex =
        new(@"^\s*\[IntegrationTest\]", RegexOptions.Compiled);
    private static readonly Regex _methodDeclarationRegex =
        new(@"^\s*(?:public|private|internal|protected)\s+(?:async\s+)?(?:Task|ValueTask|void)\s+\w+\s*\(", RegexOptions.Compiled);
    private static readonly Regex _endpointAttributeRegex =
        new(@"\[Endpoint\(""([^""]+)""\)\]", RegexOptions.Compiled);

    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

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

    private static readonly HashSet<string> _conditionallyMappedRegistryPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        // Mapped only when ServeApiDocs/HONUA_SERVE_API_DOCS is enabled.
        "GET /docs",
        // These surfaces have direct endpoint-level tests, but ASP.NET's test-host
        // EndpointDataSource does not expose their route metadata consistently.
        "GET /monitoring/alerts",
        "GET /monitoring/health/comprehensive",
        "GET /monitoring/health/production",
        "GET /monitoring/metrics/cache",
        "GET /monitoring/metrics/connection-pool",
        "GET /monitoring/metrics/database-resilience",
        "GET /monitoring/metrics/resources",
        "GET /monitoring/metrics/upload-queue",
        "GET /samples/stac-ops",
        "GET /static/{serviceId}/{center}/{dimensions}.{format}",
        "GET /static/{serviceId}/bbox/{bbox}/{dimensions}.{format}",
        "POST /v1/grounding/spec/mutate",
        "POST /v1/grounding/spec/summarize",
        "GET /v1/spec/artifact/{hash}",
        "POST /v1/spec/apply",
        "POST /v1/spec/cancel",
        "POST /v1/spec/plan",
        "POST /v1/spec/validate",
    };

    [Fact]
    [Trait("Category", "Architecture")]
    public void AllDeployedEndpoints_AreTrackedInRegistry()
    {
        var endpointSources = _fixture.Services.GetServices<EndpointDataSource>();
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
                    var normalizedPath = NormalizePath(pattern);
                    var key = $"GET {normalizedPath}";
                    if (!IsExcluded(key))
                    {
                        deployedEndpoints.Add(key);
                    }

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

        var stale = registeredEndpoints
            .Where(e => !deployedEndpoints.Contains(e) && !_conditionallyMappedRegistryPatterns.Contains(e))
            .OrderBy(e => e)
            .ToArray();

        stale.Should().BeEmpty(
            "every HTTP endpoint in EndpointRegistry.All must correspond to a deployed endpoint. " +
            "Remove stale entries from src/Honua.Server/EndpointRegistry.cs or re-deploy the endpoint. " +
            "Stale entries: {0}",
            string.Join(", ", stale));
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void MetadataOpaqueRegistryEntries_AreCoveredByEndpointLevelIntegrationTests()
    {
        var testedEndpoints = CollectEndpointLevelIntegrationTestEndpoints(_conditionallyMappedRegistryPatterns);
        var missing = _conditionallyMappedRegistryPatterns
            .Where(endpoint => !testedEndpoints.Contains(endpoint))
            .OrderBy(endpoint => endpoint)
            .ToArray();

        missing.Should().BeEmpty(
            "EndpointRegistry entries excluded from EndpointDataSource stale checks must still have an endpoint-level integration test " +
            "that sends an HTTP request to the route instead of only declaring [Endpoint] metadata");
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void EndpointCoverageSuite_EndpointAttributesAreBackedByHttpRequests()
    {
        var sourceRoot = FindServerTestSourceRoot();
        var sourcePath = Path.Combine(sourceRoot, "Features", "API", "EndpointCoverageTests.cs");
        var source = File.ReadAllText(sourcePath);
        var declaredEndpoints = _endpointAttributeRegex
            .Matches(source)
            .Select(match => NormalizeEndpointKey(match.Groups[1].Value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var endpointLevelTests = CollectEndpointLevelIntegrationTestEndpoints(declaredEndpoints, sourcePath);
        var metadataOnly = declaredEndpoints
            .Where(endpoint => !endpointLevelTests.Contains(endpoint))
            .OrderBy(endpoint => endpoint)
            .ToArray();

        metadataOnly.Should().BeEmpty(
            "EndpointCoverageTests is the broad API-surface smoke suite, so each [Endpoint] there must be backed by a same-method HTTP request");
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void AllDeployedGrpcMethods_AreTrackedInOperationRegistry()
    {
        var endpointSources = _fixture.Services.GetServices<EndpointDataSource>();
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
               path.Equals("/mcp", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/healthz/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/odata", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/ogc/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/rest/", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/stac", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/stac/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/tiles/", StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> CollectEndpointLevelIntegrationTestEndpoints(
        IEnumerable<string> targetEndpointKeys,
        string? sourcePath = null)
    {
        var targets = targetEndpointKeys
            .Select(NormalizeEndpointKey)
            .ToArray();
        var endpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceFiles = sourcePath is null
            ? Directory.EnumerateFiles(FindServerTestSourceRoot(), "*.cs", SearchOption.AllDirectories)
            : new[] { sourcePath };

        foreach (var block in EnumerateIntegrationTestMethodBodies(sourceFiles))
        {
            foreach (var endpoint in targets)
            {
                if (endpoints.Contains(endpoint))
                {
                    continue;
                }

                if (EndpointIsExercisedByMethodBody(endpoint, block.Body))
                {
                    endpoints.Add(endpoint);
                }
            }
        }

        return endpoints;
    }

    private static IEnumerable<TestSourceBlock> EnumerateIntegrationTestMethodBodies(IEnumerable<string> sourceFiles)
    {
        foreach (var sourceFile in sourceFiles)
        {
            var lines = File.ReadAllLines(sourceFile);
            for (var index = 0; index < lines.Length; index++)
            {
                if (!_integrationTestAttributeRegex.IsMatch(lines[index]))
                {
                    continue;
                }

                var methodStart = -1;
                for (var cursor = index + 1; cursor < lines.Length; cursor++)
                {
                    if (_methodDeclarationRegex.IsMatch(lines[cursor]))
                    {
                        methodStart = cursor;
                        break;
                    }
                }

                if (methodStart < 0)
                {
                    continue;
                }

                var methodEnd = lines.Length;
                for (var cursor = methodStart + 1; cursor < lines.Length; cursor++)
                {
                    if (_integrationTestAttributeRegex.IsMatch(lines[cursor]))
                    {
                        methodEnd = cursor;
                        break;
                    }
                }

                var body = string.Join('\n', lines.Skip(methodStart).Take(methodEnd - methodStart));
                yield return new TestSourceBlock(body);
                index = methodEnd - 1;
            }
        }
    }

    private static bool EndpointIsExercisedByMethodBody(string endpointKey, string methodBody)
    {
        var separatorIndex = endpointKey.IndexOf(' ');
        if (separatorIndex < 1 || separatorIndex == endpointKey.Length - 1)
        {
            return false;
        }

        var method = endpointKey[..separatorIndex];
        var path = endpointKey[(separatorIndex + 1)..];

        return HasHttpVerbInvocation(methodBody, method) &&
            Regex.IsMatch(methodBody, BuildSourcePathRegex(path), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool HasHttpVerbInvocation(string methodBody, string method)
    {
        var pattern = method.ToUpperInvariant() switch
        {
            "GET" => @"(?:\.Get(?:FromJson)?|Get\w*)Async\s*\(|new\s+HttpRequestMessage\s*\(\s*HttpMethod\.Get\s*,",
            "POST" => @"(?:\.Post(?:AsJson)?|Post\w*)Async\s*\(|new\s+HttpRequestMessage\s*\(\s*HttpMethod\.Post\s*,",
            "PUT" => @"(?:\.Put(?:AsJson)?|Put\w*)Async\s*\(|new\s+HttpRequestMessage\s*\(\s*HttpMethod\.Put\s*,",
            "PATCH" => @"(?:\.Patch(?:AsJson)?|Patch\w*)Async\s*\(|new\s+HttpRequestMessage\s*\(\s*HttpMethod\.Patch\s*,",
            "DELETE" => @"(?:\.Delete|Delete\w*)Async\s*\(|new\s+HttpRequestMessage\s*\(\s*HttpMethod\.Delete\s*,",
            _ => null
        };

        return pattern is not null &&
            Regex.IsMatch(methodBody, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string BuildSourcePathRegex(string routePath)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < routePath.Length; index++)
        {
            if (routePath[index] == '{')
            {
                var tokenEnd = routePath.IndexOf('}', index);
                if (tokenEnd > index)
                {
                    // Match either a C# interpolation hole (e.g. {id}) or a concrete path value.
                    builder.Append(@"(?:\{[^{}""'\r\n]+\}|[^/""'\s?,)]+)");
                    index = tokenEnd;
                    continue;
                }
            }

            builder.Append(Regex.Escape(routePath[index].ToString()));
        }

        builder.Append(@"(?:\?[^""'\s)]*)?");
        return builder.ToString();
    }

    private static string FindServerTestSourceRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, "tests", "dotnet", "Honua.Server.Tests");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate tests/dotnet/Honua.Server.Tests from the test execution directory.");
    }

    private static string NormalizeEndpointKey(string endpoint)
    {
        var trimmed = endpoint.Trim();
        var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
            ? $"{parts[0].ToUpperInvariant()} {NormalizePath(parts[1])}"
            : trimmed;
    }

    private sealed record TestSourceBlock(string Body);
}

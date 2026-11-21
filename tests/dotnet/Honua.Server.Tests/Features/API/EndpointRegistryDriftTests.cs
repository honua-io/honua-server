// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Honua.Server;
using Honua.Protocols.Ogc.Classic.Wfs20;
using Honua.TestKit;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.API;

// The host is only read (its endpoint data sources), so one host serves the whole class.
public sealed class EndpointRegistryDriftTests(WebAppFixture fixture) : IClassFixture<WebAppFixture>
{
    private const RegexOptions SourceMatchOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex _routeConstraintRegex =
        new(@"\{([^{}:]+):[^{}]+\}", RegexOptions.Compiled);
    private static readonly Regex _integrationTestAttributeRegex =
        new(@"^\s*\[IntegrationTest\]", RegexOptions.Compiled);
    private static readonly Regex _methodDeclarationRegex =
        new(@"^\s*(?:public|private|internal|protected)\s+(?:async\s+)?(?:Task|ValueTask|void)\s+\w+\s*\(", RegexOptions.Compiled);
    private static readonly Regex _endpointAttributeRegex =
        new(@"\[Endpoint\(""([^""]+)""\)\]", RegexOptions.Compiled);

    // HTTP-verb call-site patterns per method, shared by every method body scan.
    private static readonly ConcurrentDictionary<string, Regex[]> _verbInvocationPatterns = new(StringComparer.Ordinal);

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
        // Explicit Development/Test fixture endpoint, mapped only when
        // OperateObservabilityFixture:Enabled=true.
        "POST /api/v1/admin/dev/fixtures/operate-observability/{profile}",
        // RFC 9728 protected-resource metadata. Mapped only when the host has a
        // valid OIDC authority configured; with no authorization server the surface is
        // deliberately absent rather than serving an empty authorization_servers array,
        // which would be a false capability claim. The default test host
        // configures no authority, so the route is not deployed here. Both states are
        // proven directly by McpProtectedResourceMetadataTests in Honua.Ai.Tests
        // (…_WithOidcConfigured_ReturnsRfc9728Document asserts it appears when an
        // authority exists; …_WithNoAuthorizationServerConfigured_IsAbsent asserts the
        // 404 when none does), each against its own fixture.
        "GET /.well-known/oauth-protected-resource/mcp",
        // These surfaces have direct endpoint-level tests, but ASP.NET's test-host
        // EndpointDataSource does not expose their route metadata consistently.
        // The canonical ArcGIS Server Admin service resource is exercised with
        // both verbs by BranchVersioningMetadataTests.
        "GET /admin/services/{serviceName}.{serviceType}",
        "POST /admin/services/{serviceName}.{serviceType}",
        "GET /admin/services/{folderName}/{serviceName}.{serviceType}",
        "POST /admin/services/{folderName}/{serviceName}.{serviceType}",
        "GET /monitoring/alerts",
        "GET /monitoring/health/comprehensive",
        "GET /monitoring/health/production",
        "GET /monitoring/metrics/cache",
        "GET /monitoring/metrics/connection-pool",
        "GET /monitoring/metrics/database-resilience",
        "GET /monitoring/metrics/resources",
        "GET /monitoring/metrics/upload-queue",
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
        var endpointSources = fixture.Services.GetServices<EndpointDataSource>();
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

                deployedEndpoints.UnionWith(httpMethods
                    .Select(method => $"{method.ToUpperInvariant()} {NormalizePath(pattern)}")
                    .Where(key => !IsExcluded(key)));
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
    public void AllEndpointRegistryEntries_AreBackedByHttpRequests()
    {
        var declaredEndpoints = EndpointRegistry.All
            .Select(endpoint => NormalizeEndpointKey($"{endpoint.Method} {endpoint.Path}"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var endpointLevelTests = CollectEndpointLevelIntegrationTestEndpoints(declaredEndpoints);
        var metadataOnly = declaredEndpoints
            .Where(endpoint => !endpointLevelTests.Contains(endpoint))
            .OrderBy(endpoint => endpoint)
            .ToArray();

        metadataOnly.Should().BeEmpty(
            "every EndpointRegistry entry must be backed by a same-method HTTP request somewhere in the integration suite");
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void AllDeployedGrpcMethods_AreTrackedInOperationRegistry()
    {
        var endpointSources = fixture.Services.GetServices<EndpointDataSource>();
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
        return path.Equals("/", StringComparison.Ordinal) ||
               path.Equals("/rest", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/arcgis", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/csp-violation-report", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/openapi.json", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/metrics", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/oauth/token", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/admin/auth/callback", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/wfs", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/wps", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/wps/", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/docs", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/mcp", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/healthz/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/odata", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/edr", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/edr/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/ogc/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/rest/", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/services", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/services/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/sharing/", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/stac", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/stac/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/sta/", StringComparison.OrdinalIgnoreCase) ||
               // Enterprise identity provisioning + SSO surfaces (SCIM 2.0, SAML 2.0).
               // These routes are mapped unconditionally in Program.cs and tracked in
               // EndpointRegistry.All, so the drift comparison must include them.
               path.StartsWith("/scim/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/saml/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/scenes/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/terrain/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/elevation/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/tiles/", StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> CollectEndpointLevelIntegrationTestEndpoints(
        IEnumerable<string> targetEndpointKeys,
        string? sourcePath = null)
    {
        // Each endpoint's source-path pattern is built once. Regex's static cache holds 15 patterns,
        // so building one per (endpoint, method body) pair re-parsed every pattern for every body.
        var matchers = targetEndpointKeys
            .Select(NormalizeEndpointKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(EndpointSourceMatcher.TryCreate)
            .OfType<EndpointSourceMatcher>()
            .ToArray();
        var endpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceFiles = sourcePath is null
            ? FindIntegrationTestSourceRoots()
                .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            : new[] { sourcePath };

        foreach (var block in EnumerateIntegrationTestMethodBodies(sourceFiles))
        {
            var verbsInBody = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var matcher in matchers.Where(matcher => !endpoints.Contains(matcher.Key)))
            {
                if (matcher.IsExercisedBy(block.Body, verbsInBody))
                {
                    endpoints.Add(matcher.Key);
                }
            }
        }

        return endpoints;
    }

    private static IEnumerable<TestSourceBlock> EnumerateIntegrationTestMethodBodies(IEnumerable<string> sourceFiles)
    {
        // Not a straightforward .Select(...) candidate: this is a yield-return iterator with a
        // nested scan loop and mutable cursor state (methodStart/methodEnd/index) per source file,
        // not a simple per-element map.
        foreach (var lines in (sourceFiles).Select(sourceFile => File.ReadAllLines(sourceFile)))
        {
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

    private static bool HasHttpVerbInvocation(string methodBody, string method)
        => _verbInvocationPatterns
            .GetOrAdd(method, BuildVerbInvocationPatterns)
            .Any(pattern => pattern.IsMatch(methodBody));

    private static Regex[] BuildVerbInvocationPatterns(string method)
    {
        var pattern = method switch
        {
            // The optional (?:<[^()]*>)? segment lets the verb match generic
            // typed-deserialization helpers such as GetFromJsonAsync<T>(...) where a
            // generic type-argument list sits between the method name and the call's
            // opening parenthesis. Without it, an endpoint exercised only via
            // GetFromJsonAsync<T> (e.g. GET /api/v1/admin/oauth-scopes) is reported as
            // uncovered even though it has a backing HTTP request.
            // ConnectAsync(...) matches a WebSocket upgrade, which the server routes as an
            // HTTP GET endpoint (e.g. GET /api/v1/maps/{mapId}/collaboration/sessions/stream);
            // those routes are exercised by ws-client ConnectAsync rather than a plain GetAsync.
            "GET" => @"(?:\.Get(?:FromJson)?|Get\w*)Async(?:<[^()]*>)?\s*\(|\.ConnectAsync\s*\(|new\s+HttpRequestMessage\s*\(\s*HttpMethod\.Get\s*,",
            "POST" => @"(?:\.Post(?:AsJson)?|Post\w*)Async(?:<[^()]*>)?\s*\(|new\s+HttpRequestMessage\s*\(\s*HttpMethod\.Post\s*,",
            "PUT" => @"(?:\.Put(?:AsJson)?|Put\w*)Async(?:<[^()]*>)?\s*\(|new\s+HttpRequestMessage\s*\(\s*HttpMethod\.Put\s*,",
            "PATCH" => @"(?:\.Patch(?:AsJson)?|Patch\w*)Async(?:<[^()]*>)?\s*\(|new\s+HttpRequestMessage\s*\(\s*HttpMethod\.Patch\s*,",
            "DELETE" => @"(?:\.Delete|Delete\w*)Async(?:<[^()]*>)?\s*\(|new\s+HttpRequestMessage\s*\(\s*HttpMethod\.Delete\s*,",
            "HEAD" => @"new\s+HttpRequestMessage\s*\(\s*HttpMethod\.Head\s*,",
            _ => null
        };

        var literal = Regex.Escape(method);
        var literalHttpMethodPattern =
            $@"new\s+HttpRequestMessage\s*\(\s*new\s+HttpMethod\s*\(\s*""{literal}""\s*\)\s*,";
        var dynamicHttpMethodPattern =
            $@"new\s+HttpMethod\s*\(\s*\w+\s*\).*""{literal}""|""{literal}"".*new\s+HttpMethod\s*\(\s*\w+\s*\)";
        var patterns = new List<Regex>(3);
        if (pattern is not null)
        {
            patterns.Add(new Regex(pattern, SourceMatchOptions | RegexOptions.Compiled));
        }

        patterns.Add(new Regex(literalHttpMethodPattern, SourceMatchOptions | RegexOptions.Compiled));
        patterns.Add(new Regex(dynamicHttpMethodPattern, SourceMatchOptions | RegexOptions.Compiled | RegexOptions.Singleline));
        return patterns.ToArray();
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

            builder.Append(routePath[index] == ' '
                ? @"(?:\s|%20|\+)"
                : Regex.Escape(routePath[index].ToString()));
        }

        builder.Append(@"(?:\?[^""'\s)]*)?");
        return builder.ToString();
    }

    private static string FindServerTestSourceRoot()
    {
        var starts = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
        for (var i = 0; i < starts.Length; i++)
        {
            var directory = new DirectoryInfo(starts[i]);
            while (directory is not null)
            {
                // "tests", "dotnet", "Honua.Server.Tests" are fixed relative literals (never rooted),
                // so Path.Combine cannot drop directory.FullName here.
                var candidate = Path.Join(directory.FullName, "tests", "dotnet", "Honua.Server.Tests");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate tests/dotnet/Honua.Server.Tests from the test execution directory.");
    }

    private static List<string> FindIntegrationTestSourceRoots()
    {
        var serverTests = FindServerTestSourceRoot();
        var repositoryRoot = Directory.GetParent(serverTests)!.Parent!.Parent!.FullName;
        using var catalog = JsonDocument.Parse(File.ReadAllText(
            Path.Join(repositoryRoot, ".github", "test-projects.json")));
        var roots = new List<string>();
        foreach (var project in catalog.RootElement.GetProperty("projects").EnumerateObject())
        {
            var entry = project.Value;
            var role = entry.GetProperty("role").GetString();
            if (!entry.GetProperty("coverageRequired").GetBoolean() || role is not ("mixed" or "component"))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(Path.Join(repositoryRoot, project.Name))!;
            if (!Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException($"Catalogued integration source directory is missing: {directory}");
            }

            roots.Add(directory);
        }

        return roots.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void IntegrationSourceDiscovery_IncludesComponentHosts_AndExcludesUnitSources()
    {
        var roots = FindIntegrationTestSourceRoots().Select(Path.GetFileName).ToArray();
        roots.Should().Contain("Honua.Protocols.GeoServices.ComponentTests");
        roots.Should().Contain("Honua.Ai.ComponentTests");
        roots.Should().Contain("Honua.Server.Tests");
        roots.Should().NotContain("Honua.Protocols.GeoServices.UnitTests");
        roots.Should().NotContain("Honua.Architecture.Tests");
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

    // One registry endpoint and its source-path pattern. A method body exercises the endpoint when it
    // makes a same-method HTTP call and names the route; the verb result is shared per body.
    private sealed class EndpointSourceMatcher
    {
        private readonly string _method;
        private readonly Regex _path;

        private EndpointSourceMatcher(string key, string method, string path)
        {
            Key = key;
            _method = method;
            _path = new Regex(BuildSourcePathRegex(path), SourceMatchOptions);
        }

        public string Key { get; }

        public static EndpointSourceMatcher? TryCreate(string endpointKey)
        {
            var separatorIndex = endpointKey.IndexOf(' ');
            return separatorIndex < 1 || separatorIndex == endpointKey.Length - 1
                ? null
                : new EndpointSourceMatcher(
                    endpointKey, endpointKey[..separatorIndex].ToUpperInvariant(), endpointKey[(separatorIndex + 1)..]);
        }

        public bool IsExercisedBy(string methodBody, Dictionary<string, bool> verbsInBody)
        {
            if (!verbsInBody.TryGetValue(_method, out var hasVerb))
            {
                hasVerb = HasHttpVerbInvocation(methodBody, _method);
                verbsInBody[_method] = hasVerb;
            }

            return hasVerb && _path.IsMatch(methodBody);
        }
    }
}

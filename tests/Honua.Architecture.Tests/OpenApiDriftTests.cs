// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Server;

namespace Honua.Architecture.Tests;

/// <summary>
/// Ensures OpenAPI specifications stay in sync with the registered OGC endpoints.
/// </summary>
public sealed class OpenApiDriftTests
{
    private static readonly HashSet<string> _supportedMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "get",
        "post",
        "put",
        "delete",
        "patch"
    };

    [ArchitectureTest]
    public void OpenApiSpecs_AlignWithEndpointRegistry()
    {
        var featuresSpecEndpoints = LoadOpenApiEndpoints(ResolveOpenApiPath("openapi.json"));
        var featuresRegistryEndpoints = EndpointRegistry.All
            .Where(endpoint => endpoint.Path.StartsWith("/ogc/features", StringComparison.OrdinalIgnoreCase))
            .Where(endpoint => !endpoint.Path.Equals("/openapi.json", StringComparison.OrdinalIgnoreCase))
            .Select(endpoint => FormatKey(endpoint.Method, endpoint.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        AssertSpecMatchesRegistry(
            "OGC API Features",
            featuresSpecEndpoints,
            featuresRegistryEndpoints);

        var tilesSpecEndpoints = LoadOpenApiEndpoints(ResolveOpenApiPath("ogc-tiles-openapi.json"));
        var tilesRegistryEndpoints = EndpointRegistry.All
            .Where(endpoint => endpoint.Path.StartsWith("/ogc/tiles", StringComparison.OrdinalIgnoreCase))
            .Select(endpoint => FormatKey(endpoint.Method, endpoint.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        AssertSpecMatchesRegistry(
            "OGC API Tiles",
            tilesSpecEndpoints,
            tilesRegistryEndpoints);

        var mapsSpecEndpoints = LoadOpenApiEndpoints(ResolveOpenApiPath("ogc-maps-openapi.json"));
        var mapsRegistryEndpoints = EndpointRegistry.All
            .Where(endpoint => endpoint.Path.StartsWith("/ogc/maps", StringComparison.OrdinalIgnoreCase))
            .Select(endpoint => FormatKey(endpoint.Method, endpoint.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        AssertSpecMatchesRegistry(
            "OGC API Maps",
            mapsSpecEndpoints,
            mapsRegistryEndpoints);
    }

    [ArchitectureTest]
    public void SpatialAnalyticsResponses_UseDedicatedFeatureCollectionSchema()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ResolveOpenApiPath("openapi.json")));
        var root = document.RootElement;
        var paths = root.GetProperty("paths");

        foreach (var path in new[]
                 {
                     "/collections/{collectionId}/clusters",
                     "/collections/{collectionId}/spatial-join",
                     "/collections/{collectionId}/buffer-aggregate",
                     "/collections/{collectionId}/density"
                 })
        {
            var schemaRef = paths.GetProperty(path)
                .GetProperty("post")
                .GetProperty("responses")
                .GetProperty("200")
                .GetProperty("content")
                .GetProperty("application/geo+json")
                .GetProperty("schema")
                .GetProperty("$ref")
                .GetString();

            schemaRef.Should().Be("#/components/schemas/SpatialAnalyticsFeatureCollection");
        }

        var h3SchemaRef = paths.GetProperty("/collections/{collectionId}/h3")
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content")
            .GetProperty("application/geo+json")
            .GetProperty("schema")
            .GetProperty("$ref")
            .GetString();

        h3SchemaRef.Should().Be("#/components/schemas/FeatureCollection");

        var analyticsSchema = root.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("SpatialAnalyticsFeatureCollection");

        analyticsSchema.GetProperty("properties")
            .GetProperty("metadata")
            .GetProperty("$ref")
            .GetString()
            .Should().Be("#/components/schemas/SpatialAnalyticsMetadata");
    }

    [ArchitectureTest]
    public void SpatialAnalyticsRequestSchemas_AdvertiseSharedFilters_AndDensityAvoidsUnsupportedStatistics()
    {
        string[] analyticsPaths =
        [
            "/collections/{collectionId}/clusters",
            "/collections/{collectionId}/spatial-join",
            "/collections/{collectionId}/buffer-aggregate",
            "/collections/{collectionId}/density"
        ];

        string[] sharedFilters =
        [
            "where",
            "objectIds",
            "geometry",
            "geometryType",
            "inSR",
            "spatialRel",
            "time",
            "timeRelation"
        ];

        foreach (var specPath in GetAnalyticsContractSpecPaths())
        {
            using var document = JsonDocument.Parse(File.ReadAllText(specPath));
            var root = document.RootElement;
            var paths = root.GetProperty("paths");

            foreach (var path in analyticsPaths)
            {
                var propertyNames = GetRequestBodyPropertyNames(paths, path);
                propertyNames.Should().Contain(sharedFilters, $"{specPath} should document the shared analytics filters on {path}");
            }

            var densityOperation = paths.GetProperty("/collections/{collectionId}/density").GetProperty("post");
            GetRequestBodyPropertyNames(paths, "/collections/{collectionId}/density")
                .Should()
                .NotContain("outStatistics", $"{specPath} should not advertise unsupported density statistics");

            densityOperation.GetProperty("description")
                .GetString()
                .Should()
                .Contain("counts or weighted sums")
                .And.NotContain("statistics per cell");

            densityOperation.GetProperty("requestBody")
                .GetProperty("description")
                .GetString()
                .Should()
                .Contain("shared filters")
                .And.NotContain("outStatistics");
        }
    }

    private static void AssertSpecMatchesRegistry(
        string specName,
        HashSet<string> specEndpoints,
        HashSet<string> registryEndpoints)
    {
        var missingFromRegistry = specEndpoints
            .Except(registryEndpoints)
            .OrderBy(value => value)
            .ToArray();

        missingFromRegistry.Should()
            .BeEmpty($"{specName} OpenAPI endpoints must exist in EndpointRegistry");

        var missingFromSpec = registryEndpoints
            .Except(specEndpoints)
            .OrderBy(value => value)
            .ToArray();

        missingFromSpec.Should()
            .BeEmpty($"{specName} OpenAPI specification must include all registered endpoints");
    }

    private static HashSet<string> LoadOpenApiEndpoints(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("OpenAPI specification not found.", path);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        var basePaths = GetServerBasePaths(root);
        var endpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!root.TryGetProperty("paths", out var pathsElement))
        {
            return endpoints;
        }

        foreach (var pathEntry in pathsElement.EnumerateObject())
        {
            var pathValue = pathEntry.Value;
            foreach (var methodEntry in pathValue.EnumerateObject())
            {
                if (!_supportedMethods.Contains(methodEntry.Name))
                {
                    continue;
                }

                foreach (var basePath in basePaths)
                {
                    var fullPath = CombinePath(basePath, pathEntry.Name);
                    endpoints.Add(FormatKey(methodEntry.Name, fullPath));
                }
            }
        }

        return endpoints;
    }

    private static List<string> GetServerBasePaths(JsonElement root)
    {
        if (root.TryGetProperty("servers", out var serversElement) &&
            serversElement.ValueKind == JsonValueKind.Array)
        {
            var servers = new List<string>();
            foreach (var server in serversElement.EnumerateArray())
            {
                if (server.TryGetProperty("url", out var urlElement) &&
                    urlElement.GetString() is { Length: > 0 } url)
                {
                    servers.Add(url);
                }
            }

            if (servers.Count > 0)
            {
                return servers;
            }
        }

        return new List<string> { string.Empty };
    }

    private static string CombinePath(string basePath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return NormalizePath(relativePath);
        }

        var combined = $"{basePath.TrimEnd('/')}/{relativePath.TrimStart('/')}";
        return NormalizePath(combined);
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var normalized = path.Trim();
        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        if (normalized.Length > 1 && normalized.EndsWith('/'))
        {
            normalized = normalized.TrimEnd('/');
        }

        return normalized;
    }

    private static string FormatKey(string method, string path)
    {
        var normalizedMethod = method.Trim().ToUpperInvariant();
        var normalizedPath = NormalizePath(path);
        return $"{normalizedMethod} {normalizedPath}";
    }

    private static string ResolveOpenApiPath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        if (directory == null)
        {
            throw new FileNotFoundException("Unable to locate repository root for OpenAPI specifications.");
        }

        return Path.Combine(directory.FullName, "src", "Honua.Server", fileName);
    }

    private static string[] GetAnalyticsContractSpecPaths()
        =>
        [
            ResolveOpenApiPath("openapi.json"),
            ResolveDeveloperOpenApiPath("ogc-api-features.json")
        ];

    private static string ResolveDeveloperOpenApiPath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        if (directory == null)
        {
            throw new FileNotFoundException("Unable to locate repository root for developer OpenAPI specifications.");
        }

        return Path.Combine(directory.FullName, "docs", "developer", "api-specs", fileName);
    }

    private static HashSet<string> GetRequestBodyPropertyNames(JsonElement paths, string path)
    {
        return paths.GetProperty(path)
            .GetProperty("post")
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema")
            .GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}

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

        var coveragesSpecEndpoints = LoadOpenApiEndpoints(ResolveOpenApiPath("ogc-coverages-openapi.json"));
        var coveragesRegistryEndpoints = EndpointRegistry.All
            .Where(endpoint => endpoint.Path.StartsWith("/ogc/coverages", StringComparison.OrdinalIgnoreCase))
            .Select(endpoint => FormatKey(endpoint.Method, endpoint.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        AssertSpecMatchesRegistry(
            "OGC API Coverages",
            coveragesSpecEndpoints,
            coveragesRegistryEndpoints);

        var processesSpecEndpoints = LoadOpenApiEndpoints(ResolveOpenApiPath("ogc-processes-openapi.json"));
        var processesRegistryEndpoints = EndpointRegistry.All
            .Where(endpoint => endpoint.Path.StartsWith("/ogc/processes", StringComparison.OrdinalIgnoreCase))
            .Select(endpoint => FormatKey(endpoint.Method, endpoint.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        AssertSpecMatchesRegistry(
            "OGC API Processes",
            processesSpecEndpoints,
            processesRegistryEndpoints);
    }

    [ArchitectureTest]
    public void OgcProcessesSchemas_IncludeFieldsEmittedByEndpoints()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ResolveOpenApiPath("ogc-processes-openapi.json")));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        var processSummaryProps = schemas.GetProperty("ProcessSummary")
            .GetProperty("properties");
        processSummaryProps.TryGetProperty("links", out _).Should()
            .BeTrue("ProcessSummary must document the links array emitted by GetProcessList");

        var statusInfoProps = schemas.GetProperty("StatusInfo")
            .GetProperty("properties");
        statusInfoProps.TryGetProperty("type", out _).Should()
            .BeTrue("StatusInfo must document the type discriminator emitted by all job status responses");

        // Execute schema must require inputs.plan (server enforces this at runtime).
        var execute = schemas.GetProperty("Execute");
        execute.TryGetProperty("required", out var executeRequired).Should()
            .BeTrue("Execute must declare required properties");
        executeRequired.EnumerateArray().Select(e => e.GetString())
            .Should().Contain("inputs", "Execute must require 'inputs'");

        var executeInputs = execute.GetProperty("properties").GetProperty("inputs");
        executeInputs.TryGetProperty("required", out var inputsRequired).Should()
            .BeTrue("Execute.inputs must declare required properties");
        inputsRequired.EnumerateArray().Select(e => e.GetString())
            .Should().Contain("plan", "Execute.inputs must require 'plan'");

        // AnalysisPlan schema must describe the minimum accepted shape.
        var plan = schemas.GetProperty("AnalysisPlan");
        plan.TryGetProperty("required", out var planRequired).Should().BeTrue();
        var planRequiredFields = planRequired.EnumerateArray().Select(e => e.GetString()).ToList();
        planRequiredFields.Should().Contain("planId").And.Contain("steps");

        // PlanStep must expose the optional processId field documented in API examples
        // and aligned with the canonical AnalysisPlanStep.process_id proto field.
        var planStepProps = schemas.GetProperty("PlanStep").GetProperty("properties");
        planStepProps.TryGetProperty("processId", out _).Should()
            .BeTrue("PlanStep must document the optional processId field used in request examples and the canonical proto contract");

        // ProcessIoSchema must expose the fields emitted by process description endpoints.
        var ioSchema = schemas.GetProperty("ProcessIoSchema");
        var ioSchemaProps = ioSchema.GetProperty("properties");
        ioSchemaProps.TryGetProperty("type", out _).Should()
            .BeTrue("ProcessIoSchema must document the 'type' field emitted by process descriptions");
        ioSchemaProps.TryGetProperty("contentMediaType", out _).Should()
            .BeTrue("ProcessIoSchema must document the 'contentMediaType' field emitted by process descriptions");

        // InputDescription and OutputDescription must reference ProcessIoSchema.
        foreach (var schemaName in new[] { "InputDescription", "OutputDescription" })
        {
            var schemaRef = schemas.GetProperty(schemaName)
                .GetProperty("properties")
                .GetProperty("schema")
                .GetProperty("$ref")
                .GetString();
            schemaRef.Should().Be("#/components/schemas/ProcessIoSchema",
                $"{schemaName}.schema must reference ProcessIoSchema");
        }
    }

    [ArchitectureTest]
    public void OgcProcessesAuthProtectedEndpoints_Document401And403Responses()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ResolveOpenApiPath("ogc-processes-openapi.json")));
        var paths = document.RootElement.GetProperty("paths");

        (string Path, string Method)[] authProtectedOps =
        [
            ("/ogc/processes/processes/{processId}/execution", "post"),
            ("/ogc/processes/jobs", "get"),
            ("/ogc/processes/jobs/{jobId}", "get"),
            ("/ogc/processes/jobs/{jobId}", "delete"),
            ("/ogc/processes/jobs/{jobId}/results", "get")
        ];

        foreach (var (path, method) in authProtectedOps)
        {
            var responses = paths.GetProperty(path)
                .GetProperty(method)
                .GetProperty("responses");

            responses.TryGetProperty("401", out _).Should()
                .BeTrue($"{method.ToUpperInvariant()} {path} must document 401 Unauthorized");

            responses.TryGetProperty("403", out _).Should()
                .BeTrue($"{method.ToUpperInvariant()} {path} must document 403 Forbidden");
        }
    }

    [ArchitectureTest]
    public void OgcMapsTileSetEndpoints_DescribeRuntimeParameterAndErrorContracts()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ResolveOpenApiPath("ogc-maps-openapi.json")));
        var paths = document.RootElement.GetProperty("paths");

        var tileSets = paths.GetProperty("/ogc/maps/collections/{collectionId}/map/tiles").GetProperty("get");
        var tileSetsCollectionId = tileSets.GetProperty("parameters").EnumerateArray()
            .First(parameter => parameter.GetProperty("name").GetString() == "collectionId");
        tileSetsCollectionId.GetProperty("schema").GetProperty("type").GetString().Should().Be("integer");
        tileSets.GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString()
            .Should().Be("#/components/schemas/TileSetsList");
        tileSets.GetProperty("responses").TryGetProperty("400", out _).Should().BeTrue();
        tileSets.GetProperty("responses").TryGetProperty("404", out _).Should().BeTrue();

        var tileSet = paths.GetProperty("/ogc/maps/collections/{collectionId}/map/tiles/{tileMatrixSetId}").GetProperty("get");
        var tileSetCollectionId = tileSet.GetProperty("parameters").EnumerateArray()
            .First(parameter => parameter.GetProperty("name").GetString() == "collectionId");
        tileSetCollectionId.GetProperty("schema").GetProperty("type").GetString().Should().Be("integer");
        tileSet.GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString()
            .Should().Be("#/components/schemas/TileSet");
        tileSet.GetProperty("responses").TryGetProperty("400", out _).Should().BeTrue();
        tileSet.GetProperty("responses").TryGetProperty("404", out _).Should().BeTrue();
    }

    [ArchitectureTest]
    public void OgcMapsMapEndpoints_DescribeRuntimeMediaTypes_AndAuthErrors()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ResolveOpenApiPath("ogc-maps-openapi.json")));
        var paths = document.RootElement.GetProperty("paths");

        var openApiDocument = paths.GetProperty("/ogc/maps/openapi.json").GetProperty("get");
        openApiDocument.GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content")
            .TryGetProperty("application/vnd.oai.openapi+json", out _)
            .Should()
            .BeTrue();

        var datasetMap = paths.GetProperty("/ogc/maps/map").GetProperty("get");
        var datasetResponses = datasetMap.GetProperty("responses");
        var datasetContent = datasetResponses.GetProperty("200").GetProperty("content");
        datasetContent.TryGetProperty("image/png", out _).Should().BeTrue();
        datasetContent.TryGetProperty("image/jpeg", out _).Should().BeTrue();
        datasetContent.TryGetProperty("image/tiff", out _).Should().BeTrue();
        datasetResponses.TryGetProperty("400", out _).Should().BeTrue();
        datasetResponses.TryGetProperty("401", out _).Should().BeTrue();
        datasetResponses.TryGetProperty("403", out _).Should().BeTrue();
        datasetResponses.TryGetProperty("404", out _).Should().BeTrue();

        var collectionMap = paths.GetProperty("/ogc/maps/collections/{collectionId}/map").GetProperty("get");
        var collectionIdParameter = collectionMap.GetProperty("parameters").EnumerateArray()
            .First(parameter => parameter.GetProperty("name").GetString() == "collectionId");
        collectionIdParameter.GetProperty("schema").GetProperty("type").GetString().Should().Be("integer");
        var collectionResponses = collectionMap.GetProperty("responses");
        var collectionContent = collectionResponses.GetProperty("200").GetProperty("content");
        collectionContent.TryGetProperty("image/png", out _).Should().BeTrue();
        collectionContent.TryGetProperty("image/jpeg", out _).Should().BeTrue();
        collectionContent.TryGetProperty("image/tiff", out _).Should().BeTrue();
        collectionResponses.TryGetProperty("400", out _).Should().BeTrue();
        collectionResponses.TryGetProperty("401", out _).Should().BeTrue();
        collectionResponses.TryGetProperty("403", out _).Should().BeTrue();
        collectionResponses.TryGetProperty("404", out _).Should().BeTrue();
    }

    [ArchitectureTest]
    public void OgcTilesTileEndpoints_DescribeRasterFormats_AndValidationResponses()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ResolveOpenApiPath("ogc-tiles-openapi.json")));
        var paths = document.RootElement.GetProperty("paths");

        AssertCollectionIdParameterIsInteger(paths.GetProperty("/collections/{collectionId}").GetProperty("get"));
        AssertMetadataEndpointHasValidationResponses(paths.GetProperty("/collections/{collectionId}").GetProperty("get"));
        AssertMetadataEndpointHasValidationResponses(paths.GetProperty("/tiles").GetProperty("get"));
        AssertMetadataEndpointHasValidationResponses(paths.GetProperty("/tiles/{tileMatrixSetId}").GetProperty("get"));
        AssertCollectionIdParameterIsInteger(paths.GetProperty("/collections/{collectionId}/tiles").GetProperty("get"));
        AssertMetadataEndpointHasValidationResponses(paths.GetProperty("/collections/{collectionId}/tiles").GetProperty("get"));
        AssertCollectionIdParameterIsInteger(paths.GetProperty("/collections/{collectionId}/tiles/{tileMatrixSetId}").GetProperty("get"));
        AssertMetadataEndpointHasValidationResponses(paths.GetProperty("/collections/{collectionId}/tiles/{tileMatrixSetId}").GetProperty("get"));
        AssertMetadataEndpointHasValidationResponses(paths.GetProperty("/tileMatrixSets").GetProperty("get"));
        AssertMetadataEndpointHasValidationResponses(paths.GetProperty("/tileMatrixSets/{tileMatrixSetId}").GetProperty("get"));
        AssertTileEndpoint(paths.GetProperty("/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}").GetProperty("get"));
        var collectionTileEndpoint = paths.GetProperty("/collections/{collectionId}/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}").GetProperty("get");
        AssertCollectionIdParameterIsInteger(collectionTileEndpoint);
        AssertTileEndpoint(collectionTileEndpoint);
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

    private static void AssertTileEndpoint(JsonElement tileEndpoint)
    {
        var formatParameter = tileEndpoint.GetProperty("parameters").EnumerateArray()
            .First(parameter => parameter.GetProperty("name").GetString() == "f");
        var formats = formatParameter.GetProperty("schema")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToArray();
        formats.Should().Contain("mvt").And.Contain("png");

        var responses = tileEndpoint.GetProperty("responses");
        var successContent = responses.GetProperty("200").GetProperty("content");
        successContent.TryGetProperty("application/vnd.mapbox-vector-tile", out _).Should().BeTrue();
        successContent.TryGetProperty("image/png", out _).Should().BeTrue();
        responses.TryGetProperty("400", out _).Should().BeTrue();
        responses.TryGetProperty("404", out _).Should().BeTrue();
        responses.TryGetProperty("406", out _).Should().BeTrue();
    }

    private static void AssertCollectionIdParameterIsInteger(JsonElement operation)
    {
        var collectionIdParameter = operation.GetProperty("parameters").EnumerateArray()
            .First(parameter => parameter.GetProperty("name").GetString() == "collectionId");
        collectionIdParameter.GetProperty("schema").GetProperty("type").GetString().Should().Be("integer");
        collectionIdParameter.GetProperty("schema").GetProperty("minimum").GetInt32().Should().Be(0);
    }

    private static void AssertMetadataEndpointHasValidationResponses(JsonElement operation)
    {
        var responses = operation.GetProperty("responses");
        responses.TryGetProperty("400", out _).Should().BeTrue();
        responses.TryGetProperty("406", out _).Should().BeTrue();
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

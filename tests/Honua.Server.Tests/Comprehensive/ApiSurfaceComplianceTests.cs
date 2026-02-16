// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Honua.TestKit.Performance;
using Honua.TestKit.Security;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Comprehensive;

/// <summary>
/// Comprehensive API surface compliance tests ensuring 100% endpoint coverage
/// with security, performance, and edge case validation.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Comprehensive)]
public class ApiSurfaceComplianceTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private readonly ITestOutputHelper _output;
    private string _schema = string.Empty;

    public ApiSurfaceComplianceTests(ITestOutputHelper output)
    {
        _output = output;
        _fixture.ReplaceService<ILayerCatalog, Honua.Postgres.Features.Catalog.PostgresLayerCatalog>();
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _schema = await _fixture.CreateIsolatedSchemaAsync(nameof(ApiSurfaceComplianceTests));
        await ServerTestData.SeedAsync(_fixture.Postgres, _schema);
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    /// <summary>
    /// Tests all health check endpoints for availability and response format.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.HealthCheck)]
    [Endpoint("GET /healthz/live")]
    [Endpoint("GET /healthz/ready")]
    [Endpoint("GET /healthz/metrics")]
    [Endpoint("GET /metrics")]
    public async Task HealthEndpoints_AllVariations_ReturnCorrectStatus()
    {
        using var client = _fixture.CreateClient();

        var endpoints = new[] { "/healthz/live", "/healthz/ready", "/healthz/metrics", "/metrics" };

        foreach (var endpoint in endpoints)
        {
            var (response, duration) = await PerformanceAssertions.MeasureAsync(
                () => client.GetAsync(endpoint));

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            duration.Should().BeLessThanOrEqualTo(PerformanceAssertions.Thresholds.MetadataQuery);

            var content = await response.Content.ReadAsStringAsync();
            content.Should().NotBeEmpty();

            _output.WriteLine($"Health endpoint {endpoint}: {response.StatusCode} ({duration.TotalMilliseconds:F1}ms)");
        }
    }

    /// <summary>
    /// Tests FeatureServer service and layer metadata endpoints.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    public async Task FeatureServerMetadata_AllLayers_ReturnValidJson()
    {
        using var client = _fixture.CreateClient(_schema);

        // Test service metadata
        var serviceResponse = await client.GetAsync("/rest/services/test/FeatureServer?f=json");
        serviceResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var serviceContent = await serviceResponse.Content.ReadAsStringAsync();
        serviceContent.Should().Contain("\"layers\"");
        serviceContent.Should().Contain("\"spatialReference\"");

        // Test layer metadata for layer 0
        var layerResponse = await client.GetAsync("/rest/services/test/FeatureServer/0?f=json");
        layerResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var layerContent = await layerResponse.Content.ReadAsStringAsync();
        layerContent.Should().Contain("\"name\"");
        layerContent.Should().Contain("\"geometryType\"");
        layerContent.Should().Contain("\"fields\"");

        // Test invalid layer ID
        var invalidResponse = await client.GetAsync("/rest/services/test/FeatureServer/999?f=json");
        invalidResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Tests OGC API Features landing page and conformance.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /ogc/features")]
    [Endpoint("GET /ogc/features/conformance")]
    public async Task OgcApiFeatures_LandingPageAndConformance_ReturnValidResponses()
    {
        using var client = _fixture.CreateClient(_schema);

        // Test landing page
        var landingResponse = await client.GetAsync("/ogc/features");
        landingResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var landingContent = await landingResponse.Content.ReadAsStringAsync();
        landingContent.Should().Contain("\"links\"");
        landingContent.Should().Contain("\"title\"");

        // Test conformance
        var conformanceResponse = await client.GetAsync("/ogc/features/conformance");
        conformanceResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var conformanceContent = await conformanceResponse.Content.ReadAsStringAsync();
        conformanceContent.Should().Contain("\"conformsTo\"");
        conformanceContent.Should().Contain("http://www.opengis.net/spec/ogcapi-features-1");
    }

    /// <summary>
    /// Tests all query endpoints with various parameter combinations.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task QueryEndpoints_VariousParameters_ReturnValidResults()
    {
        using var client = _fixture.CreateClient(_schema);

        var testCases = new[]
        {
            // FeatureServer queries
            ("/rest/services/test/FeatureServer/0/query?f=json&where=1=1", "FeatureServer basic query"),
            ("/rest/services/test/FeatureServer/0/query?f=geojson&where=1=1", "FeatureServer GeoJSON"),
            ("/rest/services/test/FeatureServer/0/query?f=json&where=1=1&resultRecordCount=5", "FeatureServer with limit"),
            ("/rest/services/test/FeatureServer/0/query?f=json&objectIds=1,2,3", "FeatureServer with object IDs"),

            // OGC API Features queries
            ("/ogc/features/collections/0/items", "OGC basic query"),
            ("/ogc/features/collections/0/items?limit=10", "OGC with limit"),
            ("/ogc/features/collections/0/items?f=json", "OGC JSON format"),
            ("/ogc/features/collections/0/items?bbox=0,0,1,1", "OGC with bbox"),
        };

        foreach (var (endpoint, description) in testCases)
        {
            var (response, duration) = await PerformanceAssertions.MeasureAsync(
                () => client.GetAsync(endpoint));

            response.StatusCode.Should().Be(HttpStatusCode.OK, $"{description} should succeed");
            duration.Should().BeLessThanOrEqualTo(PerformanceAssertions.Thresholds.SmallFeatureQuery);

            var content = await response.Content.ReadAsStringAsync();
            content.Should().NotBeEmpty($"{description} should return content");

            _output.WriteLine($"{description}: {response.StatusCode} ({duration.TotalMilliseconds:F1}ms)");
        }
    }

    /// <summary>
    /// Tests error handling across all endpoints with invalid parameters.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task ErrorHandling_InvalidParameters_ReturnAppropriateStatusCodes()
    {
        using var client = _fixture.CreateClient(_schema);

        var errorTestCases = new[]
        {
            // Invalid service/collection
            ("/rest/services/invalid/FeatureServer", HttpStatusCode.NotFound, "Invalid service ID"),
            ("/ogc/features/collections/invalid/items", HttpStatusCode.NotFound, "Invalid collection ID"),

            // Invalid layer ID
            ("/rest/services/test/FeatureServer/999", HttpStatusCode.NotFound, "Invalid layer ID"),
            ("/rest/services/test/FeatureServer/abc", HttpStatusCode.NotFound, "Non-numeric layer ID"),

            // Invalid query parameters
            ("/rest/services/test/FeatureServer/0/query?where=invalid sql syntax", HttpStatusCode.BadRequest, "Invalid SQL where clause"),
            ("/ogc/features/collections/0/items?bbox=invalid", HttpStatusCode.BadRequest, "Invalid bbox format"),
            ("/ogc/features/collections/0/items?limit=-1", HttpStatusCode.BadRequest, "Negative limit"),
            ("/ogc/features/collections/0/items?limit=999999", HttpStatusCode.BadRequest, "Limit too large"),
        };

        foreach (var (endpoint, expectedStatus, description) in errorTestCases)
        {
            var response = await client.GetAsync(endpoint);
            response.StatusCode.Should().Be(expectedStatus, description);

            _output.WriteLine($"{description}: {response.StatusCode} (expected {expectedStatus})");
        }
    }

    /// <summary>
    /// Tests content negotiation across all endpoints.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.ContentNegotiation)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task ContentNegotiation_VariousFormats_ReturnCorrectContentType()
    {
        using var client = _fixture.CreateClient(_schema);

        var contentTests = new[]
        {
            // FeatureServer formats
            ("/rest/services/test/FeatureServer/0/query?f=json&where=1=1", "application/json", "FeatureServer JSON"),
            ("/rest/services/test/FeatureServer/0/query?f=geojson&where=1=1", "application/geo+json", "FeatureServer GeoJSON"),

            // OGC API formats via parameter
            ("/ogc/features/collections/0/items?f=json", "application/json", "OGC JSON via parameter"),
            ("/ogc/features/collections/0/items", "application/geo+json", "OGC default GeoJSON"),
        };

        foreach (var (endpoint, expectedContentType, description) in contentTests)
        {
            var response = await client.GetAsync(endpoint);
            response.StatusCode.Should().Be(HttpStatusCode.OK, description);

            var actualContentType = response.Content.Headers.ContentType?.MediaType;
            actualContentType.Should().StartWith(expectedContentType, description);

            _output.WriteLine($"{description}: {actualContentType} (expected {expectedContentType})");
        }
    }

    /// <summary>
    /// Tests spatial query operations with various geometries.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.SpatialQuery)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task SpatialQueries_VariousGeometries_ReturnFilteredResults()
    {
        using var client = _fixture.CreateClient(_schema);

        var spatialTests = new[]
        {
            // FeatureServer spatial queries
            ("/rest/services/test/FeatureServer/0/query?f=json&geometry=-1,-1,1,1&geometryType=esriGeometryEnvelope", "FeatureServer bbox"),
            ("/rest/services/test/FeatureServer/0/query?f=json&geometry=0,0&geometryType=esriGeometryPoint&distance=1000", "FeatureServer distance"),

            // OGC spatial queries
            ("/ogc/features/collections/0/items?bbox=-1,-1,1,1", "OGC bbox query"),
        };

        foreach (var (endpoint, description) in spatialTests)
        {
            var (response, duration) = await PerformanceAssertions.MeasureAsync(
                () => client.GetAsync(endpoint));

            response.StatusCode.Should().Be(HttpStatusCode.OK, description);
            duration.Should().BeLessThanOrEqualTo(PerformanceAssertions.Thresholds.LargeSpatialQuery);

            var content = await response.Content.ReadAsStringAsync();
            content.Should().NotBeEmpty(description);

            _output.WriteLine($"{description}: {response.StatusCode} ({duration.TotalMilliseconds:F1}ms)");
        }
    }

    /// <summary>
    /// Tests security measures across all endpoints.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task SecurityMeasures_CommonAttacks_AreBlocked()
    {
        using var client = _fixture.CreateClient(_schema);

        // Test SQL injection resistance
        var sqlInjectionResult = await SecurityTestScenarios.TestCql2SqlInjection(
            client, "/rest/services/test/FeatureServer/0/query", "where");

        sqlInjectionResult.AllSafe.Should().BeTrue("SQL injection attempts should be blocked");
        _output.WriteLine($"SQL Injection Test: {sqlInjectionResult.SafetyScore:P1} safety score");

        // Test XSS resistance
        var xssResult = await SecurityTestScenarios.TestXssVulnerability(
            client, "/ogc/features/collections/0/items", "filter");

        xssResult.AllSafe.Should().BeTrue("XSS attempts should be sanitized");
        _output.WriteLine($"XSS Test: {xssResult.SafetyScore:P1} safety score");

    }

    /// <summary>
    /// Tests pagination across query endpoints.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task Pagination_VariousPageSizes_WorksCorrectly()
    {
        using var client = _fixture.CreateClient(_schema);

        var paginationTests = new[]
        {
            // FeatureServer pagination
            ("/rest/services/test/FeatureServer/0/query?f=json&where=1=1&resultRecordCount=5", "FeatureServer page 1"),
            ("/rest/services/test/FeatureServer/0/query?f=json&where=1=1&resultRecordCount=5&resultOffset=5", "FeatureServer page 2"),

            // OGC pagination
            ("/ogc/features/collections/0/items?limit=5", "OGC page 1"),
            ("/ogc/features/collections/0/items?limit=5&offset=5", "OGC page 2"),
        };

        foreach (var (endpoint, description) in paginationTests)
        {
            var response = await client.GetAsync(endpoint);
            response.StatusCode.Should().Be(HttpStatusCode.OK, description);

            var content = await response.Content.ReadAsStringAsync();
            content.Should().NotBeEmpty(description);

            _output.WriteLine($"{description}: {response.StatusCode}");
        }
    }

    /// <summary>
    /// Tests OpenAPI specification endpoint.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /openapi.json")]
    public async Task OpenApiSpec_Endpoint_ReturnsValidSpecification()
    {
        using var client = _fixture.CreateClient(_schema);

        var response = await client.GetAsync("/openapi.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("\"openapi\"");
        content.Should().Contain("\"paths\"");
        content.Should().Contain("\"components\"");

        _output.WriteLine($"OpenAPI spec length: {content.Length} characters");
    }

    /// <summary>
    /// Performance benchmark test across critical endpoints.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Performance)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task PerformanceBenchmark_CriticalEndpoints_MeetThresholds()
    {
        using var client = _fixture.CreateClient(_schema);

        var benchmarkEndpoints = new[]
        {
            ("/healthz/live", PerformanceAssertions.Thresholds.MetadataQuery, "Health check"),
            ("/rest/services/test/FeatureServer", PerformanceAssertions.Thresholds.MetadataQuery, "Service metadata"),
            ("/rest/services/test/FeatureServer/0/query?f=json&where=1=1&resultRecordCount=10", PerformanceAssertions.Thresholds.SmallFeatureQuery, "Small query"),
            ("/ogc/features/collections/0/items?limit=10", PerformanceAssertions.Thresholds.SmallFeatureQuery, "OGC small query"),
        };

        foreach (var (endpoint, threshold, description) in benchmarkEndpoints)
        {
            var result = await PerformanceAssertions.BenchmarkAsync(
                () => client.GetAsync(endpoint),
                iterations: 10,
                operationName: description);

            result.ShouldMeetCriteria(threshold, threshold * 2, minSuccessRate: 0.9);

            _output.WriteLine(result.ToString());
        }
    }
}

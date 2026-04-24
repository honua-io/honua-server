// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.OData.Models;
using Honua.TestKit;

namespace Honua.Server.Tests.Infrastructure;

[Collection("Database")]
public class ErrorHandlingConsistencyTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task AllProtocols_NotFoundErrors_ReturnConsistentStatusCodes()
    {
        // Arrange - Test various 404 scenarios across protocols
        var testCases = new[]
        {
            "/rest/services/non-existent/FeatureServer", // FeatureServer
            "/ogc/features/collections/non-existent",   // OGC API Features
            "/tiles/99999/1/0/0.mvt",                   // MVT
            "/odata/Features(99999)",                   // OData
        };

        foreach (var endpoint in testCases)
        {
            // Act
            var response = await _fixture.Client.GetAsync(endpoint);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.NotFound,
                $"endpoint {endpoint} should return 404 Not Found");
        }
    }

    [Fact]
    public async Task GeoServicesAndOgcProtocols_UseConsistentErrorFormat()
    {
        // Arrange
        var geoServicesEndpoint = "/rest/services/non-existent/FeatureServer";
        var ogcEndpoint = "/ogc/features/collections/non-existent";
        var mvtEndpoint = "/tiles/99999/1/0/0.mvt";

        // Act
        var geoResponse = await _fixture.Client.GetAsync(geoServicesEndpoint);
        var ogcResponse = await _fixture.Client.GetAsync(ogcEndpoint);
        var mvtResponse = await _fixture.Client.GetAsync(mvtEndpoint);

        // Assert - GeoServices/MVT use GeoServices format, OGC uses RFC 7807
        var geoContent = await geoResponse.Content.ReadAsStringAsync();
        var ogcContent = await ogcResponse.Content.ReadAsStringAsync();
        var mvtContent = await mvtResponse.Content.ReadAsStringAsync();

        var geoError = JsonSerializer.Deserialize<ApiErrorResponse>(geoContent);
        var mvtError = JsonSerializer.Deserialize<ApiErrorResponse>(mvtContent);
        var ogcProblem = JsonSerializer.Deserialize<JsonElement>(ogcContent);

        geoError.Should().NotBeNull();
        mvtError.Should().NotBeNull();

        geoError!.Error.Code.Should().Be(404);
        mvtError!.Error.Code.Should().Be(404);

        // GeoServices-style responses should contain the "error" property with "code", "message" structure
        geoContent.Should().Contain("\"error\":");
        geoContent.Should().Contain("\"code\":");
        geoContent.Should().Contain("\"message\":");

        mvtContent.Should().Contain("\"error\":");
        mvtContent.Should().Contain("\"code\":");
        mvtContent.Should().Contain("\"message\":");

        // OGC should use RFC 7807 Problem Details format
        ogcProblem.GetProperty("type").GetString().Should().NotBeNullOrEmpty();
        ogcProblem.GetProperty("title").GetString().Should().Be("Not Found");
        ogcProblem.GetProperty("status").GetInt32().Should().Be(404);
        ogcProblem.GetProperty("detail").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ODataProtocol_UsesODataSpecificErrorFormat()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/odata/Features(99999)");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.Should().ContainKey("OData-Version");
        response.Headers.GetValues("OData-Version").First().Should().Be("4.01");

        var content = await response.Content.ReadAsStringAsync();
        var error = JsonSerializer.Deserialize<ODataError>(content);

        error.Should().NotBeNull();
        error!.Error.Should().NotBeNull();
        error.Error.Code.Should().Be("ResourceNotFound");
        error.Error.Message.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AllProtocols_IncludeCorrelationIdHeaders()
    {
        // Arrange - Test correlation ID presence across protocols
        var testCases = new[]
        {
            "/rest/services/non-existent/FeatureServer", // FeatureServer
            "/ogc/features/collections/non-existent",   // OGC API Features
            "/tiles/99999/1/0/0.mvt",                   // MVT
            "/odata/Features(99999)",                   // OData
        };

        foreach (var endpoint in testCases)
        {
            // Act
            var response = await _fixture.Client.GetAsync(endpoint);

            // Assert
            response.Headers.Should().ContainKey("X-Correlation-ID",
                $"endpoint {endpoint} should include correlation ID header");

            var correlationId = response.Headers.GetValues("X-Correlation-ID").First();
            correlationId.Should().NotBeNullOrEmpty();
            correlationId.Should().MatchRegex(@"^[a-fA-F0-9\-]{36}$|^[a-zA-Z0-9\-]+$",
                "correlation ID should be a valid GUID or trace ID format");
        }
    }

    [Fact]
    public async Task AllProtocols_BadRequestErrors_UseConsistentStatusCodes()
    {
        // Arrange - Test 400 Bad Request scenarios
        var testCases = new[]
        {
            "/rest/services/test/FeatureServer/0/query?where=invalid'syntax", // FeatureServer
            "/ogc/features/collections/0/items?limit=invalid",             // OGC API Features
            "/tiles/0/-1/0/0.mvt",                                         // MVT (invalid zoom)
            "/odata/Features(0)?$filter=invalid syntax",                  // OData
        };

        foreach (var endpoint in testCases)
        {
            // Act
            var response = await _fixture.Client.GetAsync(endpoint);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                $"endpoint {endpoint} should return 400 Bad Request for invalid syntax");
        }
    }

    [Fact]
    public async Task AllProtocols_NoSensitiveInformationLeakage_InErrorResponses()
    {
        // Arrange - Endpoints that might trigger server errors
        var testCases = new[]
        {
            "/rest/services/non-existent/FeatureServer",
            "/ogc/features/collections/non-existent",
            "/tiles/99999/1/0/0.mvt",
            "/odata/Features(99999)",
        };

        foreach (var endpoint in testCases)
        {
            // Act
            var response = await _fixture.Client.GetAsync(endpoint);

            // Assert
            var content = await response.Content.ReadAsStringAsync();

            // Should not contain sensitive information
            var sensitivePatterns = new[]
            {
                "Exception",
                "StackTrace",
                "ConnectionString",
                "Password",
                "Server=",
                "Database=",
                "Host=",
                "Port=",
                "at System.",
                "at Microsoft.",
                "at Npgsql.",
            };

            foreach (var pattern in sensitivePatterns)
            {
                content.Should().NotContain(pattern,
                    $"endpoint {endpoint} should not expose sensitive information '{pattern}'");
            }
        }
    }

    [Fact]
    public async Task AllProtocols_ContentTypeHeaders_AreCorrectForErrorResponses()
    {
        // Arrange
        var testCases = new Dictionary<string, string>
        {
            ["/rest/services/non-existent/FeatureServer"] = "application/json",
            ["/ogc/features/collections/non-existent"] = "application/problem+json",
            ["/tiles/99999/1/0/0.mvt"] = "application/json",
            ["/odata/Features(99999)"] = "application/json", // OData errors are also JSON
        };

        foreach (var (endpoint, expectedContentType) in testCases)
        {
            // Act
            var response = await _fixture.Client.GetAsync(endpoint);

            // Assert
            response.Content.Headers.ContentType?.MediaType.Should().Be(expectedContentType,
                $"endpoint {endpoint} should return {expectedContentType} for error responses");
        }
    }

    [Fact]
    public async Task ErrorResponseFormats_DoNotContainRfc7807ProblemDetails_InGeoServicesAndMvt()
    {
        // Arrange - GeoServices and MVT should use domain-specific error formats, not RFC 7807
        var geospatialEndpoints = new[]
        {
            "/rest/services/non-existent/FeatureServer",
            "/tiles/99999/1/0/0.mvt",
        };

        foreach (var endpoint in geospatialEndpoints)
        {
            // Act
            var response = await _fixture.Client.GetAsync(endpoint);

            // Assert
            var content = await response.Content.ReadAsStringAsync();

            // Should NOT be RFC 7807 Problem Details format
            content.Should().NotContain("\"type\":",
                $"endpoint {endpoint} should not use RFC 7807 Problem Details format");
            content.Should().NotContain("\"title\":",
                $"endpoint {endpoint} should not use RFC 7807 Problem Details format");
            content.Should().NotContain("\"status\":",
                $"endpoint {endpoint} should not use RFC 7807 Problem Details format");
            content.Should().NotContain("\"detail\":",
                $"endpoint {endpoint} should not use RFC 7807 Problem Details format");

            // Should use domain-specific error format
            content.Should().Contain("\"error\":",
                $"endpoint {endpoint} should use standardized geospatial error format");
        }
    }
}

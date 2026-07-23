// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Infrastructure.Models;
using Honua.TestKit.Extensions;
using Honua.Protocols.OData.Models;
using Honua.TestKit;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Infrastructure;

[Collection("Database")]
public class ErrorHandlingConsistencyTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);

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
            // GeoServices REST (/rest/services) signals errors as HTTP 200 +
            // {"error":{"code":404}} (#2418). /tiles is NOT an Esri protocol surface
            // and returns a real HTTP 404 + problem+json (honua-server#2945). OGC/OData
            // also keep real HTTP status codes.
            if (IsGeoServicesRestEndpoint(endpoint))
            {
                await response.AssertGeoServicesErrorAsync(404);
            }
            else
            {
                response.StatusCode.Should().Be(HttpStatusCode.NotFound,
                    $"endpoint {endpoint} should return 404 Not Found");
            }
        }
    }

    [Fact]
    public async Task GeoServicesRestAndOgcProtocols_UseConsistentErrorFormat()
    {
        // Arrange
        var geoServicesEndpoint = "/rest/services/non-existent/FeatureServer";
        var ogcEndpoint = "/ogc/features/collections/non-existent";
        var mvtEndpoint = "/tiles/99999/1/0/0.mvt";

        // Act
        var geoResponse = await _fixture.Client.GetAsync(geoServicesEndpoint);
        var ogcResponse = await _fixture.Client.GetAsync(ogcEndpoint);
        var mvtResponse = await _fixture.Client.GetAsync(mvtEndpoint);

        // Assert - GeoServices REST keeps its 200-envelope format; OGC and the tiles
        // surface (honua-server#2945) both use real HTTP status + RFC 7807.
        var geoContent = await geoResponse.Content.ReadAsStringAsync();
        var ogcContent = await ogcResponse.Content.ReadAsStringAsync();
        var mvtContent = await mvtResponse.Content.ReadAsStringAsync();

        var geoError = JsonSerializer.Deserialize<ApiErrorResponse>(geoContent);
        var ogcProblem = JsonSerializer.Deserialize<JsonElement>(ogcContent);
        var mvtProblem = JsonSerializer.Deserialize<JsonElement>(mvtContent);

        geoError.Should().NotBeNull();
        geoError!.Error.Code.Should().Be(404);

        // GeoServices REST responses should contain the "error" property with "code", "message" structure
        geoContent.Should().Contain("\"error\":");
        geoContent.Should().Contain("\"code\":");
        geoContent.Should().Contain("\"message\":");

        // OGC should use RFC 7807 Problem Details format
        ogcProblem.GetProperty("type").GetString().Should().NotBeNullOrEmpty();
        ogcProblem.GetProperty("title").GetString().Should().Be("Not Found");
        ogcProblem.GetProperty("status").GetInt32().Should().Be(404);
        ogcProblem.GetProperty("detail").GetString().Should().NotBeNullOrEmpty();

        // Tiles surface (honua-server#2945): real 404 + RFC 7807, same shape as OGC.
        mvtResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        mvtProblem.GetProperty("title").GetString().Should().Be("Not Found");
        mvtProblem.GetProperty("status").GetInt32().Should().Be(404);
        mvtProblem.GetProperty("detail").GetString().Should().NotBeNullOrEmpty();
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
            // GeoServices surfaces (/rest/services, /tiles) now signal errors as
            // HTTP 200 + {"error":{"code":400}} (#2418, PA-070/PA-117); OGC API and
            // OData keep RFC 7807 HTTP status semantics.
            if (IsGeoServicesEndpoint(endpoint))
            {
                await response.AssertGeoServicesErrorAsync(400);
            }
            else
            {
                response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
                    $"endpoint {endpoint} should return 400 Bad Request for invalid syntax");
            }
        }
    }

    // Zoom/tile-coordinate validation on /tiles still short-circuits before layer
    // resolution (StandardErrorHelpers.CreateBadRequest directly) and is unaffected by
    // honua-server#2945, which only changed the layer-not-found path. So /tiles still
    // belongs in the 200-envelope classification for THIS (bad-request) scenario.
    private static bool IsGeoServicesEndpoint(string endpoint) =>
        endpoint.StartsWith("/rest/services", StringComparison.Ordinal) ||
        endpoint.StartsWith("/tiles", StringComparison.Ordinal);

    // /rest/services keeps the GeoServices 200-envelope for not-found; /tiles does not
    // (honua-server#2945 — the tiles surface returns a real HTTP 404 + problem+json).
    private static bool IsGeoServicesRestEndpoint(string endpoint) =>
        endpoint.StartsWith("/rest/services", StringComparison.Ordinal);

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
            ["/tiles/99999/1/0/0.mvt"] = "application/problem+json", // honua-server#2945
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
    public async Task ErrorResponseFormats_DoNotContainRfc7807ProblemDetails_InGeoServicesRest()
    {
        // Arrange - GeoServices REST should use its domain-specific error format, not RFC 7807
        var geospatialEndpoints = new[]
        {
            "/rest/services/non-existent/FeatureServer",
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

    [Fact]
    public async Task ErrorResponseFormat_UsesRfc7807ProblemDetails_OnTilesSurface()
    {
        // honua-server#2945: /tiles is not an Esri GeoServices protocol surface, so
        // (unlike /rest/services) its layer-not-found error DOES use RFC 7807
        // problem+json with a real HTTP status, the opposite of the assertion above.
        var response = await _fixture.Client.GetAsync("/tiles/99999/1/0/0.mvt");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var content = await response.Content.ReadAsStringAsync();

        content.Should().Contain("\"type\":");
        content.Should().Contain("\"title\":");
        content.Should().Contain("\"status\":");
        content.Should().Contain("\"detail\":");
        content.Should().NotContain("\"error\":",
            "the tiles surface must not use the GeoServices error envelope");
    }
}

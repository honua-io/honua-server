// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Protocols.OData.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Infrastructure.Errors;

/// <summary>
/// Tests for StandardErrorHelpers ensuring consistent error creation across all protocols.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Infrastructure)]
public sealed class StandardErrorHelpersTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Combine("tests", "seed", "server.yaml"));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    #region Standard Error Creation Tests

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features(0)")]
    public async Task CreateBadRequest_AllProtocols_ReturnsConsistentStatusCode()
    {
        // Test across different protocol paths
        var testCases = new[]
        {
            "/odata/Features(0)",
            "/ogc/features/collections/test/items",
            "/rest/services/0/FeatureServer/0/query",
            "/api/v1/admin/layers"
        };

        foreach (var path in testCases)
        {
            // Arrange
            var context = CreateContext(path);
            var additionalDetails = new[] { "Field 'id' is required", "Value must be a positive integer" };

            // Act
            var result = StandardErrorHelpers.CreateBadRequest(context, "Invalid request parameters", additionalDetails);
            await result.ExecuteAsync(context);

            // Assert
            context.Response.StatusCode.Should().Be(400, $"because path {path} should return 400 Bad Request");
        }
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features(999)")]
    public async Task CreateNotFound_AllProtocols_ReturnsConsistentStatusCode()
    {
        // Test across different protocol paths
        var testCases = new[]
        {
            "/odata/Features(999)",
            "/ogc/features/collections/nonexistent/items/123",
            "/rest/services/999/FeatureServer/0",
            "/api/v1/admin/layers/nonexistent"
        };

        foreach (var path in testCases)
        {
            // Arrange
            var context = CreateContext(path);

            // Act
            var result = StandardErrorHelpers.CreateNotFound(context, "Resource not found");
            await result.ExecuteAsync(context);

            // Assert
            context.Response.StatusCode.Should().Be(404, $"because path {path} should return 404 Not Found");
        }
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /api/v1/admin/layers")]
    public async Task CreateConflict_AllProtocols_ReturnsConsistentStatusCode()
    {
        // Test across different protocol paths
        var testCases = new[]
        {
            "/odata/Features",
            "/ogc/features/collections/test/items",
            "/rest/services/0/FeatureServer/0/addFeatures",
            "/api/v1/admin/layers"
        };

        foreach (var path in testCases)
        {
            // Arrange
            var context = CreateContext(path);

            // Act
            var result = StandardErrorHelpers.CreateConflict(context, "Resource already exists");
            await result.ExecuteAsync(context);

            // Assert
            context.Response.StatusCode.Should().Be(409, $"because path {path} should return 409 Conflict");
        }
    }

    #endregion

    #region Exception Handling Tests

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features(0)")]
    public async Task CreateFromException_ValidationException_ODataFormat_PreservesDetails()
    {
        // Arrange
        var context = CreateContext("/odata/Features(0)");
        var validationException = new ValidationException("Invalid field values", ["Name cannot be empty", "Age must be between 0 and 150"]);

        // Act
        var result = StandardErrorHelpers.CreateFromException(context, validationException);
        await result.ExecuteAsync(context);

        // Assert
        context.Response.StatusCode.Should().Be(400);

        var responseBody = GetResponseBody(context);
        var odataError = JsonSerializer.Deserialize<ODataError>(responseBody, _jsonOptions);

        odataError.Should().NotBeNull();
        odataError!.Error.Code.Should().Be("BadRequest");
        odataError.Error.Message.Should().Be("Invalid field values");

        // Check that validation details are preserved
        odataError.Error.Details.Should().NotBeNull();
        odataError.Error.Details!.Should().HaveCountGreaterOrEqualTo(5); // Main message + 2 validation details + correlation metadata
        odataError.Error.Details!.Select(detail => detail.Message)
            .Should().Contain("Invalid field values")
            .And.Contain("Name cannot be empty")
            .And.Contain("Age must be between 0 and 150");
        odataError.Error.Details!.Select(detail => detail.Code)
            .Should().Contain("CorrelationId")
            .And.Contain("Timestamp");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/0/FeatureServer/0/query")]
    public async Task CreateFromException_ServiceUnavailableException_IncludesRetryAfterHeader()
    {
        // Arrange
        var context = CreateContext("/rest/services/0/FeatureServer/0/query");
        var serviceException = new ServiceUnavailableException("Database maintenance in progress", retryAfterSeconds: 300);

        // Act
        var result = StandardErrorHelpers.CreateFromException(context, serviceException);
        await result.ExecuteAsync(context);

        // Assert
        context.Response.StatusCode.Should().Be(503);
        context.Response.Headers.Should().ContainKey("Retry-After");
        context.Response.Headers["Retry-After"].ToString().Should().Be("300");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /ogc/features/collections/test/items")]
    public async Task CreateFromException_GenericException_Development_IncludesDebugInfo()
    {
        // Arrange
        var context = CreateContext("/ogc/features/collections/test/items");
        var exception = new InvalidOperationException("Detailed internal error message with sensitive info");

        // Act - with debug info enabled (Development mode)
        var result = StandardErrorHelpers.CreateFromException(context, exception, includeDebugDetails: true);
        await result.ExecuteAsync(context);

        // Assert
        context.Response.StatusCode.Should().Be(500);

        var responseBody = GetResponseBody(context);
        var problemDetails = JsonSerializer.Deserialize<JsonElement>(responseBody);

        problemDetails.GetProperty("title").GetString().Should().Be("Internal Server Error");
        var detail = problemDetails.GetProperty("detail").GetString();
        detail.Should().Contain("An unexpected error occurred while processing the request."); // Sanitized message
        detail.Should().Contain("Debug:"); // Debug info included
        detail.Should().Contain("Detailed internal error message"); // Original message in debug section
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /ogc/features/collections/test/items")]
    public async Task CreateFromException_GenericException_Production_SanitizesMessage()
    {
        // Arrange
        var context = CreateContext("/ogc/features/collections/test/items");
        var exception = new InvalidOperationException("Detailed internal error message with sensitive info");

        // Act - without debug info (Production mode)
        var result = StandardErrorHelpers.CreateFromException(context, exception, includeDebugDetails: false);
        await result.ExecuteAsync(context);

        // Assert
        context.Response.StatusCode.Should().Be(500);

        var responseBody = GetResponseBody(context);
        var problemDetails = JsonSerializer.Deserialize<JsonElement>(responseBody);

        problemDetails.GetProperty("title").GetString().Should().Be("Internal Server Error");
        var detail = problemDetails.GetProperty("detail").GetString();
        detail.Should().Be("An unexpected error occurred while processing the request."); // Only sanitized message
        detail.Should().NotContain("sensitive info"); // No sensitive information leaked
        detail.Should().NotContain("Debug:"); // No debug info in production
    }

    #endregion

    #region Query Parameter Error Tests

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /ogc/features/collections/test/items")]
    public async Task CreateQueryParameterError_CqlFilter_SanitizesErrorMessage()
    {
        // Arrange
        var context = CreateContext("/ogc/features/collections/test/items");

        // Act
        var result = StandardErrorHelpers.CreateQueryParameterError(
            context,
            "filter",
            "CQL parsing error: Unexpected token 'System.Data.SqlClient.ConnectionException' at position 15");
        await result.ExecuteAsync(context);

        // Assert
        context.Response.StatusCode.Should().Be(400);

        var responseBody = GetResponseBody(context);
        var problemDetails = JsonSerializer.Deserialize<JsonElement>(responseBody);

        var detail = problemDetails.GetProperty("detail").GetString();
        detail.Should().Contain("Invalid CQL filter syntax");
        detail.Should().NotContain("System.Data.SqlClient"); // Sensitive system info removed
        detail.Should().NotContain("ConnectionException"); // Internal exception details removed
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /ogc/features/collections/test/items")]
    public async Task CreateCqlFilterError_SanitizesLongErrorMessage()
    {
        // Arrange
        var context = CreateContext("/ogc/features/collections/test/items");
        var longErrorMessage = new string('x', 250) + " with sensitive data like passwords and connection strings";

        // Act
        var result = StandardErrorHelpers.CreateCqlFilterError(context, longErrorMessage);
        await result.ExecuteAsync(context);

        // Assert
        context.Response.StatusCode.Should().Be(400);

        var responseBody = GetResponseBody(context);
        var problemDetails = JsonSerializer.Deserialize<JsonElement>(responseBody);

        var detail = problemDetails.GetProperty("detail").GetString();
        detail.Should().StartWith("Invalid CQL filter syntax:");
        detail.Length.Should().BeLessThan(250); // Message should be truncated
        detail.Should().EndWith("..."); // Should indicate truncation
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/0/FeatureServer/0/query")]
    public async Task CreateGeometryError_FormatsGeometryValidationError()
    {
        // Arrange
        var context = CreateContext("/rest/services/0/FeatureServer/0/addFeatures");

        // Act
        var result = StandardErrorHelpers.CreateGeometryError(context, "Polygon is self-intersecting");
        await result.ExecuteAsync(context);

        // Assert
        context.Response.StatusCode.Should().Be(400);

        var responseBody = GetResponseBody(context);
        var apiErrorResponse = JsonSerializer.Deserialize<JsonElement>(responseBody);

        apiErrorResponse.GetProperty("error").GetProperty("message").GetString().Should().Be("Bad Request");
        var details = apiErrorResponse.GetProperty("error").GetProperty("details").EnumerateArray().First();
        details.GetString().Should().Be("Invalid geometry: Polygon is self-intersecting");
    }

    #endregion

    #region Validation Error Tests

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features")]
    public async Task CreateValidationError_PreservesValidationDetails()
    {
        // Arrange
        var context = CreateContext("/odata/Features");
        var validationException = new ValidationException(
            "Multiple validation errors occurred",
            ["Email address is invalid", "Phone number format is incorrect", "Date must be in the future"]);

        // Act
        var result = StandardErrorHelpers.CreateValidationError(context, validationException);
        await result.ExecuteAsync(context);

        // Assert
        context.Response.StatusCode.Should().Be(400);

        var responseBody = GetResponseBody(context);
        var odataError = JsonSerializer.Deserialize<ODataError>(responseBody, _jsonOptions);

        odataError.Should().NotBeNull();
        odataError!.Error.Details.Should().HaveCountGreaterOrEqualTo(6); // Main message + 3 validation details + correlation metadata
        odataError.Error.Details!.Select(detail => detail.Message)
            .Should().Contain("Multiple validation errors occurred")
            .And.Contain("Email address is invalid")
            .And.Contain("Phone number format is incorrect")
            .And.Contain("Date must be in the future");
        odataError.Error.Details!.Select(detail => detail.Code)
            .Should().Contain("CorrelationId")
            .And.Contain("Timestamp");
    }

    #endregion

    #region Helper Methods

    private static DefaultHttpContext CreateContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("api.example.com");
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .BuildServiceProvider();

        // Initialize response body for testing
        context.Response.Body = new MemoryStream();
        context.Features.Set<IHttpResponseFeature>(new HttpResponseFeature());

        return context;
    }

    private static string GetResponseBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return reader.ReadToEnd();
    }

    #endregion
}

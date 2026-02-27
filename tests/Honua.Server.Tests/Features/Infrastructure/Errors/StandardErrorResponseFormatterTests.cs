// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.OData.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Infrastructure.Errors;

/// <summary>
/// Tests for standardized error response formatting across all protocols.
/// Ensures consistent error handling while maintaining protocol-specific formats.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Infrastructure)]
public sealed class StandardErrorResponseFormatterTests : IAsyncLifetime
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

    #region OData Error Formatting

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features(0)")]
    public async Task FormatError_ODataPath_ReturnsODataErrorFormat()
    {
        // Arrange
        var context = CreateContext("/odata/Features(0)");
        var errorResponse = StandardErrorResponse.BadRequest("Invalid filter syntax", ["Missing quotes around string literal"]);

        // Act
        var result = StandardErrorResponseFormatter.FormatError(context, errorResponse);
        await result.ExecuteAsync(context);

        // Assert
        context.Response.StatusCode.Should().Be(400);
        context.Response.Headers.Should().ContainKey("OData-Version");
        context.Response.Headers["OData-Version"].ToString().Should().Be("4.0");

        var responseBody = GetResponseBody(context);
        var odataError = JsonSerializer.Deserialize<ODataError>(responseBody, _jsonOptions);

        odataError.Should().NotBeNull();
        odataError!.Error.Code.Should().Be("BadRequest");
        odataError.Error.Message.Should().Be("Bad Request");
        odataError.Error.Details.Should().NotBeNull();
        odataError.Error.Details!.Should().HaveCountGreaterOrEqualTo(4);
        odataError.Error.Details!.Select(detail => detail.Message)
            .Should().Contain("Invalid filter syntax")
            .And.Contain("Missing quotes around string literal");
        odataError.Error.Details!.Select(detail => detail.Code)
            .Should().Contain("CorrelationId")
            .And.Contain("Timestamp");
    }

    #endregion

    #region OGC Features Error Formatting

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /ogc/features/collections/testLayer/items")]
    public async Task FormatError_OgcFeaturesPath_ReturnsProblemDetailsFormat()
    {
        // Arrange
        var context = CreateContext("/ogc/features/collections/testLayer/items");
        var errorResponse = StandardErrorResponse.NotFound("Collection 'testLayer' not found.");

        // Act
        var result = StandardErrorResponseFormatter.FormatError(context, errorResponse);
        await result.ExecuteAsync(context);

        // Assert
        context.Response.StatusCode.Should().Be(404);

        var responseBody = GetResponseBody(context);
        var problemDetails = JsonSerializer.Deserialize<JsonElement>(responseBody);

        problemDetails.GetProperty("type").GetString().Should().Be("about:blank");
        problemDetails.GetProperty("title").GetString().Should().Be("Not Found");
        problemDetails.GetProperty("status").GetInt32().Should().Be(404);
        problemDetails.GetProperty("detail").GetString().Should().Be("Collection 'testLayer' not found.");
        problemDetails.GetProperty("instance").GetString().Should().NotBeNullOrWhiteSpace();
    }

    #endregion

    #region GeoServices Error Formatting

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/0/FeatureServer/0/query")]
    public async Task FormatError_GeoServicesPath_ReturnsGeoServicesErrorFormat()
    {
        // Arrange
        var context = CreateContext("/rest/services/0/FeatureServer/0/query");
        var errorResponse = StandardErrorResponse.BadRequest("Invalid where clause", ["Syntax error near 'WHERE'"]);

        // Act
        var result = StandardErrorResponseFormatter.FormatError(context, errorResponse);
        await result.ExecuteAsync(context);

        // Assert
        context.Response.StatusCode.Should().Be(400);

        var responseBody = GetResponseBody(context);
        var apiErrorResponse = JsonSerializer.Deserialize<JsonElement>(responseBody);

        apiErrorResponse.GetProperty("error").GetProperty("code").GetInt32().Should().Be(400);
        apiErrorResponse.GetProperty("error").GetProperty("message").GetString().Should().Be("Bad Request");

        var details = apiErrorResponse.GetProperty("error").GetProperty("details").EnumerateArray().ToList();
        details.Should().HaveCountGreaterOrEqualTo(4);
        var detailStrings = details.Select(detail => detail.GetString() ?? string.Empty).ToList();
        detailStrings.Should().Contain("Invalid where clause");
        detailStrings.Should().Contain("Syntax error near 'WHERE'");
        detailStrings.Should().Contain(detail => detail.StartsWith("CorrelationId:", StringComparison.OrdinalIgnoreCase));
        detailStrings.Should().Contain(detail => detail.StartsWith("Timestamp:", StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Admin API Error Formatting

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /api/v1/admin/layers")]
    public async Task FormatError_AdminPath_ReturnsAdminProblemDetailsFormat()
    {
        // Arrange
        var context = CreateContext("/api/v1/admin/layers");
        var errorResponse = StandardErrorResponse.Conflict("Layer with that name already exists.");

        // Act
        var result = StandardErrorResponseFormatter.FormatError(context, errorResponse);
        await result.ExecuteAsync(context);

        // Assert
        context.Response.StatusCode.Should().Be(409);

        var responseBody = GetResponseBody(context);
        var problemDetails = JsonSerializer.Deserialize<JsonElement>(responseBody);

        problemDetails.GetProperty("type").GetString().Should().Be("https://honua.io/problems/admin");
        problemDetails.GetProperty("title").GetString().Should().Be("Conflict");
        problemDetails.GetProperty("status").GetInt32().Should().Be(409);
        problemDetails.GetProperty("detail").GetString().Should().Be("Layer with that name already exists.");
    }

    #endregion

    #region Error Sanitization Tests

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features(0)")]
    public async Task FormatError_WithDebugInfoDisabled_DoesNotIncludeDebugDetails()
    {
        // Arrange
        var context = CreateContext("/odata/Features(0)");
        var errorResponse = StandardErrorResponse.FromException(
            new InvalidOperationException("Internal system error: database connection failed"),
            includeDebugDetails: false);

        var options = new ErrorResponseFormatterOptions
        {
            IncludeDebugInfo = false
        };

        // Act
        var result = StandardErrorResponseFormatter.FormatError(context, errorResponse, options);
        await result.ExecuteAsync(context);

        // Assert
        var responseBody = GetResponseBody(context);
        responseBody.Should().NotContain("database connection failed");
        responseBody.Should().NotContain("Internal system error");

        var odataError = JsonSerializer.Deserialize<ODataError>(responseBody, _jsonOptions);
        odataError!.Error.Message.Should().Be("Internal Server Error"); // Sanitized message
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features(0)")]
    public async Task FormatError_WithDebugInfoEnabled_IncludesDebugDetails()
    {
        // Arrange
        var context = CreateContext("/odata/Features(0)");
        var errorResponse = new StandardErrorResponse(
            StatusCode: 500,
            Title: "Internal Server Error",
            Detail: "An unexpected error occurred while processing the request.",
            DebugInfo: "NullReferenceException: Object reference not set to an instance of an object."
        );

        var options = new ErrorResponseFormatterOptions
        {
            IncludeDebugInfo = true
        };

        // Act
        var result = StandardErrorResponseFormatter.FormatError(context, errorResponse, options);
        await result.ExecuteAsync(context);

        // Assert
        var responseBody = GetResponseBody(context);
        var odataError = JsonSerializer.Deserialize<ODataError>(responseBody, _jsonOptions);

        odataError!.Error.Details.Should().NotBeNull();
        odataError.Error.Details!.Should().Contain(d => d.Message.Contains("NullReferenceException"));
    }

    #endregion

    #region Service Unavailable with Retry-After

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/0/FeatureServer/0/query")]
    public async Task FormatError_ServiceUnavailableWithRetryAfter_SetsRetryAfterHeader()
    {
        // Arrange
        var context = CreateContext("/rest/services/0/FeatureServer/0/query");
        var errorResponse = StandardErrorResponse.ServiceUnavailable("Database maintenance in progress", 300);

        var options = new ErrorResponseFormatterOptions
        {
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Retry-After"] = "300"
            }
        };

        // Act
        var result = StandardErrorResponseFormatter.FormatError(context, errorResponse, options);
        await result.ExecuteAsync(context);

        // Assert
        context.Response.StatusCode.Should().Be(503);
        context.Response.Headers.Should().ContainKey("Retry-After");
        context.Response.Headers["Retry-After"].ToString().Should().Be("300");

        var responseBody = GetResponseBody(context);
        var apiErrorResponse = JsonSerializer.Deserialize<JsonElement>(responseBody);
        apiErrorResponse.GetProperty("error").GetProperty("message").GetString().Should().Be("Service Unavailable");
    }

    #endregion

    #region Exception Handling Tests

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /ogc/features/collections/testLayer/items")]
    public async Task FormatError_ValidationException_PreservesValidationDetails()
    {
        // Arrange
        var context = CreateContext("/ogc/features/collections/testLayer/items");
        var validationException = new ValidationException("Invalid geometry coordinates", ["Point coordinates must be within valid range", "Longitude must be between -180 and 180"]);
        var errorResponse = StandardErrorResponse.FromException(validationException);

        // Act
        var result = StandardErrorResponseFormatter.FormatError(context, errorResponse);
        await result.ExecuteAsync(context);

        // Assert
        context.Response.StatusCode.Should().Be(400);

        var responseBody = GetResponseBody(context);
        var problemDetails = JsonSerializer.Deserialize<JsonElement>(responseBody);

        problemDetails.GetProperty("title").GetString().Should().Be("Bad Request");
        var detail = problemDetails.GetProperty("detail").GetString();
        detail.Should().Contain("Invalid geometry coordinates");
        detail.Should().Contain("Point coordinates must be within valid range");
        detail.Should().Contain("Longitude must be between -180 and 180");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features(999)")]
    public async Task FormatError_ResourceNotFoundException_ReturnsSanitizedNotFound()
    {
        // Arrange
        var context = CreateContext("/odata/Features(999)");
        var notFoundException = new ResourceNotFoundException();
        var errorResponse = StandardErrorResponse.FromException(notFoundException);

        // Act
        var result = StandardErrorResponseFormatter.FormatError(context, errorResponse);
        await result.ExecuteAsync(context);

        // Assert
        context.Response.StatusCode.Should().Be(404);

        var responseBody = GetResponseBody(context);
        var odataError = JsonSerializer.Deserialize<ODataError>(responseBody, _jsonOptions);

        odataError!.Error.Code.Should().Be("ResourceNotFound");
        odataError.Error.Message.Should().Be("Not Found");
        odataError.Error.Details![0].Message.Should().Be("The requested resource was not found.");
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

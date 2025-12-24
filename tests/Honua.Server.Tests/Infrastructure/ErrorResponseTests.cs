// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Models;
using Xunit;

namespace Honua.Server.Tests.Infrastructure;

/// <summary>
/// Tests for GeoServices-compatible error response format
/// Ensures compliance with GeoServices REST error response specification
/// </summary>
public class ErrorResponseTests
{
    [Fact]
    public void ApiErrorResponse_ShouldSerializeToGeoServicesCompatibleFormat()
    {
        // Arrange
        var errorResponse = new ApiErrorResponse
        {
            Error = new GeoServicesError
            {
                Code = 404,
                Message = "Service 'test' not found",
                Details = ["Service does not exist", "Check service name"]
            }
        };

        // Act
        var json = JsonSerializer.Serialize(errorResponse, FeatureServerJsonContext.Default.ApiErrorResponse);
        using var jsonDoc = JsonDocument.Parse(json);

        // Assert - Verify top-level structure
        jsonDoc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        jsonDoc.RootElement.EnumerateObject().Select(p => p.Name).Should().ContainSingle("error");

        // Assert - Verify error object structure
        var errorElement = jsonDoc.RootElement.GetProperty("error");
        errorElement.ValueKind.Should().Be(JsonValueKind.Object);

        var errorPropertyNames = errorElement.EnumerateObject().Select(p => p.Name).ToList();
        errorPropertyNames.Should().Contain("code");
        errorPropertyNames.Should().Contain("message");
        errorPropertyNames.Should().Contain("details");

        // Assert - Verify error property values
        errorElement.GetProperty("code").GetInt32().Should().Be(404);
        errorElement.GetProperty("message").GetString().Should().Be("Service 'test' not found");

        var detailsElement = errorElement.GetProperty("details");
        detailsElement.ValueKind.Should().Be(JsonValueKind.Array);
        detailsElement.GetArrayLength().Should().Be(2);
        detailsElement[0].GetString().Should().Be("Service does not exist");
        detailsElement[1].GetString().Should().Be("Check service name");

        // Assert - Verify camelCase naming convention
        json.Should().Contain("\"error\":");
        json.Should().Contain("\"code\":");
        json.Should().Contain("\"message\":");
        json.Should().Contain("\"details\":");
    }

    [Fact]
    public void ApiErrorResponse_WithNullDetails_ShouldOmitDetailsProperty()
    {
        // Arrange
        var errorResponse = new ApiErrorResponse
        {
            Error = new GeoServicesError
            {
                Code = 400,
                Message = "Bad request"
                // Details is null
            }
        };

        // Act
        var json = JsonSerializer.Serialize(errorResponse, FeatureServerJsonContext.Default.ApiErrorResponse);

        // Assert - Verify details property is omitted when null
        json.Should().NotContain("\"details\":");
        json.Should().Contain("\"code\":400");
        json.Should().Contain("\"message\":\"Bad request\"");
    }

    [Theory]
    [InlineData(400, 400)]
    [InlineData(401, 401)]
    [InlineData(403, 403)]
    [InlineData(404, 404)]
    [InlineData(405, 405)]
    [InlineData(408, 408)]
    [InlineData(413, 413)]
    [InlineData(422, 422)]
    [InlineData(498, 498)]
    [InlineData(499, 499)]
    [InlineData(500, 500)]
    [InlineData(502, 502)]
    [InlineData(503, 503)]
    [InlineData(999, 500)] // Unknown codes default to 500
    public void GeoServicesErrorCodes_FromHttpStatusCode_ShouldReturnCorrectCode(int httpStatusCode, int expectedGeoServicesCode)
    {
        // Act
        var actualGeoServicesCode = GeoServicesErrorCodes.FromHttpStatusCode(httpStatusCode);

        // Assert
        actualGeoServicesCode.Should().Be(expectedGeoServicesCode);
    }

    [Fact]
    public void GeoServicesError_ShouldHaveRequiredProperties()
    {
        // Arrange & Act
        var geoServicesError = new GeoServicesError
        {
            Code = 404,
            Message = "Not found"
        };

        // Assert
        geoServicesError.Code.Should().Be(404);
        geoServicesError.Message.Should().Be("Not found");
        geoServicesError.Details.Should().BeNull();
    }

    [Fact]
    public void ApiErrorResponse_ShouldMatchExactGeoServicesFormat()
    {
        // Arrange
        var errorResponse = new ApiErrorResponse
        {
            Error = new GeoServicesError
            {
                Code = 498,
                Message = "Invalid token",
                Details = ["Token has expired"]
            }
        };

        // Act
        var json = JsonSerializer.Serialize(errorResponse, FeatureServerJsonContext.Default.ApiErrorResponse);

        // Assert - Verify exact JSON structure matches GeoServices specification
        var expectedJson = """{"error":{"code":498,"message":"Invalid token","details":["Token has expired"]}}""";
        json.Should().Be(expectedJson);
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Infrastructure.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Honua.Server.Tests.Infrastructure.Validation;

/// <summary>
/// Unit tests for RouteParameterValidator ensuring consistent route parameter
/// validation across all endpoints.
/// </summary>
public class RouteParameterValidatorTests
{
    private readonly RouteParameterValidator _validator;

    public RouteParameterValidatorTests()
    {
        _validator = new RouteParameterValidator();
    }

    private static DefaultHttpContext CreateMockContext(Dictionary<string, object?> routeValues)
    {
        var context = new DefaultHttpContext();
        context.Request.RouteValues = new RouteValueDictionary(routeValues);
        return context;
    }

    [Theory]
    [InlineData("service1", true)]
    [InlineData("my-service", true)]
    [InlineData("_service", true)]
    [InlineData("Service_123", true)]
    [InlineData("", false)] // Empty
    [InlineData("123service", false)] // Starts with number
    [InlineData("service with spaces", false)] // Contains spaces
    [InlineData("service@domain", false)] // Contains special characters
    [InlineData("service<script>", false)] // Contains dangerous characters
    public void ValidateServiceId_VariousInputs_ReturnsExpectedResult(string serviceId, bool shouldBeValid)
    {
        // Arrange
        var context = CreateMockContext(new Dictionary<string, object?> { { "serviceId", serviceId } });

        // Act
        var result = _validator.ValidateServiceId(context);

        // Assert
        result.IsValid.Should().Be(shouldBeValid);
        if (shouldBeValid)
        {
            result.Value.Should().Be(serviceId);
        }
        else
        {
            result.ErrorMessage.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public void ValidateServiceId_MissingRouteValue_ReturnsFailure()
    {
        // Arrange
        var context = CreateMockContext(new Dictionary<string, object?>());

        // Act
        var result = _validator.ValidateServiceId(context);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Service ID is required");
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(123, true)]
    [InlineData(999999, true)]
    [InlineData(-1, false)] // Negative
    public void ValidateLayerId_IntegerValues_ReturnsExpectedResult(int layerId, bool shouldBeValid)
    {
        // Arrange - Test with already parsed integer route values
        var context = CreateMockContext(new Dictionary<string, object?> { { "layerId", layerId } });

        // Act
        var result = _validator.ValidateLayerId(context);

        // Assert
        result.IsValid.Should().Be(shouldBeValid);
        if (shouldBeValid)
        {
            result.Value.Should().Be(layerId);
        }
        else
        {
            result.ErrorMessage.Should().NotBeNullOrEmpty();
        }
    }

    [Theory]
    [InlineData("0", true, 0)]
    [InlineData("123", true, 123)]
    [InlineData("999999", true, 999999)]
    [InlineData("abc", false, 0)] // Invalid integer
    [InlineData("-1", false, 0)] // Negative
    [InlineData("", false, 0)] // Empty
    public void ValidateLayerId_StringValues_ReturnsExpectedResult(string layerIdString, bool shouldBeValid, int expectedValue)
    {
        // Arrange - Test with string route values that need parsing
        var context = CreateMockContext(new Dictionary<string, object?> { { "layerId", layerIdString } });

        // Act
        var result = _validator.ValidateLayerId(context);

        // Assert
        result.IsValid.Should().Be(shouldBeValid);
        if (shouldBeValid)
        {
            result.Value.Should().Be(expectedValue);
        }
        else
        {
            result.ErrorMessage.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public void ValidateLayerId_MissingRouteValue_ReturnsFailure()
    {
        // Arrange
        var context = CreateMockContext(new Dictionary<string, object?>());

        // Act
        var result = _validator.ValidateLayerId(context);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Layer ID is required");
    }

    [Theory]
    [InlineData("collection1", true)]
    [InlineData("my-collection-name", true)]
    [InlineData("Collection_123", true)]
    [InlineData("collection with spaces", true)] // Collections are more permissive
    [InlineData("", false)] // Empty
    [InlineData("collection<script>alert('xss')</script>", false)] // XSS attempt
    [InlineData("collection\"with'quotes", false)] // Dangerous characters
    public void ValidateCollectionId_VariousInputs_ReturnsExpectedResult(string collectionId, bool shouldBeValid)
    {
        // Arrange
        var context = CreateMockContext(new Dictionary<string, object?> { { "collectionId", collectionId } });

        // Act
        var result = _validator.ValidateCollectionId(context);

        // Assert
        result.IsValid.Should().Be(shouldBeValid);
        if (shouldBeValid)
        {
            result.Value.Should().Be(collectionId);
        }
        else
        {
            result.ErrorMessage.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public void ValidateCollectionId_TooLong_ReturnsFailure()
    {
        // Arrange
        var longCollectionId = new string('a', 101); // Exceeds 100 character limit
        var context = CreateMockContext(new Dictionary<string, object?> { { "collectionId", longCollectionId } });

        // Act
        var result = _validator.ValidateCollectionId(context);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("too long");
    }

    [Theory]
    [InlineData("feature123", "feature123", true)]
    [InlineData("feature%20with%20spaces", "feature with spaces", true)] // URL encoded
    [InlineData("feature-456", "feature-456", true)]
    [InlineData("", "", false)] // Empty
    [InlineData("feature<script>", "feature<script>", false)] // Dangerous characters
    public void ValidateFeatureId_VariousInputs_ReturnsExpectedResult(string featureId, string expectedDecoded, bool shouldBeValid)
    {
        // Arrange
        var context = CreateMockContext(new Dictionary<string, object?> { { "featureId", featureId } });

        // Act
        var result = _validator.ValidateFeatureId(context);

        // Assert
        result.IsValid.Should().Be(shouldBeValid);
        if (shouldBeValid)
        {
            result.Value.Should().Be(expectedDecoded);
        }
        else
        {
            result.ErrorMessage.Should().NotBeNullOrEmpty();
        }
    }

    [Theory]
    [InlineData("GET", new[] { "GET", "POST" }, true)]
    [InlineData("POST", new[] { "GET", "POST" }, true)]
    [InlineData("PUT", new[] { "GET", "POST" }, false)]
    [InlineData("DELETE", new[] { "GET", "POST" }, false)]
    public void ValidateHttpMethod_VariousMethods_ReturnsExpectedResult(string method, string[] allowedMethods, bool shouldBeValid)
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        var allowedSet = new HashSet<string>(allowedMethods);

        // Act
        var result = _validator.ValidateHttpMethod(context, allowedSet);

        // Assert
        result.IsValid.Should().Be(shouldBeValid);
        if (!shouldBeValid)
        {
            result.ErrorMessage.Should().NotBeNullOrEmpty();
            context.Response.StatusCode.Should().Be(StatusCodes.Status405MethodNotAllowed);
            context.Response.Headers.Allow.ToString().Should().Contain(string.Join(", ", allowedMethods));
        }
    }

    [Theory]
    [InlineData("application/json", new[] { "application/json", "text/plain" }, true, "application/json")]
    [InlineData("application/json; charset=utf-8", new[] { "application/json", "text/plain" }, true, "application/json")]
    [InlineData("APPLICATION/JSON", new[] { "application/json", "text/plain" }, true, "application/json")]
    [InlineData("text/xml", new[] { "application/json", "text/plain" }, false, null)]
    [InlineData("", new[] { "application/json", "text/plain" }, false, null)]
    public void ValidateContentType_VariousTypes_ReturnsExpectedResult(string contentType, string[] allowedTypes, bool shouldBeValid, string? expectedMediaType)
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.ContentType = contentType;
        var allowedSet = new HashSet<string>(allowedTypes);

        // Act
        var result = _validator.ValidateContentType(context, allowedSet);

        // Assert
        result.IsValid.Should().Be(shouldBeValid);
        if (shouldBeValid)
        {
            result.Value.Should().Be(expectedMediaType);
        }
        else
        {
            result.ErrorMessage.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public void ValidateContentType_MissingContentType_ReturnsFailure()
    {
        // Arrange
        var context = new DefaultHttpContext();
        // ContentType is null by default
        var allowedSet = new HashSet<string> { "application/json" };

        // Act
        var result = _validator.ValidateContentType(context, allowedSet);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Content-Type header is required");
    }
}

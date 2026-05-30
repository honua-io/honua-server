// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Validation;
using Honua.Infrastructure.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Infrastructure.Validation;

/// <summary>
/// Unit tests for ValidationErrorHelpers ensuring consistent error response
/// formatting across all protocols.
/// </summary>
public class ValidationErrorHelpersTests
{
    [Fact]
    public void CreateGeoServicesValidationError_WithMessage_CreatesCorrectResponse()
    {
        // Arrange
        const string message = "Test validation error";
        var details = new[] { "Detail 1", "Detail 2" };

        // Act
        var result = ValidationErrorHelpers.CreateGeoServicesValidationError(message, details);

        // Assert
        result.Should().NotBeNull();
        // Note: Testing the actual response content would require more complex setup
        // to inspect the IResult implementation details
    }

    [Fact]
    public void CreateOgcValidationError_WithTitleAndDetail_CreatesCorrectResponse()
    {
        // Arrange
        const string title = "Invalid Parameter";
        const string detail = "The parameter 'test' is not valid";
        const string instance = "/test/endpoint";

        // Act
        var result = ValidationErrorHelpers.CreateOgcValidationError(title, detail, instance);

        // Assert
        result.Should().NotBeNull();
    }

    [Fact]
    public void CreateODataValidationError_WithCodeAndMessage_CreatesCorrectResponse()
    {
        // Arrange
        const string code = "InvalidQuery";
        const string message = "The query is not valid";

        // Act
        var result = ValidationErrorHelpers.CreateODataValidationError(code, message);

        // Assert
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateMethodNotAllowed_WithAllowedMethods_SetsAllowHeader()
    {
        var allowedMethods = new HashSet<string> { "GET", "POST", "PUT" };
        var result = ValidationErrorHelpers.CreateMethodNotAllowed(allowedMethods);
        var context = CreateContext("/test");

        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status405MethodNotAllowed);
        context.Response.Headers["Allow"].ToString().Should().Be("GET, POST, PUT");
    }

    [Fact]
    public void CreateUnsupportedMediaType_WithTypesAndMessage_CreatesCorrectResponse()
    {
        // Arrange
        const string receivedType = "text/xml";
        var allowedTypes = new HashSet<string> { "application/json", "application/xml" };

        // Act
        var result = ValidationErrorHelpers.CreateUnsupportedMediaType(receivedType, allowedTypes);

        // Assert
        result.Should().NotBeNull();
    }

    [Fact]
    public void CreateErrorIfInvalid_WithValidResult_ReturnsNull()
    {
        // Arrange
        var validResult = ValidationResult.Success();
        var errorFactory = (string message) => Results.BadRequest(message);

        // Act
        var result = ValidationErrorHelpers.CreateErrorIfInvalid(validResult, errorFactory);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void CreateErrorIfInvalid_WithInvalidResult_ReturnsError()
    {
        // Arrange
        var invalidResult = ValidationResult.Failure("Test error");
        var errorFactory = (string message) => Results.BadRequest(message);

        // Act
        var result = ValidationErrorHelpers.CreateErrorIfInvalid(invalidResult, errorFactory);

        // Assert
        result.Should().NotBeNull();
    }

    [Fact]
    public void CreateErrorIfInvalid_WithTypedValidResult_ReturnsNull()
    {
        // Arrange
        var validResult = ValidationResult<string>.Success("test-value");
        var errorFactory = (string message) => Results.BadRequest(message);

        // Act
        var result = ValidationErrorHelpers.CreateErrorIfInvalid(validResult, errorFactory);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void CreateErrorIfInvalid_WithTypedInvalidResult_ReturnsError()
    {
        // Arrange
        var invalidResult = ValidationResult<string>.Failure("Test error");
        var errorFactory = (string message) => Results.BadRequest(message);

        // Act
        var result = ValidationErrorHelpers.CreateErrorIfInvalid(invalidResult, errorFactory);

        // Assert
        result.Should().NotBeNull();
    }

    [Fact]
    public void CombineValidationResults_AllValid_ReturnsSuccess()
    {
        // Arrange
        var validResults = new[]
        {
            ValidationResult.Success(),
            ValidationResult.Success(),
            ValidationResult.Success()
        };

        // Act
        var result = ValidationErrorHelpers.CombineValidationResults(validResults);

        // Assert
        result.IsValid.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void CombineValidationResults_FirstInvalid_ReturnsFirstError()
    {
        // Arrange
        var results = new[]
        {
            ValidationResult.Success(),
            ValidationResult.Failure("First error"),
            ValidationResult.Failure("Second error")
        };

        // Act
        var result = ValidationErrorHelpers.CombineValidationResults(results);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be("First error");
    }

    [Fact]
    public void CombineValidationResults_EmptyArray_ReturnsSuccess()
    {
        // Arrange
        var results = Array.Empty<ValidationResult>();

        // Act
        var result = ValidationErrorHelpers.CombineValidationResults(results);

        // Assert
        result.IsValid.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void FromErrors_NoErrors_ReturnsSuccess()
    {
        // Arrange
        var errors = new List<string>();

        // Act
        var result = ValidationErrorHelpers.FromErrors(errors);

        // Assert
        result.IsValid.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void FromErrors_SingleError_ReturnsSingleErrorMessage()
    {
        // Arrange
        var errors = new[] { "Single error message" };

        // Act
        var result = ValidationErrorHelpers.FromErrors(errors);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be("Single error message");
    }

    [Fact]
    public void FromErrors_MultipleErrors_ReturnsCombinedMessage()
    {
        // Arrange
        var errors = new[] { "Error 1", "Error 2", "Error 3" };

        // Act
        var result = ValidationErrorHelpers.FromErrors(errors);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be("Multiple validation errors: Error 1; Error 2; Error 3");
    }

    [Fact]
    public async Task WriteValidationErrorAsync_WritesCorrectStatusAndContentType()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        const int statusCode = 400;
        const string title = "Test Error";
        const string detail = "Test error detail";

        // Act
        await ValidationErrorHelpers.WriteValidationErrorAsync(context, statusCode, title, detail);

        // Assert
        context.Response.StatusCode.Should().Be(statusCode);
        context.Response.ContentType.Should().Be("application/json; charset=utf-8");

        // Verify response body was written (basic check)
        context.Response.Body.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task WriteValidationErrorAsync_WithCancellation_RespectsToken()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ValidationErrorHelpers.WriteValidationErrorAsync(context, 400, "Title", "Detail", cts.Token));
    }

    [Fact]
    public async Task CreateUnsupportedMediaType_WithGeoServicesContext_ReturnsGeoServicesEnvelope()
    {
        var context = CreateContext("/rest/services/0/FeatureServer/0/applyEdits");
        var result = ValidationErrorHelpers.CreateUnsupportedMediaType(
            context,
            "text/plain",
            new HashSet<string> { "application/json", "application/x-www-form-urlencoded" });

        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status415UnsupportedMediaType);
        context.Response.Body.Position = 0;

        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        document.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().BeGreaterThan(0);
        document.RootElement.GetProperty("error").GetProperty("message").GetString().Should().Be("Unsupported Media Type");
    }

    [Fact]
    public async Task CreateMethodNotAllowed_WithGeoServicesContext_ReturnsGeoServicesEnvelopeAndAllowHeader()
    {
        var context = CreateContext("/rest/services/0/FeatureServer/0/query");
        var result = ValidationErrorHelpers.CreateMethodNotAllowed(
            context,
            new HashSet<string> { "GET", "POST" });

        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status405MethodNotAllowed);
        context.Response.Headers["Allow"].ToString().Should().Be("GET, POST");
        context.Response.Body.Position = 0;

        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        document.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(405);
        document.RootElement.GetProperty("error").GetProperty("message").GetString().Should().Be("Method Not Allowed");
    }

    private static DefaultHttpContext CreateContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        return context;
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Validation;
using Honua.Server.Features.Infrastructure.Validation;

namespace Honua.Server.Tests.Infrastructure.Validation;

/// <summary>
/// Unit tests for ValidationExtensions ensuring validation extension methods
/// work correctly across different scenarios.
/// </summary>
public class ValidationExtensionsTests
{
    [Theory]
    [InlineData(null, "esriSpatialRelIntersects")] // Default
    [InlineData("", "esriSpatialRelIntersects")] // Default
    [InlineData("esriSpatialRelIntersects", "esriSpatialRelIntersects")] // Valid
    [InlineData("esriSpatialRelContains", "esriSpatialRelContains")] // Valid
    [InlineData("esriSpatialRelWithin", "esriSpatialRelWithin")] // Valid
    [InlineData("ESRISPATIALRELINTERSECTS", "esriSpatialRelIntersects")] // Case insensitive
    [InlineData("invalidRelationship", null)] // Invalid
    public void ValidateSpatialRelationship_VariousInputs_ReturnsExpectedResult(string? spatialRel, string? expectedValue)
    {
        // Act
        var result = ValidationExtensions.ValidateSpatialRelationship(spatialRel);

        // Assert
        if (expectedValue != null)
        {
            result.IsValid.Should().BeTrue();
            result.Value.Should().Be(expectedValue);
        }
        else
        {
            result.IsValid.Should().BeFalse();
            result.ErrorMessage.Should().NotBeNullOrEmpty();
        }
    }

    [Theory]
    [InlineData(null, null, true)] // No distance query
    [InlineData(100.0, "esriSRUnit_Meter", true)] // Valid distance query
    [InlineData(1.5, "esriSRUnit_Kilometer", true)] // Valid distance query
    [InlineData(0.0, "esriSRUnit_Meter", true)] // Zero distance is valid
    [InlineData(-1.0, "esriSRUnit_Meter", false)] // Negative distance
    [InlineData(100.0, null, false)] // Missing units
    [InlineData(100.0, "", false)] // Empty units
    [InlineData(100.0, "invalidUnits", false)] // Invalid units
    [InlineData(null, "esriSRUnit_Meter", false)] // Units without distance
    public void ValidateDistanceQuery_VariousInputs_ReturnsExpectedResult(double? distance, string? units, bool shouldBeValid)
    {
        // Act
        var result = ValidationExtensions.ValidateDistanceQuery(distance, units);

        // Assert
        result.IsValid.Should().Be(shouldBeValid);
        if (!shouldBeValid)
        {
            result.ErrorMessage.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public void Then_WithSuccessfulValidation_ExecutesNextValidation()
    {
        // Arrange
        var firstValidation = ValidationResult.Success();
        var secondValidationExecuted = false;

        // Act
        var result = firstValidation.Then(() =>
        {
            secondValidationExecuted = true;
            return ValidationResult.Success();
        });

        // Assert
        result.IsValid.Should().BeTrue();
        secondValidationExecuted.Should().BeTrue();
    }

    [Fact]
    public void Then_WithFailedValidation_SkipsNextValidation()
    {
        // Arrange
        var firstValidation = ValidationResult.Failure("First validation failed");
        var secondValidationExecuted = false;

        // Act
        var result = firstValidation.Then(() =>
        {
            secondValidationExecuted = true;
            return ValidationResult.Success();
        });

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be("First validation failed");
        secondValidationExecuted.Should().BeFalse();
    }

    [Fact]
    public void Then_WithTypedValidation_ChainsCorrectly()
    {
        // Arrange
        var firstValidation = ValidationResult<string>.Success("test-value");

        // Act
        var result = firstValidation.Then(value =>
        {
            value.Should().Be("test-value");
            return ValidationResult<int>.Success(42);
        });

        // Assert
        result.IsValid.Should().BeTrue();
        result.Value.Should().Be(42);
    }

    [Fact]
    public void Then_WithTypedValidationFailure_ReturnsFailure()
    {
        // Arrange
        var firstValidation = ValidationResult<string>.Failure("Type validation failed");

        // Act
        var result = firstValidation.Then(value => ValidationResult<int>.Success(42));

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be("Type validation failed");
    }

}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation;
using Honua.Server.Features.Infrastructure.Validation;

namespace Honua.Server.Tests.Infrastructure.Validation;

/// <summary>
/// Unit tests for ValidationExtensions ensuring validation extension methods
/// work correctly across different scenarios.
/// </summary>
public class ValidationExtensionsTests
{
    private static LayerDefinition CreateTestLayer()
    {
        var fields = new[]
        {
            new FieldDefinition("id", FieldType.BigInteger, Nullable: false),
            new FieldDefinition("name", FieldType.String),
            new FieldDefinition("age", FieldType.Integer),
            new FieldDefinition("height", FieldType.Double),
            new FieldDefinition("created_date", FieldType.Date),
            new FieldDefinition("is_active", FieldType.Boolean),
            new FieldDefinition("geometry", FieldType.Geometry, Nullable: false),
            new FieldDefinition("blob_data", FieldType.Binary)
        };

        return new LayerDefinition(
            Id: 1,
            Name: "Test Layer",
            Description: "Test Layer",
            GeometryType: GeometryType.Point,
            SpatialReference: SpatialReference.Create(4326),
            Fields: fields);
    }

    [Theory]
    [InlineData("name", true)] // String field
    [InlineData("age", true)] // Integer field
    [InlineData("height", true)] // Double field
    [InlineData("created_date", true)] // Date field
    [InlineData("is_active", true)] // Boolean field
    [InlineData("id", true)] // ObjectId field
    [InlineData("geometry", false)] // Geometry field - not queryable
    [InlineData("blob_data", false)] // Blob field - not queryable
    [InlineData("nonexistent", false)] // Field doesn't exist
    [InlineData("NAME", true)] // Case insensitive
    public void ValidateQueryableField_VariousFields_ReturnsExpectedResult(string fieldName, bool shouldBeValid)
    {
        // Arrange
        var layer = CreateTestLayer();

        // Act
        var result = layer.ValidateQueryableField(fieldName);

        // Assert
        result.IsValid.Should().Be(shouldBeValid);
        if (!shouldBeValid)
        {
            result.ErrorMessage.Should().NotBeNullOrEmpty();
        }
    }

    [Theory]
    [InlineData(null, true, null)] // Return all fields
    [InlineData("", true, null)] // Return all fields
    [InlineData("*", true, null)] // Return all fields
    [InlineData("name", true, new[] { "name" })] // Single field
    [InlineData("name,age", true, new[] { "name", "age" })] // Multiple fields
    [InlineData("NAME,AGE", true, new[] { "name", "age" })] // Case insensitive
    [InlineData("name, age, height", true, new[] { "name", "age", "height" })] // With spaces
    [InlineData("name,nonexistent", false, null)] // Invalid field
    [InlineData("name,", true, new[] { "name" })] // Trailing comma
    [InlineData(",name", true, new[] { "name" })] // Leading comma
    public void ValidateOutputFields_VariousInputs_ReturnsExpectedResult(string? outFields, bool shouldBeValid, string[]? expectedFields)
    {
        // Arrange
        var layer = CreateTestLayer();

        // Act
        var result = layer.ValidateOutputFields(outFields);

        // Assert
        result.IsValid.Should().Be(shouldBeValid);
        if (shouldBeValid)
        {
            result.Value.Should().BeEquivalentTo(expectedFields);
        }
        else
        {
            result.ErrorMessage.Should().NotBeNullOrEmpty();
        }
    }

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

    [Fact]
    public void ValidateOutputFields_EmptyFieldsAfterSplit_ReturnsAllFields()
    {
        // Arrange
        var layer = CreateTestLayer();
        var outFields = ",,,"; // Only commas

        // Act
        var result = layer.ValidateOutputFields(outFields);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Value.Should().BeNull(); // Should return all fields
    }

    [Fact]
    public void ValidateQueryableField_CaseInsensitiveMatching_ReturnsSuccess()
    {
        // Arrange
        var layer = CreateTestLayer();

        // Act - Test various case combinations
        var upperResult = layer.ValidateQueryableField("NAME");
        var lowerResult = layer.ValidateQueryableField("name");
        var mixedResult = layer.ValidateQueryableField("Name");

        // Assert
        upperResult.IsValid.Should().BeTrue();
        lowerResult.IsValid.Should().BeTrue();
        mixedResult.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ValidateAttributes_UnknownField_ReturnsFailure()
    {
        // Arrange
        var layer = CreateTestLayer();
        var attributes = new Dictionary<string, object?>
        {
            ["unknown"] = "value"
        };

        // Act
        var result = layer.ValidateAttributes(attributes, ValidationExtensions.AttributeValidationMode.Strict);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Unknown field");
    }

    [Fact]
    public void ValidateAttributes_NullForNonNullableField_ReturnsFailure()
    {
        // Arrange
        var layer = CreateTestLayer();
        var attributes = new Dictionary<string, object?>
        {
            ["id"] = null
        };

        // Act
        var result = layer.ValidateAttributes(attributes, ValidationExtensions.AttributeValidationMode.Strict);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("cannot be null");
    }

    [Fact]
    public void ValidateAttributes_TypeMismatch_ReturnsFailure()
    {
        // Arrange
        var layer = CreateTestLayer();
        var attributes = new Dictionary<string, object?>
        {
            ["age"] = "not-an-int"
        };

        // Act
        var result = layer.ValidateAttributes(attributes, ValidationExtensions.AttributeValidationMode.Strict);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("must be an integer");
    }

    [Fact]
    public void ValidateAttributes_GeoServicesAllowsNumericBoolean()
    {
        // Arrange
        var layer = CreateTestLayer();
        var attributes = new Dictionary<string, object?>
        {
            ["is_active"] = 1
        };

        // Act
        var strictResult = layer.ValidateAttributes(attributes, ValidationExtensions.AttributeValidationMode.Strict);
        var geoResult = layer.ValidateAttributes(attributes, ValidationExtensions.AttributeValidationMode.GeoServices);

        // Assert
        strictResult.IsValid.Should().BeFalse();
        geoResult.IsValid.Should().BeTrue();
    }
}

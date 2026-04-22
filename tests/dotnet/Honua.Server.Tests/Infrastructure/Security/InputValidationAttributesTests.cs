// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.TestKit.Attributes;
using Xunit.Abstractions;
using DataAnnotationsValidationResult = System.ComponentModel.DataAnnotations.ValidationResult;

namespace Honua.Server.Tests.Infrastructure.Security;

/// <summary>
/// Unit tests for security validation attributes.
/// </summary>
public class InputValidationAttributesTests
{
    private readonly ITestOutputHelper _output;

    public InputValidationAttributesTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData("valid_table_name", true)]
    [InlineData("table123", true)]
    [InlineData("_underscore_start", true)]
    [InlineData("DROP_TABLE", false)] // SQL reserved word
    [InlineData("123invalid", false)] // Starts with number
    [InlineData("table-name", false)] // Contains dash
    [InlineData("table name", false)] // Contains space
    [InlineData("", false)] // Empty
    [InlineData(null, false)] // Null
    [SecurityTest]
    public void SafeSqlIdentifierAttribute_VariousInputs_ReturnsExpectedResult(string? input, bool shouldBeValid)
    {
        // Arrange
        var attribute = new SafeSqlIdentifierAttribute();
        var context = new ValidationContext(new object());

        // Act
        var result = attribute.GetValidationResult(input, context);

        // Assert
        bool isValid = result == DataAnnotationsValidationResult.Success;
        Assert.Equal(shouldBeValid, isValid);

        if (!shouldBeValid && result != null)
        {
            _output.WriteLine($"Input '{input}': Invalid - {result.ErrorMessage}");
        }
        else
        {
            _output.WriteLine($"Input '{input}': Valid");
        }
    }

    [Theory]
    [InlineData(4326, true)]  // WGS84
    [InlineData(3857, true)]  // Web Mercator
    [InlineData(0, true)]     // Unknown/undefined
    [InlineData(-1, false)]   // Negative
    [InlineData(1000000, false)] // Too large
    [SecurityTest]
    public void ValidSridAttribute_VariousInputs_ReturnsExpectedResult(int? input, bool shouldBeValid)
    {
        // Arrange
        var attribute = new ValidSridAttribute();
        var context = new ValidationContext(new object());

        // Act
        var result = attribute.GetValidationResult(input, context);

        // Assert
        bool isValid = result == DataAnnotationsValidationResult.Success;
        Assert.Equal(shouldBeValid, isValid);

        _output.WriteLine($"SRID {input}: {(isValid ? "Valid" : $"Invalid - {result?.ErrorMessage}")}");
    }

    [Theory]
    [InlineData(-180.0, CoordinateType.Longitude, true)]
    [InlineData(180.0, CoordinateType.Longitude, true)]
    [InlineData(0.0, CoordinateType.Longitude, true)]
    [InlineData(-181.0, CoordinateType.Longitude, false)]
    [InlineData(181.0, CoordinateType.Longitude, false)]
    [InlineData(-90.0, CoordinateType.Latitude, true)]
    [InlineData(90.0, CoordinateType.Latitude, true)]
    [InlineData(-91.0, CoordinateType.Latitude, false)]
    [InlineData(91.0, CoordinateType.Latitude, false)]
    [SecurityTest]
    public void ValidCoordinateAttribute_VariousInputs_ReturnsExpectedResult(double input, CoordinateType type, bool shouldBeValid)
    {
        // Arrange
        var attribute = new ValidCoordinateAttribute(type);
        var context = new ValidationContext(new object());

        // Act
        var result = attribute.GetValidationResult(input, context);

        // Assert
        bool isValid = result == DataAnnotationsValidationResult.Success;
        Assert.Equal(shouldBeValid, isValid);

        _output.WriteLine($"{type} {input}: {(isValid ? "Valid" : $"Invalid - {result?.ErrorMessage}")}");
    }

    [Theory]
    [InlineData(".csv", new[] { ".csv", ".txt" }, true)]
    [InlineData(".txt", new[] { ".csv", ".txt" }, true)]
    [InlineData(".exe", new[] { ".csv", ".txt" }, false)]
    [InlineData(".CSV", new[] { ".csv", ".txt" }, true)] // Case insensitive
    [SecurityTest]
    public void AllowedFileExtensionAttribute_VariousInputs_ReturnsExpectedResult(string extension, string[] allowedExtensions, bool shouldBeValid)
    {
        // Arrange
        var fileName = "test" + extension;
        var attribute = new AllowedFileExtensionAttribute(allowedExtensions);
        var context = new ValidationContext(new object());

        // Act
        var result = attribute.GetValidationResult(fileName, context);

        // Assert
        bool isValid = result == DataAnnotationsValidationResult.Success;
        Assert.Equal(shouldBeValid, isValid);

        _output.WriteLine($"File '{fileName}' with allowed {string.Join(", ", allowedExtensions)}: {(isValid ? "Valid" : $"Invalid - {result?.ErrorMessage}")}");
    }

    [Theory]
    [InlineData("normal text", true)]
    [InlineData("Text with spaces and 123 numbers", true)]
    [InlineData("<script>alert('xss')</script>", false)]
    [InlineData("Text with & ampersand", false)]
    [InlineData("Text with \" quotes", false)]
    [InlineData("Text\x00with\x1fnull\x7fchars", false)]
    [SecurityTest]
    public void SafeStringAttribute_VariousInputs_ReturnsExpectedResult(string? input, bool shouldBeValid)
    {
        // Arrange
        var attribute = new SafeStringAttribute();
        var context = new ValidationContext(new object());

        // Act
        var result = attribute.GetValidationResult(input, context);

        // Assert
        bool isValid = result == DataAnnotationsValidationResult.Success;
        Assert.Equal(shouldBeValid, isValid);

        _output.WriteLine($"String '{input}': {(isValid ? "Safe" : $"Unsafe - {result?.ErrorMessage}")}");
    }

    [Theory]
    [InlineData("name = 'John'", true)]
    [InlineData("age > 18 AND status = 'active'", true)]
    [InlineData("id IN (1, 2, 3)", true)]
    [InlineData("name LIKE '%smith%'", true)]
    [InlineData("1=1; DROP TABLE users--", false)]
    [InlineData("1=1 UNION SELECT * FROM passwords", false)]
    [InlineData("'; exec xp_cmdshell('dir'); --", false)]
    [InlineData("/*comment*/ OR 1=1 /**/", false)]
    [SecurityTest]
    public void SafeWhereClauseAttribute_VariousInputs_ReturnsExpectedResult(string? input, bool shouldBeValid)
    {
        // Arrange
        var attribute = new SafeWhereClauseAttribute();
        var context = new ValidationContext(new object());

        // Act
        var result = attribute.GetValidationResult(input, context);

        // Assert
        bool isValid = result == DataAnnotationsValidationResult.Success;
        Assert.Equal(shouldBeValid, isValid);

        _output.WriteLine($"WHERE clause '{input}': {(isValid ? "Safe" : $"Dangerous - {result?.ErrorMessage}")}");
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(100, true)]
    [InlineData(10000, true)] // Default max
    [InlineData(10001, false)] // Over default max
    [InlineData(-1, false)] // Negative
    [SecurityTest]
    public void ValidPaginationAttribute_VariousInputs_ReturnsExpectedResult(int input, bool shouldBeValid)
    {
        // Arrange
        var attribute = new ValidPaginationAttribute();
        var context = new ValidationContext(new object());

        // Act
        var result = attribute.GetValidationResult(input, context);

        // Assert
        bool isValid = result == DataAnnotationsValidationResult.Success;
        Assert.Equal(shouldBeValid, isValid);

        _output.WriteLine($"Pagination value {input}: {(isValid ? "Valid" : $"Invalid - {result?.ErrorMessage}")}");
    }

    [Fact]
    [SecurityTest]
    public void ValidPaginationAttribute_CustomMaxValue_RespectsLimit()
    {
        // Arrange
        var attribute = new ValidPaginationAttribute(maxValue: 500);
        var context = new ValidationContext(new object());

        // Act
        var validResult = attribute.GetValidationResult(400, context);
        var invalidResult = attribute.GetValidationResult(600, context);

        // Assert
        Assert.Equal(DataAnnotationsValidationResult.Success, validResult);
        Assert.NotEqual(DataAnnotationsValidationResult.Success, invalidResult);

        _output.WriteLine($"Custom max value test: 400 is valid, 600 is invalid with max 500");
    }
}

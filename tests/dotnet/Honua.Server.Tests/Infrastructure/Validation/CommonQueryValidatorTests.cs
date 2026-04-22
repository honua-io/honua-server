// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Validation;

/// <summary>
/// Unit tests for CommonQueryValidator ensuring consistent validation behavior
/// across all protocols and endpoints.
/// </summary>
public class CommonQueryValidatorTests
{
    private readonly CommonQueryValidator _validator;
    private readonly LimitsOptions _limitsOptions;

    public CommonQueryValidatorTests()
    {
        _limitsOptions = new LimitsOptions
        {
            Query = new QueryLimits
            {
                MaxRecordCount = 1000,
                MaxOffset = 10000,
                DefaultRecordCount = 100
            }
        };

        var options = Options.Create(_limitsOptions);
        _validator = new CommonQueryValidator(options);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(0, 100, true)]
    [InlineData(100, 500, true)]
    [InlineData(10000, 1000, true)]
    [InlineData(-1, 100, false)] // Negative offset
    [InlineData(100, -1, false)] // Negative limit
    [InlineData(10001, 100, false)] // Offset exceeds max
    [InlineData(100, 1001, false)] // Limit exceeds max
    public void ValidatePagination_VariousInputs_ReturnsExpectedResult(int? offset, int? limit, bool shouldBeValid)
    {
        // Act
        var result = _validator.ValidatePagination(offset, limit);

        // Assert
        result.IsValid.Should().Be(shouldBeValid);
        if (!shouldBeValid)
        {
            result.ErrorMessage.Should().NotBeNullOrEmpty();
        }
    }

    [Theory]
    [InlineData(null, new[] { "json", "geojson" }, "json")] // Default to first format
    [InlineData("", new[] { "json", "geojson" }, "json")] // Empty defaults to first format
    [InlineData("json", new[] { "json", "geojson" }, "json")] // Valid format
    [InlineData("JSON", new[] { "json", "geojson" }, "json")] // Case insensitive
    [InlineData("geojson", new[] { "json", "geojson" }, "geojson")] // Valid format
    [InlineData("xml", new[] { "json", "geojson" }, null)] // Invalid format
    public void ValidateFormat_VariousInputs_ReturnsExpectedResult(string? format, string[] allowedFormats, string? expectedResult)
    {
        // Arrange
        var allowedSet = new HashSet<string>(allowedFormats);

        // Act
        var result = _validator.ValidateFormat(format, allowedSet);

        // Assert
        if (expectedResult != null)
        {
            result.IsValid.Should().BeTrue();
            result.Value.Should().Be(expectedResult);
        }
        else
        {
            result.IsValid.Should().BeFalse();
            result.ErrorMessage.Should().NotBeNullOrEmpty();
        }
    }

    [Theory]
    [InlineData(null, true, null)] // Null SRID is allowed
    [InlineData("", true, null)] // Empty SRID is allowed
    [InlineData("4326", true, 4326)] // Valid SRID
    [InlineData("0", true, 0)] // Zero SRID is valid
    [InlineData("999999", true, 999999)] // Max valid SRID
    [InlineData("abc", false, null)] // Invalid format
    [InlineData("-1", false, null)] // Negative SRID
    [InlineData("1000000", false, null)] // SRID too large
    public void ValidateSrid_VariousInputs_ReturnsExpectedResult(string? srid, bool shouldBeValid, int? expectedValue)
    {
        // Act
        var result = _validator.ValidateSrid(srid, "testSRID");

        // Assert
        result.IsValid.Should().Be(shouldBeValid);
        if (shouldBeValid)
        {
            result.Value.Should().Be(expectedValue);
        }
        else
        {
            result.ErrorMessage.Should().NotBeNullOrEmpty();
            if (srid == "abc")
            {
                result.ErrorMessage.Should().Contain("testSRID");
            }
            else
            {
                result.ErrorMessage.Should().Contain("SRID must be between 0 and 999,999");
            }
        }
    }

    [Fact]
    public void ValidateAllowedParameters_ValidParameters_ReturnsSuccess()
    {
        // Arrange
        var queryCollection = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            { "f", "json" },
            { "bbox", "0,0,1,1" }
        });
        var allowedParams = new HashSet<string> { "f", "bbox", "limit", "offset" };

        // Act
        var result = _validator.ValidateAllowedParameters(queryCollection.Keys.ToArray(), allowedParams);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ValidateAllowedParameters_InvalidParameter_ReturnsFailure()
    {
        // Arrange
        var queryCollection = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            { "f", "json" },
            { "invalidParam", "value" }
        });
        var allowedParams = new HashSet<string> { "f", "bbox", "limit", "offset" };

        // Act
        var result = _validator.ValidateAllowedParameters(queryCollection.Keys.ToArray(), allowedParams);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("invalidParam");
    }

    [Theory]
    [InlineData(null, true)] // Null bbox is allowed
    [InlineData("", true)] // Empty bbox is allowed
    [InlineData("0,0,1,1", true)] // Valid bbox
    [InlineData("-180,-90,180,90", true)] // Geographic bounds for WGS84
    [InlineData("170,-10,-170,10", true)] // Antimeridian crossing for WGS84
    [InlineData("0,0,1", false)] // Too few coordinates
    [InlineData("0,0,1,1,1", false)] // Too many coordinates
    [InlineData("a,b,c,d", false)] // Invalid numbers
    [InlineData("1,1,0,0", false)] // Min > max coordinates
    [InlineData("-181,-90,180,90", false)] // Invalid longitude for WGS84
    [InlineData("-180,-91,180,90", false)] // Invalid latitude for WGS84
    public void ValidateBbox_VariousInputs_ReturnsExpectedResult(string? bbox, bool shouldBeValid)
    {
        // Act
        var result = _validator.ValidateBbox(bbox, 4326); // WGS84

        // Assert
        result.IsValid.Should().Be(shouldBeValid);
        if (!shouldBeValid && !string.IsNullOrEmpty(bbox))
        {
            result.ErrorMessage.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public void ValidateBbox_WithGeographicCrsOutsideNarrowAllowlist_AllowsAntimeridian()
    {
        var result = _validator.ValidateBbox("170,-10,-170,10", 4230);

        result.IsValid.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.MinX.Should().BeGreaterThan(result.Value.MaxX);
    }

    [Fact]
    public void ValidateBbox_WithProjectedCrs_DisallowsAntimeridian()
    {
        var result = _validator.ValidateBbox("170,-10,-170,10", 3857);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData(null, true)] // Null where clause allowed
    [InlineData("", true)] // Empty where clause allowed
    [InlineData("name = 'test'", true)] // Valid where clause
    [InlineData("id > 100", true)] // Valid where clause
    [InlineData("name = 'test'; DROP TABLE users;", false)] // SQL injection attempt
    [InlineData("1=1 OR 1=1", false)] // SQL injection pattern
    [InlineData("name LIKE '%test%'", true)] // Valid LIKE clause
    [InlineData("/* comment */ name = 'test'", false)] // SQL comment pattern
    [InlineData("name = 'test' -- comment", false)] // SQL comment pattern
    public void ValidateWhereClause_VariousInputs_ReturnsExpectedResult(string? whereClause, bool shouldBeValid)
    {
        // Act
        var result = _validator.ValidateWhereClause(whereClause);

        // Assert
        result.IsValid.Should().Be(shouldBeValid);
        if (!shouldBeValid)
        {
            result.ErrorMessage.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public void ValidateWhereClause_TooLong_ReturnsFailure()
    {
        // Arrange
        var longWhereClause = new string('a', 4001); // Exceeds 4000 character limit

        // Act
        var result = _validator.ValidateWhereClause(longWhereClause);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("too long");
    }

    [Fact]
    public void ValidateBbox_NonGeographicSRID_SkipsCoordinateValidation()
    {
        // Arrange - Use web mercator coordinates that would be invalid for WGS84
        var webMercatorBbox = "-20000000,-20000000,20000000,20000000";

        // Act
        var result = _validator.ValidateBbox(webMercatorBbox, 3857); // Web Mercator

        // Assert
        result.IsValid.Should().BeTrue(); // Should pass because we don't validate non-geographic SRIDs
        result.Value.Should().NotBeNull();
        result.Value!.MinX.Should().Be(-20000000);
        result.Value!.MaxX.Should().Be(20000000);
    }
}

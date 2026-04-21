// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.FeatureServer.Services;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.FeatureServer.Services;

/// <summary>
/// Tests for FeatureQueryValidator - critical security component for query validation
/// </summary>
public sealed class FeatureQueryValidatorTests
{
    private readonly ICommonQueryValidator _mockCommonQueryValidator;
    private readonly FeatureQueryValidator _validator;

    public FeatureQueryValidatorTests()
    {
        _mockCommonQueryValidator = Substitute.For<ICommonQueryValidator>();
        _validator = new FeatureQueryValidator(_mockCommonQueryValidator);
    }

    [Fact]
    [UnitTest]
    public void Constructor_NullCommonQueryValidator_ThrowsArgumentNullException()
    {
        // Act & Assert
        var act = () => new FeatureQueryValidator(null!);
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("commonQueryValidator");
    }

    [Fact]
    [UnitTest]
    public void ValidateQueryLimits_ValidPagination_ReturnsValidResult()
    {
        // Arrange
        var queryParams = new QueryParameters
        {
            ResultOffset = 0,
            ResultRecordCount = 100,
            Where = "1=1"
        };

        var paginationResult = QueryValidationResult<PaginationParameters>.Valid(
            new PaginationParameters(Offset: 0, Limit: 100));

        _mockCommonQueryValidator
            .ValidateAndNormalizePagination(0, 100, Arg.Any<PaginationValidationOptions>())
            .Returns(paginationResult);

        // Act
        var result = _validator.ValidateQueryLimits(queryParams);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.ResultRecordCount.Should().Be(100);
        result.Value.ResultOffset.Should().Be(0);
    }

    [Fact]
    [UnitTest]
    public void ValidateQueryLimits_InvalidPagination_ReturnsInvalidResult()
    {
        // Arrange
        var queryParams = new QueryParameters
        {
            ResultOffset = -1,
            ResultRecordCount = -100
        };

        var paginationResult = QueryValidationResult<PaginationParameters>.Invalid("Invalid pagination parameters");

        _mockCommonQueryValidator
            .ValidateAndNormalizePagination(-1, -100, Arg.Any<PaginationValidationOptions>())
            .Returns(paginationResult);

        // Act
        var result = _validator.ValidateQueryLimits(queryParams);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be("Invalid pagination parameters");
    }

    [Fact]
    [UnitTest]
    public void ValidateQueryLimits_ObjectIdsWithoutResultRecordCount_UsesObjectIdsLength()
    {
        // Arrange
        var queryParams = new QueryParameters
        {
            ObjectIds = new long[] { 1, 2, 3, 4, 5 },
            ResultRecordCount = null,  // No explicit limit
            ResultOffset = 0
        };

        var paginationResult = QueryValidationResult<PaginationParameters>.Valid(
            new PaginationParameters(Offset: 0, Limit: 5));

        _mockCommonQueryValidator
            .ValidateAndNormalizePagination(0, 5, Arg.Any<PaginationValidationOptions>())
            .Returns(paginationResult);

        // Act
        var result = _validator.ValidateQueryLimits(queryParams);

        // Assert
        result.IsValid.Should().BeTrue();
        _mockCommonQueryValidator.Received(1)
            .ValidateAndNormalizePagination(0, 5, Arg.Any<PaginationValidationOptions>());
    }

    [Fact]
    [UnitTest]
    public void ValidateQueryLimits_ObjectIdsWithExplicitResultRecordCount_UsesExplicitValue()
    {
        // Arrange
        var queryParams = new QueryParameters
        {
            ObjectIds = new long[] { 1, 2, 3, 4, 5 },
            ResultRecordCount = 10,  // Explicit limit larger than ObjectIds
            ResultOffset = 0
        };

        var paginationResult = QueryValidationResult<PaginationParameters>.Valid(
            new PaginationParameters(Offset: 0, Limit: 10));

        _mockCommonQueryValidator
            .ValidateAndNormalizePagination(0, 10, Arg.Any<PaginationValidationOptions>())
            .Returns(paginationResult);

        // Act
        var result = _validator.ValidateQueryLimits(queryParams);

        // Assert
        result.IsValid.Should().BeTrue();
        _mockCommonQueryValidator.Received(1)
            .ValidateAndNormalizePagination(0, 10, Arg.Any<PaginationValidationOptions>());
    }

    [Fact]
    [UnitTest]
    public void ValidateQueryLimits_EmptyObjectIds_UsesNullResultRecordCount()
    {
        // Arrange
        var queryParams = new QueryParameters
        {
            ObjectIds = Array.Empty<long>(),
            ResultRecordCount = null,
            ResultOffset = 0
        };

        var paginationResult = QueryValidationResult<PaginationParameters>.Valid(
            new PaginationParameters(Offset: 0, Limit: null));

        _mockCommonQueryValidator
            .ValidateAndNormalizePagination(0, null, Arg.Any<PaginationValidationOptions>())
            .Returns(paginationResult);

        // Act
        var result = _validator.ValidateQueryLimits(queryParams);

        // Assert
        result.IsValid.Should().BeTrue();
        _mockCommonQueryValidator.Received(1)
            .ValidateAndNormalizePagination(0, null, Arg.Any<PaginationValidationOptions>());
    }

    [Fact]
    [UnitTest]
    public void ValidateQueryLimits_PreservesAllQueryParameters()
    {
        // Arrange
        var queryParams = new QueryParameters
        {
            Where = "OBJECTID > 100",
            OutFields = "field1,field2",
            OrderByFields = "field1 ASC",
            ResultOffset = 10,
            ResultRecordCount = 50,
            ObjectIds = new long[] { 1, 2, 3 },
            GeometryType = "esriGeometryPoint",
            Geometry = "test-geometry",
            InSr = 4326,
            SpatialRel = "esriSpatialRelIntersects"
        };

        var paginationResult = QueryValidationResult<PaginationParameters>.Valid(
            new PaginationParameters(Offset: 10, Limit: 50));

        _mockCommonQueryValidator
            .ValidateAndNormalizePagination(10, 50, Arg.Any<PaginationValidationOptions>())
            .Returns(paginationResult);

        // Act
        var result = _validator.ValidateQueryLimits(queryParams);

        // Assert
        result.IsValid.Should().BeTrue();
        var validatedParams = result.Value!;

        validatedParams.Where.Should().Be("OBJECTID > 100");
        validatedParams.OutFields.Should().Be("field1,field2");
        validatedParams.OrderByFields.Should().Be("field1 ASC");
        validatedParams.ResultOffset.Should().Be(10);
        validatedParams.ResultRecordCount.Should().Be(50);
        validatedParams.ObjectIds.Should().BeEquivalentTo(new long[] { 1, 2, 3 });
        validatedParams.GeometryType.Should().Be("esriGeometryPoint");
        validatedParams.Geometry.Should().Be("test-geometry");
        validatedParams.InSr.Should().Be(4326);
        validatedParams.SpatialRel.Should().Be("esriSpatialRelIntersects");
    }

    [Theory]
    [UnitTest]
    [InlineData(null, "Error message from validator")]
    [InlineData("", "Invalid pagination parameters.")]
    public void ValidateQueryLimits_ValidationFailsWithDifferentMessages_ReturnsCorrectErrorMessage(
        string? validatorErrorMessage, string expectedErrorMessage)
    {
        // Arrange
        var queryParams = new QueryParameters { ResultOffset = 0, ResultRecordCount = 100 };

        var paginationResult = QueryValidationResult<PaginationParameters>.Invalid(validatorErrorMessage);

        _mockCommonQueryValidator
            .ValidateAndNormalizePagination(0, 100, Arg.Any<PaginationValidationOptions>())
            .Returns(paginationResult);

        // Act
        var result = _validator.ValidateQueryLimits(queryParams);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be(expectedErrorMessage);
    }
}
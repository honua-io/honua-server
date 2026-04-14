// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Honua.Core.Tests.Features.Configuration;

public class StandardTtlOptionsTests
{
    [Fact]
    public void Constructor_WithDevelopmentEnvironment_SetsShortTtls()
    {
        // Arrange
        var environment = CreateEnvironment("Development");

        // Act
        var options = new StandardTtlOptions(environment);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(30), options.VeryShort);
        Assert.Equal(TimeSpan.FromMinutes(2), options.Short);
        Assert.Equal(TimeSpan.FromMinutes(5), options.Medium);
        Assert.Equal(TimeSpan.FromMinutes(30), options.Long);
        Assert.Equal(TimeSpan.FromHours(2), options.VeryLong);
    }

    [Fact]
    public void Constructor_WithProductionEnvironment_SetsLongTtls()
    {
        // Arrange
        var environment = CreateEnvironment("Production");

        // Act
        var options = new StandardTtlOptions(environment);

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(2), options.VeryShort);
        Assert.Equal(TimeSpan.FromMinutes(5), options.Short);
        Assert.Equal(TimeSpan.FromMinutes(30), options.Medium);
        Assert.Equal(TimeSpan.FromHours(2), options.Long);
        Assert.Equal(TimeSpan.FromDays(1), options.VeryLong);
    }

    [Fact]
    public void GetTtl_ReturnsCorrectValueForCategory()
    {
        // Arrange
        var options = new StandardTtlOptions();

        // Act & Assert
        Assert.Equal(options.VeryShort, options.GetTtl(TtlCategory.VeryShort));
        Assert.Equal(options.Short, options.GetTtl(TtlCategory.Short));
        Assert.Equal(options.Medium, options.GetTtl(TtlCategory.Medium));
        Assert.Equal(options.Long, options.GetTtl(TtlCategory.Long));
        Assert.Equal(options.VeryLong, options.GetTtl(TtlCategory.VeryLong));
    }

    [Fact]
    public void ValidateTtlOrdering_WithValidOrdering_ReturnsTrue()
    {
        // Arrange
        var options = new StandardTtlOptions
        {
            VeryShort = TimeSpan.FromMinutes(1),
            Short = TimeSpan.FromMinutes(5),
            Medium = TimeSpan.FromMinutes(30),
            Long = TimeSpan.FromHours(2),
            VeryLong = TimeSpan.FromDays(1)
        };

        // Act
        var result = options.ValidateTtlOrdering();

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void ValidateTtlOrdering_WithInvalidOrdering_ReturnsFalse()
    {
        // Arrange
        var options = new StandardTtlOptions
        {
            VeryShort = TimeSpan.FromMinutes(30), // Too long
            Short = TimeSpan.FromMinutes(5),      // Shorter than VeryShort
            Medium = TimeSpan.FromMinutes(30),
            Long = TimeSpan.FromHours(2),
            VeryLong = TimeSpan.FromDays(1)
        };

        // Act
        var result = options.ValidateTtlOrdering();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void ValidateTtlOrdering_WithNegativeTtl_ReturnsFalse()
    {
        // Arrange
        var options = new StandardTtlOptions
        {
            VeryShort = TimeSpan.FromMinutes(-1), // Negative
            Short = TimeSpan.FromMinutes(5),
            Medium = TimeSpan.FromMinutes(30),
            Long = TimeSpan.FromHours(2),
            VeryLong = TimeSpan.FromDays(1)
        };

        // Act
        var result = options.ValidateTtlOrdering();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void GetAllTtls_ReturnsAllCategories()
    {
        // Arrange
        var options = new StandardTtlOptions();

        // Act
        var allTtls = options.GetAllTtls();

        // Assert
        Assert.Equal(5, allTtls.Count);
        Assert.Contains("VeryShort", allTtls.Keys);
        Assert.Contains("Short", allTtls.Keys);
        Assert.Contains("Medium", allTtls.Keys);
        Assert.Contains("Long", allTtls.Keys);
        Assert.Contains("VeryLong", allTtls.Keys);
    }

    [Theory]
    [InlineData(TtlCategory.VeryShort, "Frequently changing data (user sessions, real-time metrics)")]
    [InlineData(TtlCategory.Short, "Semi-static data (layer configurations, service metadata)")]
    [InlineData(TtlCategory.Medium, "Stable data (feature schemas, service capabilities)")]
    [InlineData(TtlCategory.Long, "Rarely changing data (coordinate systems, static configurations)")]
    [InlineData(TtlCategory.VeryLong, "Immutable data (tile matrix sets, projection definitions)")]
    public void TtlCategoryExtensions_GetDescription_ReturnsCorrectDescription(TtlCategory category, string expectedDescription)
    {
        // Act
        var description = category.GetDescription();

        // Assert
        Assert.Equal(expectedDescription, description);
    }

    [Theory]
    [InlineData("session", TtlCategory.VeryShort)]
    [InlineData("metadata", TtlCategory.Short)]
    [InlineData("schema", TtlCategory.Medium)]
    [InlineData("coordinate", TtlCategory.Long)]
    [InlineData("tilematrix", TtlCategory.VeryLong)]
    [InlineData("unknown", TtlCategory.Medium)] // Default
    public void TtlCategoryExtensions_GetRecommendedCategory_ReturnsCorrectCategory(string dataType, TtlCategory expectedCategory)
    {
        // Act
        var category = TtlCategoryExtensions.GetRecommendedCategory(dataType);

        // Assert
        Assert.Equal(expectedCategory, category);
    }

    private static TestHostEnvironment CreateEnvironment(string environmentName)
    {
        return new TestHostEnvironment { EnvironmentName = environmentName };
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Microsoft.Extensions.Configuration;

namespace Honua.Core.Tests.Configuration;

/// <summary>
/// Unit tests for ConfigurationExtensions.
/// </summary>
public class ConfigurationExtensionsTests
{
    [Fact]
    public void IsFeatureEnabled_WithTrueValue_ReturnsTrue()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HONUA_SAMPLE"] = "true"
            })
            .Build();

        // Act
        var result = config.IsFeatureEnabled("SAMPLE");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsFeatureEnabled_WithOneValue_ReturnsTrue()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HONUA_OBSERVABILITY"] = "1"
            })
            .Build();

        // Act
        var result = config.IsFeatureEnabled("OBSERVABILITY");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsFeatureEnabled_WithYesValue_ReturnsTrue()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HONUA_FEATURE"] = "yes"
            })
            .Build();

        // Act
        var result = config.IsFeatureEnabled("FEATURE");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsFeatureEnabled_WithOnValue_ReturnsTrue()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HONUA_FEATURE"] = "on"
            })
            .Build();

        // Act
        var result = config.IsFeatureEnabled("FEATURE");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsFeatureEnabled_WithFalseValue_ReturnsFalse()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HONUA_SAMPLE"] = "false"
            })
            .Build();

        // Act
        var result = config.IsFeatureEnabled("SAMPLE");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsFeatureEnabled_WithMissingKey_ReturnsFalse()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        // Act
        var result = config.IsFeatureEnabled("NONEXISTENT");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsFeatureEnabled_WithEmptyValue_ReturnsFalse()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HONUA_EMPTY"] = ""
            })
            .Build();

        // Act
        var result = config.IsFeatureEnabled("EMPTY");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsFeatureEnabled_CaseInsensitive_ReturnsTrue()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HONUA_FEATURE"] = "TRUE"
            })
            .Build();

        // Act
        var result = config.IsFeatureEnabled("feature");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void GetRequiredValue_WithExistingKey_ReturnsValue()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=test"
            })
            .Build();

        // Act
        var result = config.GetRequiredValue("ConnectionStrings:DefaultConnection");

        // Assert
        Assert.Equal("Host=localhost;Database=test", result);
    }

    [Fact]
    public void GetRequiredValue_WithMissingKey_ThrowsWithHelpfulMessage()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() =>
            config.GetRequiredValue("ConnectionStrings:DefaultConnection"));

        Assert.Contains("ConnectionStrings:DefaultConnection", ex.Message);
        Assert.Contains("ConnectionStrings__DefaultConnection", ex.Message);
        Assert.Contains("environment variable", ex.Message);
    }

    [Fact]
    public void GetRequiredValue_WithEmptyValue_ThrowsWithHelpfulMessage()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SomeKey"] = ""
            })
            .Build();

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() =>
            config.GetRequiredValue("SomeKey"));

        Assert.Contains("SomeKey", ex.Message);
    }

    [Fact]
    public void GetValueOrDefault_WithExistingKey_ReturnsValue()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Port"] = "8080"
            })
            .Build();

        // Act
        var result = config.GetValueOrDefault("Port", 3000);

        // Assert
        Assert.Equal(8080, result);
    }

    [Fact]
    public void GetValueOrDefault_WithMissingKey_ReturnsDefault()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        // Act
        var result = config.GetValueOrDefault("Port", 3000);

        // Assert
        Assert.Equal(3000, result);
    }

    [Fact]
    public void GetValueOrDefault_WithBoolValue_ReturnsCorrectType()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Enabled"] = "true"
            })
            .Build();

        // Act
        var result = config.GetValueOrDefault("Enabled", false);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void GetWithEnvOverride_WithConfigValue_ReturnsValue()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cache:Enabled"] = "true"
            })
            .Build();

        // Act
        var result = config.GetWithEnvOverride("Cache:Enabled");

        // Assert
        Assert.Equal("true", result);
    }

    [Fact]
    public void GetWithEnvOverride_WithMissingKey_ReturnsNull()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        // Act
        var result = config.GetWithEnvOverride("NonExistent:Key");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void IsFeatureEnabled_WithNullConfiguration_ThrowsArgumentNullException()
    {
        // Arrange
        IConfiguration? config = null;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => config!.IsFeatureEnabled("FEATURE"));
    }

    [Fact]
    public void IsFeatureEnabled_WithNullFeatureName_ThrowsArgumentException()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => config.IsFeatureEnabled(null!));
    }

    [Fact]
    public void IsFeatureEnabled_WithEmptyFeatureName_ThrowsArgumentException()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => config.IsFeatureEnabled(""));
    }

    [Fact]
    public void GetRequiredValue_WithNullKey_ThrowsArgumentException()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => config.GetRequiredValue(null!));
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Validation;
using Microsoft.Extensions.Options;
using Xunit;

namespace Honua.Core.Tests.Features.Infrastructure.Validation;

public class ValidationExtensionsTests
{
    private sealed class TestOptions
    {
        public string Value { get; set; } = "default";
    }

    [Fact]
    public void ThrowIfNull_WithValidValue_ReturnsValue()
    {
        // Arrange
        const string testValue = "test";

        // Act
        var result = testValue.ThrowIfNull();

        // Assert
        Assert.Equal(testValue, result);
    }

    [Fact]
    public void ThrowIfNull_WithNullValue_ThrowsArgumentNullException()
    {
        // Arrange
        string? testValue = null;

        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() => testValue.ThrowIfNull());
        Assert.Equal("testValue", exception.ParamName);
    }

    [Fact]
    public void ThrowIfNull_WithNullableStruct_ValidValue_ReturnsValue()
    {
        // Arrange
        int? testValue = 42;

        // Act
        var result = testValue.ThrowIfNull();

        // Assert
        Assert.Equal(42, result);
    }

    [Fact]
    public void ThrowIfNull_WithNullableStruct_NullValue_ThrowsArgumentNullException()
    {
        // Arrange
        int? testValue = null;

        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() => testValue.ThrowIfNull());
        Assert.Equal("testValue", exception.ParamName);
    }

    [Fact]
    public void ValidateAndGetValue_WithValidOptions_ReturnsValue()
    {
        // Arrange
        var options = new TestOptions { Value = "test" };
        var optionsWrapper = Options.Create(options);

        // Act
        var result = optionsWrapper.ValidateAndGetValue();

        // Assert
        Assert.Equal(options, result);
        Assert.Equal("test", result.Value);
    }

    [Fact]
    public void ValidateAndGetValue_WithNullOptions_ThrowsArgumentNullException()
    {
        // Arrange
        IOptions<TestOptions>? options = null;

        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() => options.ValidateAndGetValue());
        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public void ThrowIfNullOrEmpty_WithValidCollection_ReturnsCollection()
    {
        // Arrange
        var collection = new[] { "item1", "item2" };

        // Act
        var result = collection.ThrowIfNullOrEmpty();

        // Assert
        Assert.Equal(collection, result);
    }

    [Fact]
    public void ThrowIfNullOrEmpty_WithNullCollection_ThrowsArgumentNullException()
    {
        // Arrange
        string[]? collection = null;

        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() => collection.ThrowIfNullOrEmpty());
        Assert.Equal("collection", exception.ParamName);
    }

    [Fact]
    public void ThrowIfNullOrEmpty_WithEmptyCollection_ThrowsArgumentException()
    {
        // Arrange
        var collection = Array.Empty<string>();

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => collection.ThrowIfNullOrEmpty());
        Assert.Equal("collection", exception.ParamName);
        Assert.Contains("cannot be empty", exception.Message);
    }

    [Fact]
    public void ThrowIfNullOrEmpty_String_WithValidString_ReturnsString()
    {
        // Arrange
        const string testValue = "test";

        // Act
        var result = testValue.ThrowIfNullOrEmpty();

        // Assert
        Assert.Equal(testValue, result);
    }

    [Fact]
    public void ThrowIfNullOrEmpty_String_WithNullString_ThrowsArgumentNullException()
    {
        // Arrange
        string? testValue = null;

        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() => testValue.ThrowIfNullOrEmpty());
        Assert.Equal("testValue", exception.ParamName);
    }

    [Fact]
    public void ThrowIfNullOrEmpty_String_WithEmptyString_ThrowsArgumentException()
    {
        // Arrange
        const string testValue = "";

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => testValue.ThrowIfNullOrEmpty());
        Assert.Equal("testValue", exception.ParamName);
        Assert.Contains("cannot be empty or whitespace", exception.Message);
    }

    [Fact]
    public void ValidateConstructorParameters_TwoParams_WithValidValues_ReturnsValidatedTuple()
    {
        // Arrange
        const string param1 = "test1";
        const string param2 = "test2";

        // Act
        var result = ValidationExtensions.ValidateConstructorParameters(param1, param2);

        // Assert
        Assert.Equal(param1, result.Item1);
        Assert.Equal(param2, result.Item2);
    }

    [Fact]
    public void ValidateConstructorParameters_TwoParams_WithNullValue_ThrowsArgumentNullException()
    {
        // Arrange
        const string param1 = "test1";
        string? param2 = null;

        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() =>
            ValidationExtensions.ValidateConstructorParameters(param1, param2));
        Assert.Equal("param2", exception.ParamName);
    }

    [Fact]
    public void ValidateConstructorParameters_ThreeParams_WithValidValues_ReturnsValidatedTuple()
    {
        // Arrange
        const string param1 = "test1";
        const string param2 = "test2";
        const string param3 = "test3";

        // Act
        var result = ValidationExtensions.ValidateConstructorParameters(param1, param2, param3);

        // Assert
        Assert.Equal(param1, result.Item1);
        Assert.Equal(param2, result.Item2);
        Assert.Equal(param3, result.Item3);
    }

    [Fact]
    public void ValidateConstructorParameters_FourParams_WithValidValues_ReturnsValidatedTuple()
    {
        // Arrange
        const string param1 = "test1";
        const string param2 = "test2";
        const string param3 = "test3";
        const string param4 = "test4";

        // Act
        var result = ValidationExtensions.ValidateConstructorParameters(param1, param2, param3, param4);

        // Assert
        Assert.Equal(param1, result.Item1);
        Assert.Equal(param2, result.Item2);
        Assert.Equal(param3, result.Item3);
        Assert.Equal(param4, result.Item4);
    }

    [Fact]
    public void ValidateConstructorParameters_FiveParams_WithValidValues_ReturnsValidatedTuple()
    {
        // Arrange
        const string param1 = "test1";
        const string param2 = "test2";
        const string param3 = "test3";
        const string param4 = "test4";
        const string param5 = "test5";

        // Act
        var result = ValidationExtensions.ValidateConstructorParameters(param1, param2, param3, param4, param5);

        // Assert
        Assert.Equal(param1, result.Item1);
        Assert.Equal(param2, result.Item2);
        Assert.Equal(param3, result.Item3);
        Assert.Equal(param4, result.Item4);
        Assert.Equal(param5, result.Item5);
    }

    [Fact]
    public void ValidateCommonDependencies_WithValidValues_ReturnsValidatedTuple()
    {
        // Arrange
        var connectionProvider = new TestConnectionProvider();
        var logger = new TestLogger();
        var options = Options.Create(new TestOptions { Value = "test" });

        // Act
        var result = ValidationExtensions.ValidateCommonDependencies(connectionProvider, logger, options);

        // Assert
        Assert.Equal(connectionProvider, result.Item1);
        Assert.Equal(logger, result.Item2);
        Assert.Equal("test", result.Item3.Value);
    }

    [Fact]
    public void ValidateCommonDependencies_WithNullConnectionProvider_ThrowsArgumentNullException()
    {
        // Arrange
        TestConnectionProvider? connectionProvider = null;
        var logger = new TestLogger();
        var options = Options.Create(new TestOptions());

        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() =>
            ValidationExtensions.ValidateCommonDependencies(connectionProvider, logger, options));
        Assert.Equal("connectionProvider", exception.ParamName);
    }

    // Test classes for dependency validation
    private sealed class TestConnectionProvider;
    private sealed class TestLogger;
}
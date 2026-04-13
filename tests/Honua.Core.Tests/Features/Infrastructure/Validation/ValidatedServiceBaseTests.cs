// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Validation;
using Microsoft.Extensions.Options;
using Xunit;

namespace Honua.Core.Tests.Features.Infrastructure.Validation;

public class ValidatedServiceBaseTests
{
    private sealed class TestService : ValidatedServiceBase
    {
        public TestService(string param1, object param2, IOptions<TestOptions> options)
        {
            TestProperty1 = ValidateRequired(param1);
            TestProperty2 = ValidateRequired(param2);
            TestOptions = ValidateOptions(options);
        }

        public string TestProperty1 { get; }
        public object TestProperty2 { get; }
        public TestOptions TestOptions { get; }
    }

    private sealed class TestOptions
    {
        public string Value { get; set; } = "default";
    }

    [Fact]
    public void ValidateRequired_WithValidValue_ReturnsValue()
    {
        // Arrange
        const string testValue = "test";

        // Act
        var result = ValidatedServiceBase.ValidateRequired(testValue);

        // Assert
        Assert.Equal(testValue, result);
    }

    [Fact]
    public void ValidateRequired_WithNullValue_ThrowsArgumentNullException()
    {
        // Arrange
        string? testValue = null;

        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() => ValidatedServiceBase.ValidateRequired(testValue));
        Assert.Equal("testValue", exception.ParamName);
    }

    [Fact]
    public void ValidateRequired_WithNullableStruct_ValidValue_ReturnsValue()
    {
        // Arrange
        int? testValue = 42;

        // Act
        var result = ValidatedServiceBase.ValidateRequired(testValue);

        // Assert
        Assert.Equal(42, result);
    }

    [Fact]
    public void ValidateRequired_WithNullableStruct_NullValue_ThrowsArgumentNullException()
    {
        // Arrange
        int? testValue = null;

        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() => ValidatedServiceBase.ValidateRequired(testValue));
        Assert.Equal("testValue", exception.ParamName);
    }

    [Fact]
    public void ValidateOptions_WithValidOptions_ReturnsValue()
    {
        // Arrange
        var options = new TestOptions { Value = "test" };
        var optionsWrapper = Options.Create(options);

        // Act
        var result = ValidatedServiceBase.ValidateOptions(optionsWrapper);

        // Assert
        Assert.Equal(options, result);
        Assert.Equal("test", result.Value);
    }

    [Fact]
    public void ValidateOptions_WithNullOptions_ThrowsArgumentNullException()
    {
        // Arrange
        IOptions<TestOptions>? options = null;

        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() => ValidatedServiceBase.ValidateOptions(options));
        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public void ValidateOptions_WithNullOptionsValue_ThrowsArgumentNullException()
    {
        // Arrange
        var optionsWrapper = Options.Create<TestOptions>(null!);

        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() => ValidatedServiceBase.ValidateOptions(optionsWrapper));
        Assert.Equal("options.Value", exception.ParamName);
    }

    [Fact]
    public void ValidateCollectionNotEmpty_WithValidCollection_ReturnsCollection()
    {
        // Arrange
        var collection = new[] { "item1", "item2" };

        // Act
        var result = ValidatedServiceBase.ValidateCollectionNotEmpty(collection);

        // Assert
        Assert.Equal(collection, result);
    }

    [Fact]
    public void ValidateCollectionNotEmpty_WithNullCollection_ThrowsArgumentNullException()
    {
        // Arrange
        string[]? collection = null;

        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() => ValidatedServiceBase.ValidateCollectionNotEmpty(collection));
        Assert.Equal("collection", exception.ParamName);
    }

    [Fact]
    public void ValidateCollectionNotEmpty_WithEmptyCollection_ThrowsArgumentException()
    {
        // Arrange
        var collection = Array.Empty<string>();

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => ValidatedServiceBase.ValidateCollectionNotEmpty(collection));
        Assert.Equal("collection", exception.ParamName);
        Assert.Contains("cannot be empty", exception.Message);
    }

    [Fact]
    public void ValidateNotEmpty_WithValidString_ReturnsString()
    {
        // Arrange
        const string testValue = "test";

        // Act
        var result = ValidatedServiceBase.ValidateNotEmpty(testValue);

        // Assert
        Assert.Equal(testValue, result);
    }

    [Fact]
    public void ValidateNotEmpty_WithNullString_ThrowsArgumentNullException()
    {
        // Arrange
        string? testValue = null;

        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() => ValidatedServiceBase.ValidateNotEmpty(testValue));
        Assert.Equal("testValue", exception.ParamName);
    }

    [Fact]
    public void ValidateNotEmpty_WithEmptyString_ThrowsArgumentException()
    {
        // Arrange
        const string testValue = "";

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => ValidatedServiceBase.ValidateNotEmpty(testValue));
        Assert.Equal("testValue", exception.ParamName);
        Assert.Contains("cannot be empty or whitespace", exception.Message);
    }

    [Fact]
    public void ValidateNotEmpty_WithWhitespaceString_ThrowsArgumentException()
    {
        // Arrange
        const string testValue = "   ";

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => ValidatedServiceBase.ValidateNotEmpty(testValue));
        Assert.Equal("testValue", exception.ParamName);
        Assert.Contains("cannot be empty or whitespace", exception.Message);
    }

    [Fact]
    public void ValidationBuilder_FluentValidation_WorksCorrectly()
    {
        // Arrange
        const string str = "test";
        var obj = new object();
        var collection = new[] { 1, 2, 3 };

        // Act & Assert (should not throw)
        ValidatedServiceBase.Validate()
            .Required(str)
            .Required(obj)
            .CollectionNotEmpty(collection)
            .NotEmpty(str)
            .That(true, "Should not fail")
            .That(str.Length > 0, new InvalidOperationException("Custom exception"));
    }

    [Fact]
    public void ValidationBuilder_WithFailingCondition_ThrowsExpectedException()
    {
        // Arrange & Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ValidatedServiceBase.Validate()
                .That(false, new InvalidOperationException("Custom exception")));

        Assert.Equal("Custom exception", exception.Message);
    }

    [Fact]
    public void ValidationBuilder_WithFailingConditionAndMessage_ThrowsArgumentException()
    {
        // Arrange & Act & Assert
        var exception = Assert.Throws<ArgumentException>(() =>
            ValidatedServiceBase.Validate()
                .That(false, "Custom message", "paramName"));

        Assert.Equal("Custom message", exception.Message);
        Assert.Equal("paramName", exception.ParamName);
    }

    [Fact]
    public void TestService_WithValidParameters_ConstructsSuccessfully()
    {
        // Arrange
        const string param1 = "test";
        var param2 = new object();
        var options = Options.Create(new TestOptions { Value = "test" });

        // Act
        var service = new TestService(param1, param2, options);

        // Assert
        Assert.Equal(param1, service.TestProperty1);
        Assert.Equal(param2, service.TestProperty2);
        Assert.Equal("test", service.TestOptions.Value);
    }

    [Fact]
    public void TestService_WithNullParameter_ThrowsArgumentNullException()
    {
        // Arrange
        const string param1 = "test";
        object? param2 = null;
        var options = Options.Create(new TestOptions());

        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() => new TestService(param1, param2!, options));
        Assert.Equal("param2", exception.ParamName);
    }
}
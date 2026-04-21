// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Server.Features.Infrastructure.Configuration;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.Configuration;

/// <summary>
/// Tests for ConfigurationValidator - critical system configuration validation
/// </summary>
public sealed class ConfigurationValidatorTests
{
    private readonly IServiceProvider _mockServiceProvider;
    private readonly IConfiguration _mockConfiguration;
    private readonly ILogger<ConfigurationValidator> _mockLogger;
    private readonly ConfigurationValidator _validator;

    public ConfigurationValidatorTests()
    {
        _mockServiceProvider = Substitute.For<IServiceProvider>();
        _mockConfiguration = Substitute.For<IConfiguration>();
        _mockLogger = Substitute.For<ILogger<ConfigurationValidator>>();

        _validator = new ConfigurationValidator(
            _mockServiceProvider,
            _mockConfiguration,
            _mockLogger);
    }

    [Fact]
    [UnitTest]
    public void Constructor_NullServiceProvider_ThrowsArgumentNullException()
    {
        // Act & Assert
        var act = () => new ConfigurationValidator(null!, _mockConfiguration, _mockLogger);
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("serviceProvider");
    }

    [Fact]
    [UnitTest]
    public void Constructor_NullConfiguration_ThrowsArgumentNullException()
    {
        // Act & Assert
        var act = () => new ConfigurationValidator(_mockServiceProvider, null!, _mockLogger);
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("configuration");
    }

    [Fact]
    [UnitTest]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        // Act & Assert
        var act = () => new ConfigurationValidator(_mockServiceProvider, _mockConfiguration, null!);
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("logger");
    }

    [Fact]
    [UnitTest]
    public void ConfigurationSection_ReturnsEmptyString()
    {
        // Act & Assert
        _validator.ConfigurationSection.Should().BeEmpty();
    }

    [Fact]
    [UnitTest]
    public void ValidateConfiguration_ValidConfiguration_ReturnsNoErrors()
    {
        // Arrange
        var validConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SampleSection:Value"] = "ValidValue"
            })
            .Build();

        // Act
        var errors = _validator.ValidateConfiguration(validConfig).ToList();

        // Assert
        errors.Should().BeEmpty();
    }

    // Test model for validation testing
    private class TestConfigurationOptions
    {
        [Required]
        [StringLength(50, MinimumLength = 1)]
        public string? RequiredField { get; set; }

        [Range(1, 100)]
        public int NumericField { get; set; }

        [EmailAddress]
        public string? EmailField { get; set; }
    }

    [Fact]
    [UnitTest]
    public void RegisterOptionsValidation_ValidType_SuccessfullyRegisters()
    {
        // Act
        var result = _validator.RegisterOptionsValidation<TestConfigurationOptions>("TestSection");

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    [UnitTest]
    public void RegisterOptionsValidation_DuplicateType_ReturnsFalse()
    {
        // Arrange
        _validator.RegisterOptionsValidation<TestConfigurationOptions>("TestSection");

        // Act
        var result = _validator.RegisterOptionsValidation<TestConfigurationOptions>("TestSection");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    [UnitTest]
    public void GetAllOptions_EmptyRegistry_ReturnsEmptyCollection()
    {
        // Act
        var options = _validator.GetAllOptions().ToList();

        // Assert
        options.Should().BeEmpty();
    }

    [Fact]
    [UnitTest]
    public void GetAllOptions_WithRegisteredOptions_ReturnsMetadata()
    {
        // Arrange
        _validator.RegisterOptionsValidation<TestConfigurationOptions>("TestSection");

        // Act
        var options = _validator.GetAllOptions().ToList();

        // Assert
        options.Should().HaveCount(1);
        var metadata = options.First();
        metadata.OptionsType.Should().Be(typeof(TestConfigurationOptions));
        metadata.ConfigurationSection.Should().Be("TestSection");
        metadata.Properties.Should().NotBeEmpty();
    }

    [Fact]
    [UnitTest]
    public void GetAllOptions_IncludesPropertyMetadata()
    {
        // Arrange
        _validator.RegisterOptionsValidation<TestConfigurationOptions>("TestSection");

        // Act
        var options = _validator.GetAllOptions().ToList();

        // Assert
        var metadata = options.First();
        var properties = metadata.Properties.ToList();

        properties.Should().Contain(p => p.Name == nameof(TestConfigurationOptions.RequiredField));
        properties.Should().Contain(p => p.Name == nameof(TestConfigurationOptions.NumericField));
        properties.Should().Contain(p => p.Name == nameof(TestConfigurationOptions.EmailField));

        var requiredProperty = properties.First(p => p.Name == nameof(TestConfigurationOptions.RequiredField));
        requiredProperty.IsRequired.Should().BeTrue();
        requiredProperty.PropertyType.Should().Be(typeof(string));
    }

    [Fact]
    [IntegrationTest]
    public void ValidateConfiguration_WithInvalidValues_ReturnsValidationErrors()
    {
        // Arrange
        var services = new ServiceCollection();
        services.Configure<TestConfigurationOptions>("TestSection", options =>
        {
            options.RequiredField = null; // Required field is null
            options.NumericField = 101; // Out of range
            options.EmailField = "invalid-email"; // Invalid email format
        });

        services.AddSingleton<IConfiguration>(_mockConfiguration);
        services.AddLogging();

        var serviceProvider = services.BuildServiceProvider();

        var validator = new ConfigurationValidator(
            serviceProvider,
            _mockConfiguration,
            serviceProvider.GetRequiredService<ILogger<ConfigurationValidator>>());

        validator.RegisterOptionsValidation<TestConfigurationOptions>("TestSection");

        var invalidConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TestSection:RequiredField"] = "",
                ["TestSection:NumericField"] = "101",
                ["TestSection:EmailField"] = "invalid-email"
            })
            .Build();

        // Act
        var errors = validator.ValidateConfiguration(invalidConfig).ToList();

        // Assert - We should get validation errors
        // Note: Exact error messages depend on implementation details
        // This test verifies the validation mechanism works
        errors.Should().NotBeEmpty();
    }

    [Fact]
    [UnitTest]
    public void GetOptionsMetadata_UnregisteredType_ReturnsNull()
    {
        // Act
        var metadata = _validator.GetOptionsMetadata<TestConfigurationOptions>();

        // Assert
        metadata.Should().BeNull();
    }

    [Fact]
    [UnitTest]
    public void GetOptionsMetadata_RegisteredType_ReturnsMetadata()
    {
        // Arrange
        _validator.RegisterOptionsValidation<TestConfigurationOptions>("TestSection");

        // Act
        var metadata = _validator.GetOptionsMetadata<TestConfigurationOptions>();

        // Assert
        metadata.Should().NotBeNull();
        metadata!.OptionsType.Should().Be(typeof(TestConfigurationOptions));
        metadata.ConfigurationSection.Should().Be("TestSection");
    }
}
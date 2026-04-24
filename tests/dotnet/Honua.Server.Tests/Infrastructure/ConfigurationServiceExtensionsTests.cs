// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Configuration;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Features.Infrastructure.Configuration;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Tests.Infrastructure;

[Protocol(TestProtocols.TestQuality)]
public sealed class ConfigurationServiceExtensionsTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void AddStandardConfiguration_DoesNotRegisterSecretResolutionForSecretProviderOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddLogging();

        services.AddStandardConfiguration(configuration);

        services.Should().NotContain(static descriptor =>
            descriptor.ServiceType == typeof(IPostConfigureOptions<SecretProviderOptions>));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        provider.GetRequiredService<IConnectionSecretResolver>()
            .GetSupportedProviders()
            .Should()
            .Contain(["env", "aws", "azure", "null"]);
        provider.GetRequiredService<IOptions<SecretProviderOptions>>().Value.Should().NotBeNull();
        provider.GetRequiredService<IOptions<SecureConfigurationOptions>>().Value.Should().NotBeNull();
        provider.GetRequiredService<ISecretProvider>().Should().NotBeNull();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ConfigureWithValidation_BindsRegisteredOptions_AndValidatesSuccessfully()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{TestValidationOptions.SectionName}:{nameof(TestValidationOptions.RequiredValue)}"] = "configured"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddSingleton(Substitute.For<ISecretProvider>());
        services.AddLogging();
        services.AddConfigurationValidation(configuration);
        services.ConfigureWithValidation<TestValidationOptions>(configuration, TestValidationOptions.SectionName);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        var options = provider.GetRequiredService<IOptions<TestValidationOptions>>().Value;
        options.RequiredValue.Should().Be("configured");

        var validator = provider.GetRequiredService<IConfigurationValidator>();
        var summary = await validator.ValidateAllAsync().ConfigureAwait(false);

        summary.IsValid.Should().BeTrue();
        summary.Results.Should().ContainSingle(result => result.SectionName == TestValidationOptions.SectionName);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ConfigureWithValidation_ResolvesEnvironmentSecretReferencesSynchronously()
    {
        const string envKey = "HONUA_TEST_REQUIRED_VALUE";
        var previousValue = Environment.GetEnvironmentVariable(envKey);
        Environment.SetEnvironmentVariable(envKey, "configured-from-env");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{TestValidationOptions.SectionName}:{nameof(TestValidationOptions.RequiredValue)}"] = $"env:{envKey}"
                })
                .Build();
            var services = new ServiceCollection();

            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
            services.AddSingleton(Substitute.For<ISecretProvider>());
            services.AddLogging();
            services.AddConfigurationValidation(configuration);
            services.ConfigureWithValidation<TestValidationOptions>(configuration, TestValidationOptions.SectionName);

            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });

            provider.GetRequiredService<IOptions<TestValidationOptions>>().Value.RequiredValue
                .Should().Be("configured-from-env");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, previousValue);
        }
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void ConfigureWithValidation_RejectsNonEnvironmentSecretReferencesForStartupBoundOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{TestValidationOptions.SectionName}:{nameof(TestValidationOptions.RequiredValue)}"] = "aws:secretsmanager:test-secret"
            })
            .Build();
        var secretProvider = Substitute.For<ISecretProvider>();
        secretProvider.IsSecretReference("aws:secretsmanager:test-secret").Returns(true);
        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddSingleton(secretProvider);
        services.AddLogging();
        services.AddConfigurationValidation(configuration);
        services.ConfigureWithValidation<TestValidationOptions>(configuration, TestValidationOptions.SectionName);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        var options = provider.GetRequiredService<IOptions<TestValidationOptions>>();
        options.Invoking(static value => _ = value.Value)
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*only env:*startup-bound option binding*");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ValidateAllAsync_WithMissingRequiredOption_ReportsBindingErrors()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddSingleton(Substitute.For<ISecretProvider>());
        services.AddLogging();
        services.AddConfigurationValidation(configuration);
        services.ConfigureWithValidation<TestValidationOptions>(configuration, TestValidationOptions.SectionName);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        var validator = provider.GetRequiredService<IConfigurationValidator>();
        var summary = await validator.ValidateAllAsync().ConfigureAwait(false);

        summary.IsValid.Should().BeFalse();
        summary.Results.Should().ContainSingle(result => result.SectionName == TestValidationOptions.SectionName);
        summary.AllErrors.Should().Contain(error =>
            error.Contains(nameof(TestValidationOptions.RequiredValue), StringComparison.Ordinal));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ValidateAllAsync_DoesNotRequireOptionsResolutionToRegisterConfiguredType()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{TestValidationOptions.SectionName}:{nameof(TestValidationOptions.RequiredValue)}"] = "configured"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddSingleton(Substitute.For<ISecretProvider>());
        services.AddLogging();
        services.AddConfigurationValidation(configuration);
        services.ConfigureWithValidation<TestValidationOptions>(configuration, TestValidationOptions.SectionName);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        var validator = provider.GetRequiredService<IConfigurationValidator>();
        var summary = await validator.ValidateAllAsync().ConfigureAwait(false);

        summary.IsValid.Should().BeTrue();
        summary.Results.Should().ContainSingle(result =>
            result.SectionName == TestValidationOptions.SectionName &&
            result.OptionsTypeName == nameof(TestValidationOptions));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task RegisterOptionsType_WithoutOptionsService_BindsFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{TestValidationOptions.SectionName}:{nameof(TestValidationOptions.RequiredValue)}"] = "configured"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddLogging();
        services.AddConfigurationValidation(configuration);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        var validator = provider.GetRequiredService<IConfigurationValidator>();
        validator.RegisterOptionsType<TestValidationOptions>(TestValidationOptions.SectionName);

        var summary = await validator.ValidateAllAsync().ConfigureAwait(false);

        summary.IsValid.Should().BeTrue();
        summary.Results.Should().ContainSingle(result =>
            result.SectionName == TestValidationOptions.SectionName &&
            result.OptionsTypeName == nameof(TestValidationOptions));
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "Honua.Server.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TestValidationOptions
    {
        public const string SectionName = "TestValidation";

        [Required]
        public string? RequiredValue { get; set; }
    }
}

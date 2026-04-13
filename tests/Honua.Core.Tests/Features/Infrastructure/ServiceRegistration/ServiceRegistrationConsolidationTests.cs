// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.ServiceRegistration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Core.Tests.Features.Infrastructure.ServiceRegistration;

/// <summary>
/// Tests for the service registration consolidation framework.
/// Validates that the consolidated patterns work correctly and eliminate duplication.
/// </summary>
public class ServiceRegistrationConsolidationTests
{
    [Fact]
    public void ServiceRegistrationHelpers_AddScopedService_RegistersCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddScopedService<ITestService, TestService>();

        // Assert
        var serviceProvider = services.BuildServiceProvider();
        var service = serviceProvider.GetService<ITestService>();

        Assert.NotNull(service);
        Assert.IsType<TestService>(service);
    }

    [Fact]
    public void ServiceRegistrationHelpers_AddScopedService_WithFactory_RegistersCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddScopedService<ITestService>(provider => new TestService());

        // Assert
        var serviceProvider = services.BuildServiceProvider();
        var service = serviceProvider.GetService<ITestService>();

        Assert.NotNull(service);
        Assert.IsType<TestService>(service);
    }

    [Fact]
    public void ServiceRegistrationHelpers_AddSingletonService_RegistersCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddSingletonService<ITestService, TestService>();

        // Assert
        var serviceProvider = services.BuildServiceProvider();
        var service1 = serviceProvider.GetService<ITestService>();
        var service2 = serviceProvider.GetService<ITestService>();

        Assert.NotNull(service1);
        Assert.NotNull(service2);
        Assert.Same(service1, service2); // Should be same instance for singleton
    }

    [Fact]
    public void ServiceRegistrationHelpers_AddConfigurationOptions_WithValidator_RegistersCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "Test:Value", "TestValue" },
                { "Test:Number", "42" }
            })
            .Build();

        var configSection = configuration.GetSection("Test");

        // Act
        services.AddConfigurationOptions<TestOptions, TestOptionsValidator>(configSection);

        // Assert
        var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetService<IOptions<TestOptions>>();

        Assert.NotNull(options);
        Assert.Equal("TestValue", options.Value.Value);
        Assert.Equal(42, options.Value.Number);
    }

    [Fact]
    public void ServiceRegistrationHelpers_AddProviderRegistry_RegistersCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddProviderRegistry<ITestProvider, TestProviderOptions>();

        // Assert
        var serviceProvider = services.BuildServiceProvider();
        var registry = serviceProvider.GetService<IProviderRegistry<ITestProvider>>();

        Assert.NotNull(registry);
    }

    [Fact]
    public void ServiceRegistrationHelpers_AddSegregatedInterfaces_RegistersAllInterfaces()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddSegregatedInterfaces<TestImplementation>(
            typeof(ITestService),
            typeof(ITestProvider));

        // Assert
        var serviceProvider = services.BuildServiceProvider();
        var service = serviceProvider.GetService<ITestService>();
        var provider = serviceProvider.GetService<ITestProvider>();
        var implementation = serviceProvider.GetService<TestImplementation>();

        Assert.NotNull(service);
        Assert.NotNull(provider);
        Assert.NotNull(implementation);

        // All should point to the same instance
        Assert.Same(implementation, service);
        Assert.Same(implementation, provider);
    }

    [Fact]
    public void ServiceRegistrationHelpers_AddReadOnlyImplementations_RegistersCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddReadOnlyImplementations(
            (typeof(ITestService), typeof(ReadOnlyTestService)));

        // Assert
        var serviceProvider = services.BuildServiceProvider();
        var service = serviceProvider.GetService<ITestService>();

        Assert.NotNull(service);
        Assert.IsType<ReadOnlyTestService>(service);
    }

    [Fact]
    public void ServiceRegistrationPatterns_AddSimpleCoreFeature_RegistersCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddSimpleCoreFeature<ITestService, TestService>();

        // Assert
        var serviceProvider = services.BuildServiceProvider();
        var service = serviceProvider.GetService<ITestService>();

        Assert.NotNull(service);
        Assert.IsType<TestService>(service);
    }

    [Fact]
    public void ServiceRegistrationPatterns_AddPerformanceOptimizedObjectPools_RegistersCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddPerformanceOptimizedObjectPools();

        // Assert
        var serviceProvider = services.BuildServiceProvider();
        var stringBuilderPool = serviceProvider.GetService<Microsoft.Extensions.ObjectPool.ObjectPool<System.Text.StringBuilder>>();
        var dictionaryPool = serviceProvider.GetService<Microsoft.Extensions.ObjectPool.ObjectPool<Dictionary<string, object?>>>();

        Assert.NotNull(stringBuilderPool);
        Assert.NotNull(dictionaryPool);
    }

    [Fact]
    public void DefaultProviderRegistry_RegisterAndRetrieveProvider_WorksCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();
        var options = Options.Create(new TestProviderOptions());
        var serviceProvider = services.BuildServiceProvider();
        var registry = new DefaultProviderRegistry<ITestProvider, TestProviderOptions>(serviceProvider, options);

        // Act
        registry.RegisterProvider("test", sp => new TestProvider());
        var provider = registry.GetProvider("test");

        // Assert
        Assert.NotNull(provider);
        Assert.IsType<TestProvider>(provider);
        Assert.True(registry.IsProviderRegistered("test"));
        Assert.Contains("test", registry.GetProviderNames());
    }

    [Fact]
    public void DefaultProviderRegistry_UnregisterProvider_WorksCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();
        var options = Options.Create(new TestProviderOptions());
        var serviceProvider = services.BuildServiceProvider();
        var registry = new DefaultProviderRegistry<ITestProvider, TestProviderOptions>(serviceProvider, options);

        registry.RegisterProvider("test", sp => new TestProvider());

        // Act
        var removed = registry.UnregisterProvider("test");
        var provider = registry.GetProvider("test");

        // Assert
        Assert.True(removed);
        Assert.Null(provider);
        Assert.False(registry.IsProviderRegistered("test"));
        Assert.DoesNotContain("test", registry.GetProviderNames());
    }

    // Test interfaces and implementations
    public interface ITestService { }
    public interface ITestProvider { }

    public class TestService : ITestService { }
    public class TestProvider : ITestProvider { }
    public class ReadOnlyTestService : ITestService { }

    public class TestImplementation : ITestService, ITestProvider { }

    public class TestOptions
    {
        public string Value { get; set; } = string.Empty;
        public int Number { get; set; }
    }

    public class TestOptionsValidator : IValidateOptions<TestOptions>
    {
        public ValidateOptionsResult Validate(string? name, TestOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.Value))
            {
                return ValidateOptionsResult.Fail("Value cannot be empty");
            }

            return ValidateOptionsResult.Success;
        }
    }

    public class TestProviderOptions
    {
        public Dictionary<string, Func<IServiceProvider, ITestProvider>> Providers { get; } = new();
    }
}
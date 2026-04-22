// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.ServiceRegistration;
using Microsoft.Extensions.DependencyInjection;

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

    // Test interfaces and implementations
    public interface ITestService { }

    public class TestService : ITestService { }
}

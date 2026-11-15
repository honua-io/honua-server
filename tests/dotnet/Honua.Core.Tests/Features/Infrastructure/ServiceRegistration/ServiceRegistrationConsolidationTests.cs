// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.ServiceRegistration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Core.Tests.Features.Infrastructure.ServiceRegistration;

/// <summary>
/// Tests for the service registration consolidation framework.
/// Validates that the consolidated patterns work correctly and eliminate duplication.
/// </summary>
public class ServiceRegistrationConsolidationTests
{
    [Trait("Tier", "Fast")]
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

    [Trait("Tier", "Fast")]
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

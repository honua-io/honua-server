// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Validation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Honua.Core.Tests.Features.Infrastructure.Validation;

public class ServiceValidationHelpersTests
{
    [Fact]
    public void ValidateServiceDependencies_WithValidParameters_ReturnsValidatedTuple()
    {
        // Arrange
        var connectionProvider = new TestConnectionProvider();
        var logger = new TestLogger();

        // Act
        var result = ServiceValidationHelpers.ValidateServiceDependencies(connectionProvider, logger);

        // Assert
        Assert.Equal(connectionProvider, result.Item1);
        Assert.Equal(logger, result.Item2);
    }

    [Fact]
    public void ValidateServiceDependencies_WithNullConnectionProvider_ThrowsArgumentNullException()
    {
        // Arrange
        TestConnectionProvider? connectionProvider = null;
        var logger = new TestLogger();

        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() =>
            ServiceValidationHelpers.ValidateServiceDependencies(connectionProvider, logger));
        Assert.Equal("connectionProvider", exception.ParamName);
    }

    [Fact]
    public void ValidateServiceDependencies_WithNullLogger_ThrowsArgumentNullException()
    {
        // Arrange
        var connectionProvider = new TestConnectionProvider();
        TestLogger? logger = null;

        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() =>
            ServiceValidationHelpers.ValidateServiceDependencies(connectionProvider, logger));
        Assert.Equal("logger", exception.ParamName);
    }

    [Fact]
    public void ValidateServiceDependencies_WithOptions_ValidParameters_ReturnsValidatedTuple()
    {
        // Arrange
        var connectionProvider = new TestConnectionProvider();
        var logger = new TestServiceLogger();
        var options = Options.Create(new TestOptions { Value = "test" });

        // Act
        var result = ServiceValidationHelpers.ValidateServiceDependencies<TestService, TestOptions>(
            connectionProvider, logger, options);

        // Assert
        Assert.Equal(connectionProvider, result.Item1);
        Assert.Equal(logger, result.Item2);
        Assert.Equal("test", result.Item3.Value);
    }

    [Fact]
    public void ValidateCacheDecoratorDependencies_WithValidParameters_ReturnsValidatedTuple()
    {
        // Arrange
        var innerService = new TestInnerService();
        var cacheService = new TestCacheService();
        var options = Options.Create(new TestOptions { Value = "test" });

        // Act
        var result = ServiceValidationHelpers.ValidateCacheDecoratorDependencies(
            innerService, cacheService, options);

        // Assert
        Assert.Equal(innerService, result.Item1);
        Assert.Equal(cacheService, result.Item2);
        Assert.Equal("test", result.Item3.Value);
    }

    [Fact]
    public void ValidateCacheDecoratorDependencies_WithNullInnerService_ThrowsArgumentNullException()
    {
        // Arrange
        TestInnerService? innerService = null;
        var cacheService = new TestCacheService();
        var options = Options.Create(new TestOptions());

        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() =>
            ServiceValidationHelpers.ValidateCacheDecoratorDependencies(innerService, cacheService, options));
        Assert.Equal("innerService", exception.ParamName);
    }

    [Fact]
    public void ValidateHandlerDependencies_WithValidParameters_ReturnsValidatedTuple()
    {
        // Arrange
        var dependency1 = new TestDependency1();
        var dependency2 = new TestDependency2();
        var dependency3 = new TestDependency3();
        var logger = new TestHandlerLogger();

        // Act
        var result = ServiceValidationHelpers.ValidateHandlerDependencies<
            TestDependency1, TestDependency2, TestDependency3, TestHandler>(
            dependency1, dependency2, dependency3, logger);

        // Assert
        Assert.Equal(dependency1, result.Item1);
        Assert.Equal(dependency2, result.Item2);
        Assert.Equal(dependency3, result.Item3);
        Assert.Equal(logger, result.Item4);
    }

    [Fact]
    public void ValidateBackgroundServiceDependencies_WithValidParameters_ReturnsValidatedTuple()
    {
        // Arrange
        var service = new TestBackgroundDependency();
        var logger = new TestBackgroundServiceLogger();
        var options = Options.Create(new TestOptions { Value = "test" });

        // Act
        var result = ServiceValidationHelpers.ValidateBackgroundServiceDependencies<
            TestBackgroundDependency, TestBackgroundService, TestOptions>(
            service, logger, options);

        // Assert
        Assert.Equal(service, result.Item1);
        Assert.Equal(logger, result.Item2);
        Assert.Equal("test", result.Item3.Value);
    }

    [Fact]
    public void ValidateRepositoryDependencies_WithValidParameters_ReturnsValidatedTuple()
    {
        // Arrange
        var connectionProvider = new TestConnectionProvider();
        var registry = new TestRegistry();
        var logger = new TestRepositoryLogger();

        // Act
        var result = ServiceValidationHelpers.ValidateRepositoryDependencies<TestRegistry, TestRepository>(
            connectionProvider, registry, logger);

        // Assert
        Assert.Equal(connectionProvider, result.Item1);
        Assert.Equal(registry, result.Item2);
        Assert.Equal(logger, result.Item3);
    }

    [Fact]
    public void ValidateComplexServiceDependencies_WithValidParameters_ReturnsValidatedTuple()
    {
        // Arrange
        var param1 = new TestDependency1();
        var param2 = new TestDependency2();
        var param3 = new TestDependency3();
        var param4 = new TestDependency4();
        var param5 = new TestDependency5();
        var param6 = new TestDependency6();

        // Act
        var result = ServiceValidationHelpers.ValidateComplexServiceDependencies(
            param1, param2, param3, param4, param5, param6);

        // Assert
        Assert.Equal(param1, result.Item1);
        Assert.Equal(param2, result.Item2);
        Assert.Equal(param3, result.Item3);
        Assert.Equal(param4, result.Item4);
        Assert.Equal(param5, result.Item5);
        Assert.Equal(param6, result.Item6);
    }

    [Fact]
    public void ValidateEventServiceDependencies_WithValidParameters_ReturnsValidatedTuple()
    {
        // Arrange
        var publisher = new TestEventPublisher();
        var logger = new TestEventServiceLogger();

        // Act
        var result = ServiceValidationHelpers.ValidateEventServiceDependencies<
            TestEventPublisher, TestEventService>(publisher, logger);

        // Assert
        Assert.Equal(publisher, result.Item1);
        Assert.Equal(logger, result.Item2);
    }

    [Fact]
    public void ValidateEventServiceDependencies_WithNullPublisher_ThrowsArgumentNullException()
    {
        // Arrange
        TestEventPublisher? publisher = null;
        var logger = new TestEventServiceLogger();

        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() =>
            ServiceValidationHelpers.ValidateEventServiceDependencies<TestEventPublisher, TestEventService>(
                publisher, logger));
        Assert.Equal("publisher", exception.ParamName);
    }

    // Test classes for dependency validation
    private sealed class TestConnectionProvider : IDatabaseConnectionProvider
    {
        public Task<System.Data.Common.DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<(System.Data.Common.DbConnection Connection, System.Data.Common.DbTransaction Transaction)> OpenTransactionAsync(
            System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    private sealed class TestLogger : ILogger<TestConnectionProvider>;
    private sealed class TestServiceLogger : ILogger<TestService>;
    private sealed class TestHandlerLogger : ILogger<TestHandler>;
    private sealed class TestBackgroundServiceLogger : ILogger<TestBackgroundService>;
    private sealed class TestRepositoryLogger : ILogger<TestRepository>;
    private sealed class TestEventServiceLogger : ILogger<TestEventService>;

    private sealed class TestOptions
    {
        public string Value { get; set; } = "default";
    }

    private sealed class TestService;
    private sealed class TestHandler;
    private sealed class TestBackgroundService;
    private sealed class TestRepository;
    private sealed class TestEventService;

    private sealed class TestInnerService;
    private sealed class TestCacheService;
    private sealed class TestRegistry;
    private sealed class TestBackgroundDependency;
    private sealed class TestEventPublisher;

    private sealed class TestDependency1;
    private sealed class TestDependency2;
    private sealed class TestDependency3;
    private sealed class TestDependency4;
    private sealed class TestDependency5;
    private sealed class TestDependency6;
}
// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Caching.Abstractions;
using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Server.Features.HealthCheck;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Tests.Infrastructure;

/// <summary>
/// Tests for ReadinessCheckService - validates separated health check orchestration logic
/// </summary>
[Protocol(TestProtocols.Health)]
public sealed class ReadinessCheckServiceTests
{
    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public async Task CheckReadinessAsync_WithHealthyDatabase_ReturnsReady()
    {
        // Arrange
        var mockDatabaseChecker = new MockHealthyDatabaseChecker();
        var service = CreateService(mockDatabaseChecker);

        // Act
        var result = await service.CheckReadinessAsync();

        // Assert
        result.IsReady.Should().BeTrue();
        result.StatusCode.Should().Be(200);
        result.Message.Should().Be("Ready");
        result.Exception.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public async Task CheckReadinessAsync_LogsMeasuredDatabaseElapsedTime()
    {
        var logger = new MockLogger<ReadinessCheckService>();
        var migrationState = new MigrationState();
        migrationState.MarkSucceeded();
        var service = new ReadinessCheckService(
            new DelayedHealthyDatabaseChecker(TimeSpan.FromMilliseconds(25)),
            migrationState,
            logger);

        var result = await service.CheckReadinessAsync();

        result.IsReady.Should().BeTrue();
        logger.LogCalls.Should().ContainSingle(call =>
            call.Message.Contains("DatabaseHealth", StringComparison.Ordinal)
            && !call.Message.Contains("in 0.00ms", StringComparison.Ordinal));
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public async Task CheckReadinessAsync_WithUnhealthyDatabase_ReturnsNotReady()
    {
        // Arrange
        var mockDatabaseChecker = new MockUnhealthyDatabaseChecker();
        var service = CreateService(mockDatabaseChecker);

        // Act
        var result = await service.CheckReadinessAsync();

        // Assert
        result.IsReady.Should().BeFalse();
        result.StatusCode.Should().Be(503);
        result.Message.Should().Be("Not Ready - Database unavailable");
        result.Exception.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public async Task CheckReadinessAsync_WithMigrationsRunning_ReturnsNotReady()
    {
        // Arrange
        var mockDatabaseChecker = new MockHealthyDatabaseChecker();
        var migrationState = new MigrationState();
        migrationState.MarkRunning();
        var service = CreateService(mockDatabaseChecker, migrationState: migrationState);

        // Act
        var result = await service.CheckReadinessAsync();

        // Assert
        result.IsReady.Should().BeFalse();
        result.StatusCode.Should().Be(503);
        result.Message.Should().Be("Not Ready - Database migrations in progress");
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public async Task CheckReadinessAsync_WithCacheHealthChecker_IncludesCacheStatus()
    {
        // Arrange
        var mockDatabaseChecker = new MockHealthyDatabaseChecker();
        var mockCacheChecker = new MockCacheHealthChecker(healthy: true, usingFallback: false);
        var service = CreateService(mockDatabaseChecker, mockCacheChecker);

        // Act
        var result = await service.CheckReadinessAsync();

        // Assert
        result.IsReady.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public async Task CheckReadinessAsync_WithCacheInFallbackMode_StillReturnsReady()
    {
        // Arrange
        var mockDatabaseChecker = new MockHealthyDatabaseChecker();
        var mockCacheChecker = new MockCacheHealthChecker(healthy: true, usingFallback: true);
        var service = CreateService(mockDatabaseChecker, mockCacheChecker);

        // Act
        var result = await service.CheckReadinessAsync();

        // Assert
        result.IsReady.Should().BeTrue(); // Cache in fallback doesn't affect readiness
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public async Task CheckReadinessAsync_WithAvailableFeatureChangeStore_ReturnsReady()
    {
        var mockDatabaseChecker = new MockHealthyDatabaseChecker();
        var featureChangeStoreHealth = new MockFeatureChangeEventStoreHealth(canPersistEvents: true);
        var service = CreateService(mockDatabaseChecker, featureChangeEventStoreHealth: featureChangeStoreHealth);

        var result = await service.CheckReadinessAsync();

        result.IsReady.Should().BeTrue();
        result.StatusCode.Should().Be(200);
        result.Message.Should().Be("Ready");
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public async Task CheckReadinessAsync_WithUnhealthyCache_ReturnsNotReady()
    {
        // Arrange
        var mockDatabaseChecker = new MockHealthyDatabaseChecker();
        var mockCacheChecker = new MockCacheHealthChecker(healthy: false, usingFallback: false);
        var service = CreateService(mockDatabaseChecker, mockCacheChecker);

        // Act
        var result = await service.CheckReadinessAsync();

        // Assert
        result.IsReady.Should().BeFalse();
        result.StatusCode.Should().Be(503);
        result.Message.Should().Be("Not Ready - Cache unavailable");
        result.Exception.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public async Task CheckReadinessAsync_WithUnavailableFeatureChangeStore_ReturnsNotReady()
    {
        var mockDatabaseChecker = new MockHealthyDatabaseChecker();
        var featureChangeStoreHealth = new MockFeatureChangeEventStoreHealth(canPersistEvents: false);
        var service = CreateService(mockDatabaseChecker, featureChangeEventStoreHealth: featureChangeStoreHealth);

        var result = await service.CheckReadinessAsync();

        result.IsReady.Should().BeFalse();
        result.StatusCode.Should().Be(503);
        result.Message.Should().Be("Not Ready - Feature-change event storage unavailable");
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public async Task CheckReadinessAsync_WithUnavailableFeatureChangeStore_LogsMeasuredElapsedTime()
    {
        var logger = new MockLogger<ReadinessCheckService>();
        var migrationState = new MigrationState();
        migrationState.MarkSucceeded();
        var service = new ReadinessCheckService(
            new MockHealthyDatabaseChecker(),
            migrationState,
            logger,
            featureChangeEventStoreHealth: new DelayedFeatureChangeEventStoreHealth(false, TimeSpan.FromMilliseconds(25)));

        var result = await service.CheckReadinessAsync();

        result.IsReady.Should().BeFalse();
        logger.LogCalls.Should().ContainSingle(call =>
            call.Message.Contains("FeatureChangeEventStore", StringComparison.Ordinal)
            && !call.Message.Contains("in 0.00ms", StringComparison.Ordinal));
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public async Task CheckReadinessAsync_WithDatabaseException_ReturnsNotReadyWithException()
    {
        // Arrange
        var mockDatabaseChecker = new MockExceptionDatabaseChecker();
        var service = CreateService(mockDatabaseChecker);

        // Act
        var result = await service.CheckReadinessAsync();

        // Assert
        result.IsReady.Should().BeFalse();
        result.StatusCode.Should().Be(503);
        result.Message.Should().Be("Not Ready - Database health check failed");
        result.Exception.Should().NotBeNull();
        result.Exception.Should().BeOfType<InvalidOperationException>();
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public async Task CheckReadinessAsync_WithCacheException_ReturnsContextualFailure()
    {
        var mockDatabaseChecker = new MockHealthyDatabaseChecker();
        var throwingCacheChecker = new ThrowingCacheHealthChecker();
        var service = CreateService(mockDatabaseChecker, throwingCacheChecker);

        var result = await service.CheckReadinessAsync();

        result.IsReady.Should().BeFalse();
        result.StatusCode.Should().Be(503);
        result.Message.Should().Be("Not Ready - Cache health check failed");
        result.Exception.Should().BeOfType<InvalidOperationException>();
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public async Task CheckReadinessAsync_WithCancellation_PropagatesCancellation()
    {
        // Arrange
        var mockDatabaseChecker = new MockCancellationDatabaseChecker();
        var service = CreateService(mockDatabaseChecker);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await FluentActions.Invoking(async () =>
            await service.CheckReadinessAsync(cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public void ReadinessResult_Ready_CreatesCorrectResult()
    {
        // Act
        var result = ReadinessResult.Ready();

        // Assert
        result.IsReady.Should().BeTrue();
        result.StatusCode.Should().Be(200);
        result.Message.Should().Be("Ready");
        result.Exception.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public void ReadinessResult_NotReady_CreatesCorrectResult()
    {
        // Arrange
        var exception = new InvalidOperationException("Test error");

        // Act
        var result = ReadinessResult.NotReady("Database unavailable", exception);

        // Assert
        result.IsReady.Should().BeFalse();
        result.StatusCode.Should().Be(503);
        result.Message.Should().Be("Not Ready - Database unavailable");
        result.Exception.Should().Be(exception);
    }

    [UnitTest]
    [Operation(Operations.HealthCheck)]
    public void ReadinessResult_NotReadyWithoutException_CreatesCorrectResult()
    {
        // Act
        var result = ReadinessResult.NotReady("Service unavailable");

        // Assert
        result.IsReady.Should().BeFalse();
        result.StatusCode.Should().Be(503);
        result.Message.Should().Be("Not Ready - Service unavailable");
        result.Exception.Should().BeNull();
    }

    private static ReadinessCheckService CreateService(
        IDatabaseHealthChecker databaseChecker,
        ICacheHealthChecker? cacheChecker = null,
        MigrationState? migrationState = null,
        IFeatureChangeEventStoreHealth? featureChangeEventStoreHealth = null)
    {
        var state = migrationState ?? new MigrationState();
        if (migrationState is null)
        {
            state.MarkSucceeded();
        }

        return new ReadinessCheckService(
            databaseChecker,
            state,
            new MockLogger<ReadinessCheckService>(),
            cacheChecker,
            featureChangeEventStoreHealth);
    }
}

/// <summary>
/// Mock database health checker that always returns healthy
/// </summary>
internal sealed class MockHealthyDatabaseChecker : IDatabaseHealthChecker
{
    public Task<bool> IsDatabaseHealthyAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true);
    }
}

internal sealed class DelayedHealthyDatabaseChecker(TimeSpan delay) : IDatabaseHealthChecker
{
    public async Task<bool> IsDatabaseHealthyAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(delay, cancellationToken);
        return true;
    }
}

/// <summary>
/// Mock database health checker that always returns unhealthy
/// </summary>
internal sealed class MockUnhealthyDatabaseChecker : IDatabaseHealthChecker
{
    public Task<bool> IsDatabaseHealthyAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }
}

/// <summary>
/// Mock database health checker that throws an exception
/// </summary>
internal sealed class MockExceptionDatabaseChecker : IDatabaseHealthChecker
{
    public Task<bool> IsDatabaseHealthyAsync(CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Database connection failed");
    }
}

/// <summary>
/// Mock database health checker that respects cancellation tokens
/// </summary>
internal sealed class MockCancellationDatabaseChecker : IDatabaseHealthChecker
{
    public Task<bool> IsDatabaseHealthyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(true);
    }
}

internal sealed class MockFeatureChangeEventStoreHealth(bool canPersistEvents) : IFeatureChangeEventStoreHealth
{
    public bool CanPersistEvents { get; } = canPersistEvents;
}

internal sealed class ThrowingCacheHealthChecker : ICacheHealthChecker
{
    public bool IsUsingFallback => false;

    public Task<bool> IsCacheHealthyAsync(CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Cache connection failed");
}

internal sealed class DelayedFeatureChangeEventStoreHealth(bool canPersistEvents, TimeSpan delay) : IFeatureChangeEventStoreHealth
{
    public bool CanPersistEvents
    {
        get
        {
            Thread.Sleep(delay);
            return canPersistEvents;
        }
    }
}

/// <summary>
/// Simple mock logger for testing
/// </summary>
/// <typeparam name="T">Category type</typeparam>
internal sealed class MockLogger<T> : ILogger<T>
{
    public List<LogCall> LogCalls { get; } = [];

    public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
        new NullScope();

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        LogCalls.Add(new LogCall(logLevel, eventId, formatter(state, exception), exception));
    }

    private sealed class NullScope : IDisposable
    {
        public void Dispose() { }
    }
}

/// <summary>
/// Represents a captured log call
/// </summary>
/// <param name="LogLevel">The log level</param>
/// <param name="EventId">The event ID</param>
/// <param name="Message">The formatted message</param>
/// <param name="Exception">The exception, if any</param>
internal sealed record LogCall(
    LogLevel LogLevel,
    EventId EventId,
    string Message,
    Exception? Exception);

/// <summary>
/// Mock cache health checker for testing
/// </summary>
internal sealed class MockCacheHealthChecker : ICacheHealthChecker
{
    private readonly bool _healthy;
    private readonly bool _usingFallback;

    public MockCacheHealthChecker(bool healthy, bool usingFallback)
    {
        _healthy = healthy;
        _usingFallback = usingFallback;
    }

    public bool IsUsingFallback => _usingFallback;

    public Task<bool> IsCacheHealthyAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_healthy);
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.HealthCheck.Abstractions;
using Honua.Server.Features.HealthCheck;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Tests.Infrastructure;

/// <summary>
/// Tests for ReadinessCheckService - validates separated health check orchestration logic
/// </summary>
[Protocol("Infrastructure")]
public sealed class ReadinessCheckServiceTests
{
    [UnitTest]
    [Operation("HealthCheck")]
    public async Task CheckReadinessAsync_WithHealthyDatabase_ReturnsReady()
    {
        // Arrange
        var mockDatabaseChecker = new MockHealthyDatabaseChecker();
        var mockLogger = new MockLogger<ReadinessCheckService>();
        var service = new ReadinessCheckService(mockDatabaseChecker, mockLogger);

        // Act
        var result = await service.CheckReadinessAsync();

        // Assert
        result.IsReady.Should().BeTrue();
        result.StatusCode.Should().Be(200);
        result.Message.Should().Be("Ready");
        result.Exception.Should().BeNull();
    }

    [UnitTest]
    [Operation("HealthCheck")]
    public async Task CheckReadinessAsync_WithUnhealthyDatabase_ReturnsNotReady()
    {
        // Arrange
        var mockDatabaseChecker = new MockUnhealthyDatabaseChecker();
        var mockLogger = new MockLogger<ReadinessCheckService>();
        var service = new ReadinessCheckService(mockDatabaseChecker, mockLogger);

        // Act
        var result = await service.CheckReadinessAsync();

        // Assert
        result.IsReady.Should().BeFalse();
        result.StatusCode.Should().Be(503);
        result.Message.Should().Be("Not Ready - Database unavailable");
        result.Exception.Should().BeNull();
    }

    [UnitTest]
    [Operation("HealthCheck")]
    public async Task CheckReadinessAsync_WithDatabaseException_ReturnsNotReadyWithException()
    {
        // Arrange
        var mockDatabaseChecker = new MockExceptionDatabaseChecker();
        var mockLogger = new MockLogger<ReadinessCheckService>();
        var service = new ReadinessCheckService(mockDatabaseChecker, mockLogger);

        // Act
        var result = await service.CheckReadinessAsync();

        // Assert
        result.IsReady.Should().BeFalse();
        result.StatusCode.Should().Be(503);
        result.Message.Should().Be("Not Ready - Database unavailable");
        result.Exception.Should().NotBeNull();
        result.Exception.Should().BeOfType<InvalidOperationException>();
    }

    [UnitTest]
    [Operation("HealthCheck")]
    public async Task CheckReadinessAsync_WithCancellation_PropagatesCancellation()
    {
        // Arrange
        var mockDatabaseChecker = new MockCancellationDatabaseChecker();
        var mockLogger = new MockLogger<ReadinessCheckService>();
        var service = new ReadinessCheckService(mockDatabaseChecker, mockLogger);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await FluentActions.Invoking(async () =>
            await service.CheckReadinessAsync(cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [UnitTest]
    [Operation("HealthCheck")]
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
    [Operation("HealthCheck")]
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
    [Operation("HealthCheck")]
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

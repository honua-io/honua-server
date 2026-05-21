// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Server.Features.Infrastructure.Redis;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Infrastructure.Redis;

[Collection("Unit")]
public sealed class RedisHealthMonitorTests
{
    private static RedisConnectionException CreateConnectionException(string message)
        => new(ConnectionFailureType.UnableToConnect, message);

    [UnitTest]
    public void Constructor_WithNullRedis_InitializesCorrectly()
    {
        var monitor = new RedisHealthMonitor(null, NullLogger<RedisHealthMonitor>.Instance);

        monitor.IsRedisAvailable.Should().BeFalse();
        monitor.WasRedisEverAvailable.Should().BeFalse();
        monitor.LastSuccessfulContact.Should().BeNull();
        monitor.LastFailure.Should().BeNull();
        monitor.ConsecutiveFailures.Should().Be(0);
        monitor.ShouldRetryRedis.Should().BeFalse();
    }

    [UnitTest]
    public void Constructor_WithRedis_InitializesAsAvailable()
    {
        var mockRedis = new Mock<IConnectionMultiplexer>();
        var monitor = new RedisHealthMonitor(mockRedis.Object, NullLogger<RedisHealthMonitor>.Instance);

        monitor.IsRedisAvailable.Should().BeTrue();
        monitor.WasRedisEverAvailable.Should().BeTrue();
        monitor.LastSuccessfulContact.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1));
        monitor.LastFailure.Should().BeNull();
        monitor.ConsecutiveFailures.Should().Be(0);
    }

    [UnitTest]
    public void RecordFailure_UpdatesStateCorrectly()
    {
        var mockRedis = new Mock<IConnectionMultiplexer>();
        var monitor = new RedisHealthMonitor(mockRedis.Object, NullLogger<RedisHealthMonitor>.Instance);

        var exception = CreateConnectionException("Test failure");
        monitor.RecordFailure(exception);

        monitor.IsRedisAvailable.Should().BeFalse();
        monitor.WasRedisEverAvailable.Should().BeTrue();
        monitor.LastFailure.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1));
        monitor.ConsecutiveFailures.Should().Be(1);
    }

    [UnitTest]
    public void RecordSuccess_ResetsFailureState()
    {
        var mockRedis = new Mock<IConnectionMultiplexer>();
        var monitor = new RedisHealthMonitor(mockRedis.Object, NullLogger<RedisHealthMonitor>.Instance);

        // Record some failures
        var exception = CreateConnectionException("Test failure");
        monitor.RecordFailure(exception);
        monitor.RecordFailure(exception);

        monitor.ConsecutiveFailures.Should().Be(2);
        monitor.IsRedisAvailable.Should().BeFalse();

        // Record success
        monitor.RecordSuccess();

        monitor.IsRedisAvailable.Should().BeTrue();
        monitor.ConsecutiveFailures.Should().Be(0);
        monitor.LastSuccessfulContact.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1));
    }

    [UnitTest]
    public void ShouldRetryRedis_RespectsRetryInterval()
    {
        var mockRedis = new Mock<IConnectionMultiplexer>();
        var monitor = new RedisHealthMonitor(mockRedis.Object, NullLogger<RedisHealthMonitor>.Instance);

        var exception = CreateConnectionException("Test failure");
        monitor.RecordFailure(exception);

        // Immediately after failure, should not retry (within retry interval)
        monitor.ShouldRetryRedis.Should().BeFalse();
    }

    [UnitTest]
    public async Task TestConnectivityAsync_WithSuccessfulPing_RecordsSuccess()
    {
        var mockDatabase = new Mock<IDatabase>();
        mockDatabase.Setup(db => db.PingAsync()).ReturnsAsync(TimeSpan.FromMilliseconds(10));

        var mockRedis = new Mock<IConnectionMultiplexer>();
        mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDatabase.Object);

        var monitor = new RedisHealthMonitor(mockRedis.Object, NullLogger<RedisHealthMonitor>.Instance);

        // Force failure state first
        monitor.RecordFailure(CreateConnectionException("Test"));
        monitor.IsRedisAvailable.Should().BeFalse();

        var result = await monitor.TestConnectivityAsync();

        result.Should().BeTrue();
        monitor.IsRedisAvailable.Should().BeTrue();
        monitor.ConsecutiveFailures.Should().Be(0);
    }

    [UnitTest]
    public async Task TestConnectivityAsync_WithFailedPing_RecordsFailure()
    {
        var mockDatabase = new Mock<IDatabase>();
        mockDatabase.Setup(db => db.PingAsync()).ThrowsAsync(CreateConnectionException("Connection failed"));

        var mockRedis = new Mock<IConnectionMultiplexer>();
        mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDatabase.Object);

        var monitor = new RedisHealthMonitor(mockRedis.Object, NullLogger<RedisHealthMonitor>.Instance);

        var result = await monitor.TestConnectivityAsync();

        result.Should().BeFalse();
        monitor.IsRedisAvailable.Should().BeFalse();
        monitor.ConsecutiveFailures.Should().Be(1);
        monitor.LastFailure.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1));
    }

    [UnitTest]
    public void Dispose_StopsHealthChecks()
    {
        var mockRedis = new Mock<IConnectionMultiplexer>();
        var monitor = new RedisHealthMonitor(mockRedis.Object, NullLogger<RedisHealthMonitor>.Instance);

        monitor.Dispose();

        // Should not throw and should be safe to call multiple times
        monitor.Dispose();
    }

    [UnitTest]
    public async Task PerformHealthCheck_WhenPingThrows_DoesNotPropagate()
    {
        // Async void timer callbacks must never let exceptions escape; verify by
        // invoking the private callback while the database throws synchronously.
        var mockDatabase = new Mock<IDatabase>();
        mockDatabase.Setup(db => db.PingAsync(It.IsAny<CommandFlags>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var mockRedis = new Mock<IConnectionMultiplexer>();
        mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(mockDatabase.Object);

        var logger = new InMemoryLogger<RedisHealthMonitor>();
        var monitor = new RedisHealthMonitor(mockRedis.Object, logger);

        var unhandled = 0;
        UnhandledExceptionEventHandler handler = (_, _) => Interlocked.Increment(ref unhandled);
        AppDomain.CurrentDomain.UnhandledException += handler;
        try
        {
            InvokePerformHealthCheck(monitor);
            // Yield to let the async void continuation run.
            await Task.Delay(50);
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= handler;
            monitor.Dispose();
        }

        unhandled.Should().Be(0, "async void timer callback must swallow inner exceptions");
        monitor.ConsecutiveFailures.Should().BeGreaterThan(0, "inner failure path must still log via RecordFailure");
    }

    private static void InvokePerformHealthCheck(RedisHealthMonitor monitor)
    {
        var method = typeof(RedisHealthMonitor).GetMethod(
            "PerformHealthCheck",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        method!.Invoke(monitor, new object?[] { null });
    }

    private sealed class InMemoryLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, EventId Id, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullDisposable.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, eventId, formatter(state, exception), exception));
            }
        }

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }
    }
}

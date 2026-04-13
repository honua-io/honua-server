// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Infrastructure.Redis;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Infrastructure.Redis;

[Collection("Unit")]
public sealed class RedisHealthMonitorTests
{
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

        var exception = new RedisConnectionException("Test failure");
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
        var exception = new RedisConnectionException("Test failure");
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

        var exception = new RedisConnectionException("Test failure");
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
        monitor.RecordFailure(new RedisConnectionException("Test"));
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
        mockDatabase.Setup(db => db.PingAsync()).ThrowsAsync(new RedisConnectionException("Connection failed"));

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
}
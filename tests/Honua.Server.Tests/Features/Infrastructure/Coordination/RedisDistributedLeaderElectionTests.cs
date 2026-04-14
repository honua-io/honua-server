// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Infrastructure.Coordination;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Infrastructure.Coordination;

/// <summary>
/// Tests for RedisDistributedLeaderElection — validates distributed leader election behavior.
/// </summary>
[Protocol(Protocols.TestQuality)]
public sealed class RedisDistributedLeaderElectionTests : IDisposable
{
    private readonly RedisDistributedLeaderElection _leaderElection;
    private readonly Mock<IConnectionMultiplexer> _redis = new();
    private readonly Mock<IDatabase> _database = new();

    public RedisDistributedLeaderElectionTests()
    {
        _redis.SetupGet(r => r.IsConnected).Returns(false);
        _redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_database.Object);
        _database.SetupGet(db => db.Multiplexer).Returns(_redis.Object);

        _leaderElection = new RedisDistributedLeaderElection(
            "test-leader-key",
            _redis.Object,
            NullLogger<RedisDistributedLeaderElection>.Instance);
    }

    public void Dispose()
    {
        _leaderElection.Dispose();
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public void Constructor_WithoutRedis_EnablesFallbackMode()
    {
        var election = new RedisDistributedLeaderElection(
            "test-key",
            null,
            NullLogger<RedisDistributedLeaderElection>.Instance);

        election.IsLeader.Should().BeTrue("fallback mode assumes single instance leadership");

        election.Dispose();
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public void Constructor_WithDisconnectedRedis_EnablesFallbackMode()
    {
        var redis = new Mock<IConnectionMultiplexer>();
        redis.SetupGet(r => r.IsConnected).Returns(false);

        var election = new RedisDistributedLeaderElection(
            "test-key",
            redis.Object,
            NullLogger<RedisDistributedLeaderElection>.Instance);

        election.IsLeader.Should().BeTrue("fallback mode when Redis disconnected");

        election.Dispose();
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public void Constructor_WithoutAllowFallback_ThrowsWhenRedisUnavailable()
    {
        var action = () => new RedisDistributedLeaderElection(
            "test-key",
            null,
            NullLogger<RedisDistributedLeaderElection>.Instance,
            allowFallback: false);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*Redis is required*");
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public void InstanceId_IsUniquePerInstance()
    {
        var election1 = new RedisDistributedLeaderElection(
            "test-key", null, NullLogger<RedisDistributedLeaderElection>.Instance);
        var election2 = new RedisDistributedLeaderElection(
            "test-key", null, NullLogger<RedisDistributedLeaderElection>.Instance);

        election1.InstanceId.Should().NotBe(election2.InstanceId);
        election1.InstanceId.Should().NotBeNullOrEmpty();
        election2.InstanceId.Should().NotBeNullOrEmpty();

        election1.Dispose();
        election2.Dispose();
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public async Task TryAcquireLeadershipAsync_FallbackMode_AlwaysReturnsTrue()
    {
        var result = await _leaderElection.TryAcquireLeadershipAsync();

        result.Should().BeTrue();
        _leaderElection.IsLeader.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public async Task HeartbeatAsync_FallbackMode_AlwaysReturnsTrue()
    {
        await _leaderElection.TryAcquireLeadershipAsync();

        var result = await _leaderElection.HeartbeatAsync();

        result.Should().BeTrue();
        _leaderElection.IsLeader.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public async Task ReleaseLeadershipAsync_FallbackMode_ReleasesLeadership()
    {
        await _leaderElection.TryAcquireLeadershipAsync();

        await _leaderElection.ReleaseLeadershipAsync();

        _leaderElection.IsLeader.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public async Task TryAcquireLeadershipAsync_WithConnectedRedis_UsesRedisLock()
    {
        using var election = CreateRedisBackedElection();
        _database.Setup(db => db.StringSetAsync(
                "test-leader-key",
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                When.NotExists))
            .ReturnsAsync(true);

        var result = await election.TryAcquireLeadershipAsync();

        result.Should().BeTrue();
        election.IsLeader.Should().BeTrue();
        _database.Verify(db => db.StringSetAsync(
            "test-leader-key",
            It.IsAny<RedisValue>(),
            It.IsAny<TimeSpan?>(),
            When.NotExists), Times.Once);
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public async Task TryAcquireLeadershipAsync_WithConnectedRedis_WhenLockTaken_ReturnsFalse()
    {
        using var election = CreateRedisBackedElection();
        _database.Setup(db => db.StringSetAsync(
                "test-leader-key",
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                When.NotExists))
            .ReturnsAsync(false);

        var result = await election.TryAcquireLeadershipAsync();

        result.Should().BeFalse();
        election.IsLeader.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public async Task HeartbeatAsync_WithConnectedRedis_ExtendsLease()
    {
        using var election = CreateRedisBackedElection();
        _database.Setup(db => db.StringSetAsync(
                "test-leader-key",
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                When.NotExists))
            .ReturnsAsync(true);
        _database.Setup(db => db.ScriptEvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>()))
            .ReturnsAsync(RedisResult.Create((RedisValue)"1"));

        await election.TryAcquireLeadershipAsync();

        var result = await election.HeartbeatAsync();

        result.Should().BeTrue();
        election.IsLeader.Should().BeTrue();
        _database.Verify(db => db.ScriptEvaluateAsync(
            It.IsAny<string>(),
            It.IsAny<RedisKey[]>(),
            It.IsAny<RedisValue[]>()), Times.Once);
        _database.Verify(db => db.StringSetAsync(
            It.IsAny<RedisKey>(),
            It.IsAny<RedisValue>(),
            It.IsAny<TimeSpan?>(),
            When.Exists), Times.Never);
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public async Task HeartbeatAsync_WhenLeaseIsNoLongerOwned_LosesLeadership()
    {
        using var election = CreateRedisBackedElection();
        _database.Setup(db => db.StringSetAsync(
                "test-leader-key",
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                When.NotExists))
            .ReturnsAsync(true);
        _database.Setup(db => db.ScriptEvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>()))
            .ReturnsAsync(RedisResult.Create((RedisValue)"0"));

        await election.TryAcquireLeadershipAsync();
        election.IsLeader.Should().BeTrue();

        var result = await election.HeartbeatAsync();

        result.Should().BeFalse();
        election.IsLeader.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public async Task ReleaseLeadershipAsync_WithConnectedRedis_DeletesLock()
    {
        using var election = CreateRedisBackedElection();
        _database.Setup(db => db.StringSetAsync(
                "test-leader-key",
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                When.NotExists))
            .ReturnsAsync(true);
        _database.Setup(db => db.ScriptEvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>()))
            .ReturnsAsync(RedisResult.Create((RedisValue)"1"));

        await election.TryAcquireLeadershipAsync();

        await election.ReleaseLeadershipAsync();

        election.IsLeader.Should().BeFalse();
        _database.Verify(db => db.ScriptEvaluateAsync(
            It.IsAny<string>(),
            It.IsAny<RedisKey[]>(),
            It.IsAny<RedisValue[]>()), Times.Once);
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public async Task RedisFailure_FallsBackToLocalMode()
    {
        using var election = CreateRedisBackedElection();
        _database.Setup(db => db.StringSetAsync(
                "test-leader-key",
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                When.NotExists))
            .ThrowsAsync(new RedisException("Connection failed"));

        var result = await election.TryAcquireLeadershipAsync();

        result.Should().BeTrue("should fallback to local leadership on Redis failure");
        election.IsLeader.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public async Task RedisFailure_WithoutFallback_ReturnsFalse()
    {
        _redis.SetupGet(r => r.IsConnected).Returns(true);
        var election = new RedisDistributedLeaderElection(
            "test-key",
            _redis.Object,
            NullLogger<RedisDistributedLeaderElection>.Instance,
            allowFallback: false);

        _database.Setup(db => db.StringSetAsync(
                "test-key",
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                When.NotExists))
            .ThrowsAsync(new RedisException("Connection failed"));

        var result = await election.TryAcquireLeadershipAsync();

        result.Should().BeFalse("should not fallback when fallback disabled");
        election.IsLeader.Should().BeFalse();

        election.Dispose();
    }

    [UnitTest]
    [Operation(Operations.Infrastructure)]
    public async Task Dispose_ReleasesLeadershipGracefully()
    {
        var election = CreateRedisBackedElection();
        _database.Setup(db => db.StringSetAsync(
                "test-leader-key",
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                When.NotExists))
            .ReturnsAsync(true);
        _database.Setup(db => db.ScriptEvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>()))
            .ReturnsAsync(RedisResult.Create((RedisValue)"1"));

        await election.TryAcquireLeadershipAsync();
        election.IsLeader.Should().BeTrue();

        election.Dispose();

        await Task.Delay(50);

        _database.Verify(db => db.ScriptEvaluateAsync(
            It.IsAny<string>(),
            It.IsAny<RedisKey[]>(),
            It.IsAny<RedisValue[]>()), Times.AtLeastOnce);
    }

    private RedisDistributedLeaderElection CreateRedisBackedElection()
    {
        _redis.SetupGet(r => r.IsConnected).Returns(true);

        return new RedisDistributedLeaderElection(
            "test-leader-key",
            _redis.Object,
            NullLogger<RedisDistributedLeaderElection>.Instance);
    }
}

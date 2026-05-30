// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Infrastructure.Redis;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Infrastructure.Redis;

[Collection("Unit")]
public sealed class RedisLeaderElectionTests : IDisposable
{
    private readonly Mock<IConnectionMultiplexer> _mockRedis = new();
    private readonly Mock<IDatabase> _mockDatabase = new();
    private readonly Mock<IHostEnvironment> _mockEnvironment = new();
    private readonly RedisHealthMonitor _healthMonitor;

    public RedisLeaderElectionTests()
    {
        _mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_mockDatabase.Object);
        _healthMonitor = new RedisHealthMonitor(_mockRedis.Object, NullLogger<RedisHealthMonitor>.Instance);
        _mockEnvironment.Setup(e => e.EnvironmentName).Returns("Development");
    }

    private static RedisConnectionException CreateConnectionException(string message)
        => new(ConnectionFailureType.UnableToConnect, message);

    [UnitTest]
    public void Constructor_InitializesCorrectly()
    {
        var election = CreateElection("test-key");

        election.LeadershipKey.Should().Be("test-key");
        election.IsConfigured.Should().BeTrue();
        election.IsLeader.Should().BeFalse();
        election.NodeId.Should().NotBeNullOrEmpty();
        election.LeaseDuration.Should().BePositive();
    }

    [UnitTest]
    public async Task TryAcquireOrExtendLeadershipAsync_WithRedis_AcquiresLeadership()
    {
        _mockDatabase.Setup(db => db.LockTakeAsync("test-key", It.IsAny<RedisValue>(), It.IsAny<TimeSpan>()))
            .ReturnsAsync(true);

        var election = CreateElection("test-key");
        var result = await election.TryAcquireOrExtendLeadershipAsync();

        result.Should().BeTrue();
        election.IsLeader.Should().BeTrue();
        election.CurrentLeader.Should().Be(election.NodeId);
    }

    [UnitTest]
    public async Task TryAcquireOrExtendLeadershipAsync_WithRedis_FailsToAcquire()
    {
        _mockDatabase.Setup(db => db.LockTakeAsync("test-key", It.IsAny<RedisValue>(), It.IsAny<TimeSpan>()))
            .ReturnsAsync(false);

        var election = CreateElection("test-key");
        var result = await election.TryAcquireOrExtendLeadershipAsync();

        result.Should().BeFalse();
        election.IsLeader.Should().BeFalse();
        election.CurrentLeader.Should().BeNull();
    }

    [UnitTest]
    public async Task TryAcquireOrExtendLeadershipAsync_ExtendsExistingLease()
    {
        // First acquire leadership
        _mockDatabase.Setup(db => db.LockTakeAsync("test-key", It.IsAny<RedisValue>(), It.IsAny<TimeSpan>()))
            .ReturnsAsync(true);

        var election = CreateElection("test-key");
        await election.TryAcquireOrExtendLeadershipAsync();
        election.IsLeader.Should().BeTrue();

        // Then extend it
        _mockDatabase.Setup(db => db.LockExtendAsync("test-key", It.IsAny<RedisValue>(), It.IsAny<TimeSpan>()))
            .ReturnsAsync(true);

        var result = await election.TryAcquireOrExtendLeadershipAsync();

        result.Should().BeTrue();
        election.IsLeader.Should().BeTrue();
    }

    [UnitTest]
    public async Task TryAcquireOrExtendLeadershipAsync_InDevelopment_AllowsFallback()
    {
        // Simulate Redis failure
        _healthMonitor.RecordFailure(CreateConnectionException("Test failure"));

        var election = CreateElection("test-key");
        var result = await election.TryAcquireOrExtendLeadershipAsync();

        result.Should().BeTrue(); // Should succeed in development environment
        election.IsLeader.Should().BeTrue();
    }

    [UnitTest]
    public async Task TryAcquireOrExtendLeadershipAsync_InProduction_RejectsOnRedisFailure()
    {
        _mockEnvironment.Setup(e => e.EnvironmentName).Returns("Production");
        _healthMonitor.RecordFailure(CreateConnectionException("Test failure"));

        var election = CreateElection("test-key");
        var result = await election.TryAcquireOrExtendLeadershipAsync();

        result.Should().BeFalse(); // Should fail in production without Redis
        election.IsLeader.Should().BeFalse();
    }

    [UnitTest]
    public async Task ReleaseLeadershipAsync_ReleasesSuccessfully()
    {
        // First acquire leadership
        _mockDatabase.Setup(db => db.LockTakeAsync("test-key", It.IsAny<RedisValue>(), It.IsAny<TimeSpan>()))
            .ReturnsAsync(true);

        var election = CreateElection("test-key");
        await election.TryAcquireOrExtendLeadershipAsync();
        election.IsLeader.Should().BeTrue();

        // Then release it
        _mockDatabase.Setup(db => db.LockReleaseAsync("test-key", It.IsAny<RedisValue>()))
            .ReturnsAsync(true);

        await election.ReleaseLeadershipAsync();

        election.IsLeader.Should().BeFalse();
        election.CurrentLeader.Should().BeNull();
    }

    [UnitTest]
    public async Task LeadershipChanged_EventFiredOnStatusChange()
    {
        _mockDatabase.Setup(db => db.LockTakeAsync("test-key", It.IsAny<RedisValue>(), It.IsAny<TimeSpan>()))
            .ReturnsAsync(true);

        var election = CreateElection("test-key");
        var eventsFired = new List<bool>();

        election.LeadershipChanged += (sender, args) =>
        {
            eventsFired.Add(args.IsLeader);
        };

        await election.TryAcquireOrExtendLeadershipAsync();
        await election.ReleaseLeadershipAsync();

        eventsFired.Should().Equal(true, false);
    }

    [UnitTest]
    public async Task StartStop_ManagesLifecycleCorrectly()
    {
        var election = CreateElection("test-key");

        await election.StartAsync();
        await election.StopAsync();

        election.IsLeader.Should().BeFalse();
    }

    [UnitTest]
    public void Dispose_ReleasesResourcesCleanly()
    {
        var election = CreateElection("test-key");

        election.Dispose();

        // Should not throw and should be safe to call multiple times
        election.Dispose();
    }

    [UnitTest]
    public async Task Dispose_WhenLeader_ReleasesLeadershipBeforeDisposal()
    {
        var releaseStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _mockDatabase.Setup(db => db.LockTakeAsync("test-key", It.IsAny<RedisValue>(), It.IsAny<TimeSpan>()))
            .ReturnsAsync(true);
        _mockDatabase.Setup(db => db.LockReleaseAsync("test-key", It.IsAny<RedisValue>()))
            .Returns(() =>
            {
                releaseStarted.TrySetResult(true);
                return Task.FromResult(true);
            });

        var election = CreateElection("test-key");
        await election.TryAcquireOrExtendLeadershipAsync();

        election.Dispose();
        await releaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        _mockDatabase.Verify(
            db => db.LockReleaseAsync("test-key", It.IsAny<RedisValue>()),
            Times.Once);
    }

    [UnitTest]
    public async Task Dispose_WhenReleaseDoesNotComplete_ReturnsAfterTimeout()
    {
        var releaseStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _mockDatabase.Setup(db => db.LockTakeAsync("test-key", It.IsAny<RedisValue>(), It.IsAny<TimeSpan>()))
            .ReturnsAsync(true);

        var hangingRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockDatabase.Setup(db => db.LockReleaseAsync("test-key", It.IsAny<RedisValue>()))
            .Returns(() =>
            {
                releaseStarted.TrySetResult(true);
                return hangingRelease.Task;
            });

        var election = CreateElection("test-key");
        await election.TryAcquireOrExtendLeadershipAsync();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        election.Dispose();
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
        await releaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        _mockDatabase.Verify(
            db => db.LockReleaseAsync("test-key", It.IsAny<RedisValue>()),
            Times.Once);
    }

    [UnitTest]
    public async Task OnRenewalTimer_WhenInnerWorkThrows_DoesNotPropagate()
    {
        // Async void timer callbacks must never let exceptions escape (no managed handler).
        _mockDatabase.Setup(db => db.LockTakeAsync("test-key", It.IsAny<RedisValue>(), It.IsAny<TimeSpan>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var election = CreateElection("test-key");
        await election.StartAsync();

        var unhandled = 0;
        UnhandledExceptionEventHandler handler = (_, _) => Interlocked.Increment(ref unhandled);
        AppDomain.CurrentDomain.UnhandledException += handler;
        try
        {
            InvokeOnRenewalTimer(election);
            await Task.Delay(50);
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= handler;
            await election.StopAsync();
            election.Dispose();
        }

        unhandled.Should().Be(0, "async void timer callback must swallow inner exceptions");
    }

    private static void InvokeOnRenewalTimer(RedisLeaderElection election)
    {
        var method = typeof(RedisLeaderElection).GetMethod(
            "OnRenewalTimer",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        method!.Invoke(election, new object?[] { null });
    }

    private RedisLeaderElection CreateElection(string key, TimeSpan? leaseDuration = null)
    {
        return new RedisLeaderElection(
            key,
            _mockRedis.Object,
            _healthMonitor,
            _mockEnvironment.Object,
            NullLogger<RedisLeaderElection>.Instance,
            leaseDuration);
    }

    public void Dispose()
    {
        _healthMonitor.Dispose();
    }
}

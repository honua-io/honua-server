// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Alerts;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Server.Tests.Features.Infrastructure.ControlPlane;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class LeaseLeaderElectionStrategyUnitTests
{
    [UnitTest]
    public async Task TryAcquireAsync_WhenAnotherReplicaLeads_IsAHealthyFollower()
    {
        var leases = new InMemoryLeaseStore();
        var leader = LeaderStrategies.Create(leases, TimeSpan.FromSeconds(30));
        var follower = LeaderStrategies.Create(leases, TimeSpan.FromSeconds(30));
        (await leader.TryAcquireAsync()).Should().BeTrue();

        (await follower.TryAcquireAsync()).Should().BeFalse();

        follower.IsLeader.Should().BeFalse();
        follower.LastAcquireFaulted.Should().BeFalse();
    }

    [UnitTest]
    public async Task TryAcquireAsync_WhenTheLeaseStoreFails_StepsDownAndReportsAFault()
    {
        var leases = Substitute.For<ILeaseStore>();
        leases.TryAcquireAsync(default, default, default).ReturnsForAnyArgs(true);
        leases.RenewAsync(default, default, default).ThrowsAsyncForAnyArgs(new TimeoutException("store unavailable"));
        var strategy = LeaderStrategies.Create(leases, TimeSpan.FromSeconds(30));
        (await strategy.TryAcquireAsync()).Should().BeTrue();

        (await strategy.TryAcquireAsync()).Should().BeFalse();

        strategy.IsLeader.Should().BeFalse();
        strategy.LastAcquireFaulted.Should().BeTrue();
    }

    [UnitTest]
    public async Task TryAcquireAsync_AfterTheLeaseStoreRecovers_RenewsTheLeaseItStillHolds()
    {
        var leases = Substitute.For<ILeaseStore>();
        leases.TryAcquireAsync(default, default, default).ReturnsForAnyArgs(true, false);
        leases.RenewAsync(default, default, default).ReturnsForAnyArgs(
            Task.FromException<bool>(new TimeoutException("store unavailable")),
            Task.FromResult(true));
        var strategy = LeaderStrategies.Create(leases, TimeSpan.FromSeconds(30));
        await strategy.TryAcquireAsync();
        await strategy.TryAcquireAsync();

        (await strategy.TryAcquireAsync()).Should().BeTrue();

        strategy.IsLeader.Should().BeTrue();
        strategy.LastAcquireFaulted.Should().BeFalse();
        await leases.ReceivedWithAnyArgs(1).TryAcquireAsync(default, default, default);
    }

    [UnitTest]
    public async Task TryAcquireAsync_WithNoLeaseStore_NeverLeadsAndReportsAFault()
    {
        var strategy = LeaderStrategies.Create(leases: null, TimeSpan.FromSeconds(30));

        (await strategy.TryAcquireAsync()).Should().BeFalse();

        strategy.IsLeader.Should().BeFalse();
        strategy.LastAcquireFaulted.Should().BeTrue();
    }
}

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class LeaseLeaderElectionStrategyTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    private static readonly TimeSpan _shortLease = TimeSpan.FromSeconds(1);

    [IntegrationTest]
    public async Task TryAcquireAsync_TwoReplicasOverOnePostgresLeaseStore_ExactlyOneLeads()
    {
        await using var harness = await CreateHarnessAsync();
        var replicas = new[]
        {
            LeaderStrategies.Create(harness.Leases, TimeSpan.FromSeconds(30)),
            LeaderStrategies.Create(harness.Leases, TimeSpan.FromSeconds(30)),
        };

        var outcomes = await Task.WhenAll(replicas.Select(replica => replica.TryAcquireAsync()));

        outcomes.Count(leads => leads).Should().Be(1);
        replicas.Count(replica => replica.IsLeader).Should().Be(1);
        replicas.Should().OnlyContain(replica => !replica.LastAcquireFaulted);
    }

    [IntegrationTest]
    public async Task TryAcquireAsync_AfterTheLeaderStopsRenewing_TheFollowerLeadsWithinTheLease()
    {
        await using var harness = await CreateHarnessAsync();
        var stopped = LeaderStrategies.Create(harness.Leases, _shortLease);
        var follower = LeaderStrategies.Create(harness.Leases, _shortLease);
        (await stopped.TryAcquireAsync()).Should().BeTrue();
        (await follower.TryAcquireAsync()).Should().BeFalse();

        var leadsAt = await WaitForLeadershipAsync(follower, TimeSpan.FromSeconds(10));

        leadsAt.Should().BeLessThan(TimeSpan.FromSeconds(5));
        (await stopped.TryAcquireAsync()).Should().BeFalse();
        stopped.IsLeader.Should().BeFalse();
        stopped.LastAcquireFaulted.Should().BeFalse();
    }

    [IntegrationTest]
    public async Task TryAcquireAsync_AfterTheLeaderReleases_TheFollowerLeads()
    {
        await using var harness = await CreateHarnessAsync();
        var leader = LeaderStrategies.Create(harness.Leases, TimeSpan.FromSeconds(30));
        var follower = LeaderStrategies.Create(harness.Leases, TimeSpan.FromSeconds(30));
        (await leader.TryAcquireAsync()).Should().BeTrue();
        (await follower.TryAcquireAsync()).Should().BeFalse();

        await leader.ReleaseAsync();

        (await follower.TryAcquireAsync()).Should().BeTrue();
        leader.IsLeader.Should().BeFalse();
    }

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(LeaseLeaderElectionStrategyTests));

    private static async Task<TimeSpan> WaitForLeadershipAsync(LeaseLeaderElectionStrategy strategy, TimeSpan timeout)
    {
        var started = DateTimeOffset.UtcNow;
        while (!await strategy.TryAcquireAsync())
        {
            (DateTimeOffset.UtcNow - started).Should().BeLessThan(timeout, "the follower must lead once the lease expires");
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        return DateTimeOffset.UtcNow - started;
    }
}

internal static class LeaderStrategies
{
    public static LeaseLeaderElectionStrategy Create(ILeaseStore? leases, TimeSpan leaseDuration)
        => new(
            leases,
            Options.Create(new AlertOptions { Evaluation = new AlertEvaluationOptions { LeaderLeaseDuration = leaseDuration } }),
            NullLogger<LeaseLeaderElectionStrategy>.Instance);
}

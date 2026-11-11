// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class LeaseCoordinationTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    private static readonly TimeSpan _leaseDuration = TimeSpan.FromSeconds(30);

    [IntegrationTest]
    public async Task TryAcquireLeadershipAsync_EightNodes_ExactlyOneLeads()
    {
        await using var harness = await CreateHarnessAsync();
        var leadershipKey = NewKey("leader");
        var nodes = Enumerable.Range(0, 8)
            .Select(_ => new LeaseLeaderElection(harness.Leases, leadershipKey, _leaseDuration))
            .ToArray();

        var outcomes = await Task.WhenAll(nodes.Select(node => node.TryAcquireLeadershipAsync()));

        outcomes.Count(leads => leads).Should().Be(1);
        nodes.Count(node => node.IsLeader).Should().Be(1);
    }

    [IntegrationTest]
    public async Task Leadership_AfterTheLeaderStopsRenewing_MovesWhenTheLeaseExpires()
    {
        await using var harness = await CreateHarnessAsync();
        var leadershipKey = NewKey("leader");
        var stopped = new LeaseLeaderElection(harness.Leases, leadershipKey, _leaseDuration);
        var successor = new LeaseLeaderElection(harness.Leases, leadershipKey, _leaseDuration);
        await stopped.TryAcquireLeadershipAsync();
        (await successor.TryAcquireLeadershipAsync()).Should().BeFalse();

        await ExpireAsync(harness, leadershipKey);

        (await successor.TryAcquireLeadershipAsync()).Should().BeTrue();
        (await stopped.HeartbeatAsync()).Should().BeFalse();
        stopped.IsLeader.Should().BeFalse();
    }

    [IntegrationTest]
    public async Task LeaseLostToken_WhenRenewalIsRefused_IsCancelled()
    {
        await using var harness = await CreateHarnessAsync();
        var leaseKey = NewKey("holder");
        using var holder = new LeaseHolder(harness.Leases, leaseKey, _leaseDuration);
        (await holder.TryAcquireOrExtendAsync()).Should().BeTrue();
        var leaseLost = holder.LeaseLostToken;
        await ExpireAsync(harness, leaseKey);
        await harness.Leases.TryAcquireAsync(new LeaseClaim(leaseKey, "another-node"), _leaseDuration);

        (await holder.TryAcquireOrExtendAsync()).Should().BeFalse();

        leaseLost.IsCancellationRequested.Should().BeTrue();
        holder.HasLease.Should().BeFalse();
    }

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(LeaseCoordinationTests));

    private static Task ExpireAsync(ControlPlaneStateHarness harness, string leaseKey)
        => harness.ExecuteAsync(
            $"UPDATE {harness.Table("leases")} SET expires_at = now() - interval '1 second' WHERE lease_key LIKE @lease_key",
            new NpgsqlParameter("lease_key", leaseKey + "%"));

    private static string NewKey(string purpose) => $"test-{purpose}:{Guid.NewGuid():N}";
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Server.Features.Infrastructure.Coordination;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class LeaseVersionLockTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    private static readonly TimeSpan _leaseDuration = TimeSpan.FromSeconds(30);

    [IntegrationTest]
    public async Task TryAcquireAsync_SixteenContenders_ExactlyOneHolds()
    {
        await using var harness = await CreateHarnessAsync();
        var leases = new PostgresLeaseStore(harness.JobStore);
        var leaseKey = NewKey();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 16).Select(node =>
            leases.TryAcquireAsync(new LeaseClaim(leaseKey, "node-" + node), _leaseDuration)));

        outcomes.Count(held => held).Should().Be(1);
    }

    [IntegrationTest]
    public async Task TryAcquireAsync_AfterExpiry_AnotherOwnerTakesOverAndTheFirstCannotRenew()
    {
        await using var harness = await CreateHarnessAsync();
        var leases = new PostgresLeaseStore(harness.JobStore);
        var held = new LeaseClaim(NewKey(), "node-a");
        var contender = held with { OwnerId = "node-b" };
        await leases.TryAcquireAsync(held, _leaseDuration);
        (await leases.TryAcquireAsync(contender, _leaseDuration)).Should().BeFalse();

        await harness.ExecuteAsync(
            $"UPDATE {harness.Database.Table("leases")} SET expires_at = now() - interval '1 second' WHERE lease_key = @lease_key",
            new NpgsqlParameter("lease_key", held.LeaseKey));

        (await leases.TryAcquireAsync(contender, _leaseDuration)).Should().BeTrue();
        (await leases.RenewAsync(held, _leaseDuration)).Should().BeFalse();
    }

    [IntegrationTest]
    public async Task VersionLock_SecondNode_ReturnsNullUntilRelease()
    {
        await using var harness = await CreateHarnessAsync();
        var versionId = Guid.NewGuid();
        var firstNode = new LeaseVersionLock(new PostgresLeaseStore(harness.JobStore));
        var secondNode = new LeaseVersionLock(new PostgresLeaseStore(harness.CreateNodeJobStore()));

        var held = await firstNode.TryAcquireAsync("honua.versioning", versionId, _leaseDuration);
        held.Should().NotBeNull();
        (await secondNode.TryAcquireAsync("honua.versioning", versionId, _leaseDuration)).Should().BeNull();
        await using var otherVersion = await secondNode.TryAcquireAsync("honua.versioning", Guid.NewGuid(), _leaseDuration);
        otherVersion.Should().NotBeNull();

        await held!.DisposeAsync();

        await using var afterRelease = await secondNode.TryAcquireAsync("honua.versioning", versionId, _leaseDuration);
        afterRelease.Should().NotBeNull();
    }

    private Task<PostgresJobSubstrateHarness> CreateHarnessAsync()
        => PostgresJobSubstrateHarness.CreateAsync(database, nameof(LeaseVersionLockTests));

    private static string NewKey() => $"test-lease:{Guid.NewGuid():N}";
}

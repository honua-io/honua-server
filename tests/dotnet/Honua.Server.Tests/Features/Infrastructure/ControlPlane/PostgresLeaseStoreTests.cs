// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresLeaseStoreTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    private static readonly TimeSpan _leaseDuration = TimeSpan.FromSeconds(30);

    [IntegrationTest]
    public async Task ReleaseAsync_ByANonOwner_LeavesTheLeaseHeld()
    {
        await using var harness = await CreateHarnessAsync();
        var held = new LeaseClaim(NewLeaseKey(), "node-a");
        await harness.Leases.TryAcquireAsync(held, _leaseDuration);

        await harness.Leases.ReleaseAsync(held with { OwnerId = "node-b" });

        (await harness.Leases.RenewAsync(held, _leaseDuration)).Should().BeTrue();
    }

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresLeaseStoreTests));

    private static string NewLeaseKey() => "test-lease:" + Guid.NewGuid().ToString("N");
}

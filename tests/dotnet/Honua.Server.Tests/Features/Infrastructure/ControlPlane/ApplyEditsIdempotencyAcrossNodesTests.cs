// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class ApplyEditsIdempotencyAcrossNodesTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    private static readonly ApplyEditsIdempotencyScope _scope = new("svc", 0, new IdempotencyCaller("none", "alice"), "key-across-nodes");

    [IntegrationTest]
    public async Task TryReserveAsync_EightNodesAtOnce_ExactlyOneEditRuns()
    {
        await using var harness = await CreateHarnessAsync();

        var tokens = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => NodeOver(harness).TryReserveAsync(_scope)));

        tokens.Count(token => token is not null).Should().Be(1);
    }

    [IntegrationTest]
    public async Task RecordedResponse_OnOneNode_ReplaysOnAnotherAndBlocksAFreshReservation()
    {
        await using var harness = await CreateHarnessAsync();
        var applying = NodeOver(harness);
        var token = await applying.TryReserveAsync(_scope);

        (await applying.SetAsync(_scope, token!, new ApplyEditsResponse { Success = true })).Should().BeTrue();

        var retryNode = NodeOver(harness);
        (await retryNode.TryGetAsync(_scope))!.Success.Should().BeTrue();
        (await retryNode.TryReserveAsync(_scope)).Should().BeNull();
    }

    [IntegrationTest]
    public async Task ReleaseAsync_OnTheOwningNode_LetsAnotherNodeReserve()
    {
        await using var harness = await CreateHarnessAsync();
        var owner = NodeOver(harness);
        var token = await owner.TryReserveAsync(_scope);

        await owner.ReleaseAsync(_scope, token!);

        (await NodeOver(harness).TryReserveAsync(_scope)).Should().NotBeNull();
    }

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(ApplyEditsIdempotencyAcrossNodesTests));

    private static DistributedApplyEditsIdempotencyStore NodeOver(ControlPlaneStateHarness harness)
        => new(harness.CreateIdempotencyReservationStore(), NullLogger<DistributedApplyEditsIdempotencyStore>.Instance);
}

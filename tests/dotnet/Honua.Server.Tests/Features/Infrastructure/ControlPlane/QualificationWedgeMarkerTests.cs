// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.HealthCheck;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.Health)]
[Operation(Operations.ReadinessCheck)]
public sealed class QualificationWedgeMarkerTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task ShouldWedgeAsync_EveryTaskOfTheFirstRollout_Wedges()
    {
        await using var harness = await CreateHarnessAsync();
        var token = NewToken();

        var decisions = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            new QualificationWedgeMarker(harness.Database).ShouldWedgeAsync(token, CancellationToken.None)));

        decisions.Should().AllSatisfy(decision => decision.Should().BeTrue());
    }

    [IntegrationTest]
    public async Task ShouldWedgeAsync_TaskStartedAfterTheArmingWindow_ServesNormally()
    {
        await using var harness = await CreateHarnessAsync();
        var marker = new QualificationWedgeMarker(harness.Database);
        var token = NewToken();
        (await marker.ShouldWedgeAsync(token, CancellationToken.None)).Should().BeTrue();

        await harness.ExecuteAsync(
            $"UPDATE {harness.Table("leases")} SET expires_at = expires_at - @age WHERE lease_key = @lease_key",
            new NpgsqlParameter("age", QualificationWedgeMarker.ArmingWindow + TimeSpan.FromSeconds(1)),
            new NpgsqlParameter("lease_key", QualificationWedgeMarker.LeaseKey(token)));

        (await marker.ShouldWedgeAsync(token, CancellationToken.None)).Should().BeFalse("the recovery restart must not wedge again");
        (await marker.ShouldWedgeAsync(NewToken(), CancellationToken.None)).Should().BeTrue("a new gate run arms a new token");
    }

    [IntegrationTest]
    public async Task ShouldWedgeAsync_MarkerOutlivesTheWindow_SoTheRetentionSweepKeepsIt()
    {
        await using var harness = await CreateHarnessAsync();
        var token = NewToken();

        await new QualificationWedgeMarker(harness.Database).ShouldWedgeAsync(token, CancellationToken.None);

        (await harness.ScalarAsync<bool>(
            $"SELECT expires_at > now() + interval '1 day' FROM {harness.Table("leases")} WHERE lease_key = @lease_key",
            new NpgsqlParameter("lease_key", QualificationWedgeMarker.LeaseKey(token)))).Should().BeTrue();
    }

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(QualificationWedgeMarkerTests));

    private static string NewToken() => "gate-" + Guid.NewGuid().ToString("N");
}

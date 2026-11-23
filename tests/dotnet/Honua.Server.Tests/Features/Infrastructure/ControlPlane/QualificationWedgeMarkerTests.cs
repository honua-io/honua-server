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
            Marker(harness, "initial").ShouldWedgeAsync(token, CancellationToken.None)));

        decisions.Should().AllSatisfy(decision => decision.Should().BeTrue());
    }

    [IntegrationTest]
    public async Task ShouldWedgeAsync_ReplacementDeploymentServesImmediately_LateOriginalTaskStillWedges()
    {
        await using var harness = await CreateHarnessAsync();
        var original = Marker(harness, "initial");
        var replacement = Marker(harness, "replacement");
        var token = NewToken();

        (await original.ShouldWedgeAsync(token, CancellationToken.None)).Should().BeTrue();
        (await replacement.ShouldWedgeAsync(token, CancellationToken.None)).Should().BeFalse();

        await harness.ExecuteAsync(
            $"UPDATE {harness.Table("leases")} SET expires_at = now() + interval '1 day' WHERE lease_key = @lease_key",
            new NpgsqlParameter("lease_key", QualificationWedgeMarker.LeaseKey(token)));

        (await replacement.ShouldWedgeAsync(token, CancellationToken.None)).Should().BeFalse();
        (await original.ShouldWedgeAsync(token, CancellationToken.None)).Should().BeTrue();
        (await harness.ScalarAsync<bool>(
            $"SELECT expires_at = 'infinity' FROM {harness.Table("leases")} WHERE lease_key = @lease_key",
            new NpgsqlParameter("lease_key", QualificationWedgeMarker.LeaseKey(token)))).Should().BeTrue();
        (await replacement.ShouldWedgeAsync(NewToken(), CancellationToken.None)).Should().BeTrue();
    }

    [IntegrationTest]
    public async Task ShouldWedgeAsync_MarkerPersistsForTheQualificationSession()
    {
        await using var harness = await CreateHarnessAsync();
        var token = NewToken();

        await Marker(harness, "initial").ShouldWedgeAsync(token, CancellationToken.None);

        (await harness.ScalarAsync<bool>(
            $"SELECT expires_at > now() + interval '1 day' FROM {harness.Table("leases")} WHERE lease_key = @lease_key",
            new NpgsqlParameter("lease_key", QualificationWedgeMarker.LeaseKey(token)))).Should().BeTrue();
    }

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(QualificationWedgeMarkerTests));

    private static QualificationWedgeMarker Marker(ControlPlaneStateHarness harness, string deployment)
        => new(harness.Database, _ => Task.FromResult(deployment));

    private static string NewToken() => "gate-" + Guid.NewGuid().ToString("N");
}

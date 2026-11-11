// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Security;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresTokenReplayStoreTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    private const string SingleUseMarker = "1";
    private static readonly TimeSpan _lifetime = TimeSpan.FromMinutes(10);

    [IntegrationTest]
    public async Task TryRegisterAsync_SixteenConcurrentPresentations_ExactlyOneRegisters()
    {
        await using var harness = await CreateHarnessAsync();
        var request = new TokenReplayRegistrationRequest(NewReplayKey(), SingleUseMarker, _lifetime);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            harness.CreateTokenReplayStore().TryRegisterAsync(request, CancellationToken.None)));

        outcomes.Count(outcome => outcome.Registered).Should().Be(1);
        outcomes.Where(outcome => !outcome.Registered).Should().OnlyContain(outcome => outcome.ExistingMarker == SingleUseMarker);
    }

    [IntegrationTest]
    public async Task TryRegisterAsync_AfterTheFirstPresentationExpired_RegistersAgain()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateTokenReplayStore();
        var request = new TokenReplayRegistrationRequest(NewReplayKey(), "surface:http", _lifetime);
        await store.TryRegisterAsync(request, CancellationToken.None);
        await harness.ExecuteAsync($"UPDATE {harness.Table("token_replay")} SET expires_at = now() - interval '1 second'");

        (await store.TryRegisterAsync(request with { Marker = SingleUseMarker }, CancellationToken.None)).Registered.Should().BeTrue();
        (await store.TryRegisterAsync(request, CancellationToken.None)).ExistingMarker.Should().Be(SingleUseMarker);
    }

    [IntegrationTest]
    public async Task TryRebindAsync_TwoContinuations_FirstWinsAndItsRetrySucceeds()
    {
        await using var harness = await CreateHarnessAsync();
        var replayKey = NewReplayKey();
        await harness.CreateTokenReplayStore().TryRegisterAsync(
            new TokenReplayRegistrationRequest(replayKey, SingleUseMarker, _lifetime), CancellationToken.None);
        var first = new TokenReplayRebinding(replayKey, SingleUseMarker, "continuation:a");
        var second = first with { ToMarker = "continuation:b" };

        (await harness.CreateTokenReplayStore().TryRebindAsync(first, CancellationToken.None)).Should().BeTrue();
        (await harness.CreateTokenReplayStore().TryRebindAsync(second, CancellationToken.None)).Should().BeFalse();
        (await harness.CreateTokenReplayStore().TryRebindAsync(first, CancellationToken.None)).Should().BeTrue();
    }

    [IntegrationTest]
    public async Task TryRegister_SameIdOnTwoNodes_SecondIsRejected()
    {
        await using var harness = await CreateHarnessAsync();
        var assertion = new TokenReplayRegistrationRequest("saml:" + Guid.NewGuid().ToString("N"), SingleUseMarker, _lifetime);

        harness.CreateTokenReplayStore().TryRegister(assertion).Should().BeTrue();
        harness.CreateTokenReplayStore().TryRegister(assertion).Should().BeFalse();
    }

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresTokenReplayStoreTests));

    private static string NewReplayKey() => "jti:issuer:" + Guid.NewGuid().ToString("N");
}

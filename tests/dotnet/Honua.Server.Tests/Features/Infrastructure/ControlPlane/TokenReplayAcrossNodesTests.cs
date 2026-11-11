// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Security;
using Honua.Server.Features.Identity.Saml;
using Honua.Server.Tests.Infrastructure;
using Honua.Server.Tests.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[SecurityTest]
[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.Security)]
public sealed class TokenReplayAcrossNodesTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task BoundToken_AlternatingNodes_IsAdmittedOnlyOnItsContinuation()
    {
        await using var harness = await CreateHarnessAsync();

        await TokenReplayContinuationSemantics.AssertContinuationAsync(Alternating(harness));
    }

    [IntegrationTest]
    public async Task HttpApiToken_AlternatingNodes_IsReusableThereAndReplaysOnContinuationSurface()
    {
        await using var harness = await CreateHarnessAsync();

        await TokenReplayContinuationSemantics.AssertHttpApiReuseAsync(Alternating(harness));
    }

    [IntegrationTest]
    public async Task SamlAssertion_PresentedOnEightNodesAtOnce_IsAcceptedOnce()
    {
        await using var harness = await CreateHarnessAsync();
        var assertionId = "_" + Guid.NewGuid().ToString("N");

        var accepted = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            new SamlAssertionReplayCache(harness.CreateTokenReplayStore())
                .TryRemember(assertionId, DateTimeOffset.UtcNow.AddMinutes(5)))));

        Assert.Single(accepted, remembered => remembered);
    }

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(TokenReplayAcrossNodesTests));

    private static Func<ITokenReplayStore> Alternating(ControlPlaneStateHarness harness)
    {
        var nodes = new ITokenReplayStore[] { harness.CreateTokenReplayStore(), harness.CreateTokenReplayStore() };
        var next = 0;
        return () => nodes[next++ % nodes.Length];
    }
}

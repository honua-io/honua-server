// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class PortalOAuthStoreAcrossNodesTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task ConsumeAuthorizationCodeAsync_EightNodesAtOnce_ExactlyOneRedeemsTheCode()
    {
        await using var harness = await CreateHarnessAsync();
        var issuingNode = NodeOver(harness);
        var code = await issuingNode.CreateAuthorizationCodeAsync(AuthorizationCode(), CancellationToken.None);

        var redeemed = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => NodeOver(harness).ConsumeAuthorizationCodeAsync(code, CancellationToken.None)));

        redeemed.Count(result => result is not null).Should().Be(1);
    }

    [IntegrationTest]
    public async Task RefreshToken_RotatedOnOneNode_CannotBeReplayedOnAnother()
    {
        await using var harness = await CreateHarnessAsync();
        var token = await NodeOver(harness).CreateRefreshTokenAsync(RefreshToken(), CancellationToken.None);

        var rotated = await NodeOver(harness).ConsumeRefreshTokenAsync(token, CancellationToken.None);
        var replayed = await NodeOver(harness).ConsumeRefreshTokenAsync(token, CancellationToken.None);

        rotated.Should().NotBeNull();
        replayed.Should().BeNull();
    }

    [IntegrationTest]
    public async Task RevokePrincipalAsync_OnOneNode_RejectsEarlierRefreshTokensOnAnother()
    {
        await using var harness = await CreateHarnessAsync();
        var issuedBeforeRevocation = DateTimeOffset.UtcNow.AddSeconds(-1);

        await NodeOver(harness).RevokePrincipalAsync("user@example.com", CancellationToken.None);

        (await NodeOver(harness).IsPrincipalRefreshRevokedAsync("user@example.com", issuedBeforeRevocation, CancellationToken.None))
            .Should().BeTrue();
    }

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(PortalOAuthStoreAcrossNodesTests));

    private static PortalOAuthStore NodeOver(ControlPlaneStateHarness harness)
        => new(harness.CreateSecurityTokenStore(), NullLogger<PortalOAuthStore>.Instance);

    private static PortalOAuthAuthorizationCode AuthorizationCode()
        => new()
        {
            ClientId = "arcgispro",
            RedirectUri = "https://app.example.com/redirect",
            Principal = Principal(),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        };

    private static PortalOAuthRefreshToken RefreshToken()
        => new()
        {
            ClientId = "arcgispro",
            Principal = Principal(),
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
        };

    private static PortalOAuthPrincipal Principal()
        => new() { PrincipalId = "user@example.com", Roles = ["viewer"] };
}

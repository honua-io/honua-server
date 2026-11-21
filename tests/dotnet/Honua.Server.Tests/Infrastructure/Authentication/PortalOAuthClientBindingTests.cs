// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Infrastructure.Authentication;

[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class PortalOAuthClientBindingTests
{
    private const string RegisteredRedirect = "https://app.example.com/oauth/redirect";
    private const string OtherAllowedRedirect = "https://app.example.com/oauth/other";

    [UnitTest]
    public async Task UnregisteredLabel_KeepsTheDeploymentAllowList()
    {
        var store = new InMemoryOAuthClientStore();

        var decision = await AuthorizeAsync(store, "arcgispro", OtherAllowedRedirect);

        Assert.Equal(PortalOAuthClientDecision.Allowed, decision);
    }

    [UnitTest]
    public async Task RegisteredClient_IsHeldToItsOwnRedirectUris()
    {
        var store = new InMemoryOAuthClientStore();
        var client = await RegisterAsync(store, grantTypes: ["authorization_code"]);

        Assert.Equal(PortalOAuthClientDecision.Allowed, await AuthorizeAsync(store, client.ClientId, RegisteredRedirect));
        Assert.Equal(
            PortalOAuthClientDecision.RedirectNotRegistered,
            await AuthorizeAsync(store, client.ClientId, OtherAllowedRedirect));
    }

    [UnitTest]
    public async Task RegisteredClient_WithoutTheCodeGrant_IsRefused()
    {
        var store = new InMemoryOAuthClientStore();
        var client = await RegisterAsync(store, grantTypes: ["client_credentials"]);

        var decision = await AuthorizeAsync(store, client.ClientId, RegisteredRedirect);

        Assert.Equal(PortalOAuthClientDecision.GrantNotAllowed, decision);
    }

    [UnitTest]
    public async Task DeletedClient_DoesNotFallBackToALabel()
    {
        var store = new InMemoryOAuthClientStore();
        var client = await RegisterAsync(store, grantTypes: []);
        await store.DeleteAsync(client.Id, CancellationToken.None);

        var decision = await AuthorizeAsync(store, client.ClientId, RegisteredRedirect);

        Assert.Equal(PortalOAuthClientDecision.UnknownClient, decision);
    }

    [UnitTest]
    public async Task ExpiredClient_IsRefused()
    {
        var store = new InMemoryOAuthClientStore();
        var client = await RegisterAsync(store, grantTypes: [], expiresAt: DateTimeOffset.UtcNow.AddMinutes(5));

        var decision = await PortalOAuthClientBinding.AuthorizeAsync(
            store,
            client.ClientId,
            RegisteredRedirect,
            new ShiftedTimeProvider(TimeSpan.FromHours(1)),
            CancellationToken.None);

        Assert.Equal(PortalOAuthClientDecision.InactiveClient, decision);
    }

    [UnitTest]
    public void RegistryShape_MatchesMintedIdsOnly()
    {
        Assert.True(OAuthClientIdentifiers.HasRegistryShape("client_" + new string('A', 24)));
        Assert.False(OAuthClientIdentifiers.HasRegistryShape("client_short"));
        Assert.False(OAuthClientIdentifiers.HasRegistryShape("arcgispro"));
        Assert.False(OAuthClientIdentifiers.HasRegistryShape("client_" + new string('!', 24)));
    }

    internal static async Task<OAuthClientRecord> RegisterAsync(
        IOAuthClientStore store,
        string[] grantTypes,
        DateTimeOffset? expiresAt = null)
    {
        var created = await store.CreateAsync(
            new OAuthClientRegistration(
                Name: "Field app",
                ClientType: OAuthClientType.Public,
                AllowedGrantTypes: grantTypes,
                RedirectUris: [RegisteredRedirect],
                AllowedScopes: [],
                ExpiresAt: expiresAt,
                CreatedBy: "test"),
            CancellationToken.None);
        return created.Record;
    }

    private static Task<PortalOAuthClientDecision> AuthorizeAsync(IOAuthClientStore store, string clientId, string redirectUri)
        => PortalOAuthClientBinding.AuthorizeAsync(store, clientId, redirectUri, TimeProvider.System, CancellationToken.None);

    private sealed class ShiftedTimeProvider(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + offset;
    }
}

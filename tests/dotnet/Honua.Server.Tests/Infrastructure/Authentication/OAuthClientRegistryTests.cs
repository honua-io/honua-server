// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// Unit tests for the first-class OAuth2 client registry and scope catalogue
/// (ADR-0053 Increment 2, #1888): client CRUD, secret hashing (no plaintext at
/// rest), secret validation, and scope grant/narrowing semantics.
/// </summary>
[SecurityTest]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Security)]
public sealed class OAuthClientRegistryTests
{
    [UnitTest]
    public async Task Create_ConfidentialClient_MintsSecretAndStoresHashNotPlaintext()
    {
        var store = new InMemoryOAuthClientStore();

        var result = await store.CreateAsync(
            new OAuthClientRegistration(
                Name: "etl-worker",
                ClientType: OAuthClientType.Confidential,
                AllowedGrantTypes: ["client_credentials"],
                RedirectUris: [],
                AllowedScopes: ["features:read"],
                ExpiresAt: null,
                CreatedBy: "test"),
            CancellationToken.None);

        result.Secret.Should().NotBeNullOrWhiteSpace();
        result.Record.ClientId.Should().StartWith("client_");
        // The secret is never persisted in plaintext: only a SHA-256 hash and a
        // non-secret display prefix are stored on the record.
        result.Record.SecretHash.Should().NotBeNull();
        result.Record.SecretPrefix.Should().NotBeNullOrWhiteSpace();
        result.Record.SecretHash!.Should().NotEqual(System.Text.Encoding.UTF8.GetBytes(result.Secret!));
    }

    [UnitTest]
    public async Task Create_PublicClient_MintsNoSecret()
    {
        var store = new InMemoryOAuthClientStore();

        var result = await store.CreateAsync(
            new OAuthClientRegistration(
                Name: "spa",
                ClientType: OAuthClientType.Public,
                AllowedGrantTypes: ["authorization_code"],
                RedirectUris: ["https://app.example.org/callback"],
                AllowedScopes: [],
                ExpiresAt: null,
                CreatedBy: "test"),
            CancellationToken.None);

        result.Secret.Should().BeNull();
        result.Record.SecretHash.Should().BeNull();
    }

    [UnitTest]
    public async Task ValidateSecret_WithCorrectPair_ReturnsRecord()
    {
        var store = new InMemoryOAuthClientStore();
        var created = await CreateConfidentialAsync(store);

        var validated = await store.ValidateSecretAsync(
            created.Record.ClientId,
            created.Secret!,
            CancellationToken.None);

        validated.Should().NotBeNull();
        validated!.ClientId.Should().Be(created.Record.ClientId);
        validated.LastUsedAt.Should().NotBeNull();
    }

    [UnitTest]
    public async Task ValidateSecret_WithWrongSecret_ReturnsNull()
    {
        var store = new InMemoryOAuthClientStore();
        var created = await CreateConfidentialAsync(store);

        var validated = await store.ValidateSecretAsync(
            created.Record.ClientId,
            "secret_not-the-real-secret",
            CancellationToken.None);

        validated.Should().BeNull();
    }

    [UnitTest]
    public async Task ValidateSecret_WhenDeleted_ReturnsNull()
    {
        var store = new InMemoryOAuthClientStore();
        var created = await CreateConfidentialAsync(store);

        await store.DeleteAsync(created.Record.Id, CancellationToken.None);

        var validated = await store.ValidateSecretAsync(
            created.Record.ClientId,
            created.Secret!,
            CancellationToken.None);

        validated.Should().BeNull();
    }

    [UnitTest]
    public async Task ValidateSecret_WhenExpired_ReturnsNull()
    {
        var store = new InMemoryOAuthClientStore();
        var created = await store.CreateAsync(
            new OAuthClientRegistration(
                Name: "expired",
                ClientType: OAuthClientType.Confidential,
                AllowedGrantTypes: ["client_credentials"],
                RedirectUris: [],
                AllowedScopes: [],
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-1),
                CreatedBy: "test"),
            CancellationToken.None);

        var validated = await store.ValidateSecretAsync(
            created.Record.ClientId,
            created.Secret!,
            CancellationToken.None);

        validated.Should().BeNull();
    }

    [UnitTest]
    public async Task ScopeCatalogue_ResolveGrant_GrantsPermissionsForAllowedDefinedScopes()
    {
        var catalogue = new InMemoryOAuthScopeCatalogue();
        await catalogue.DefineAsync(
            new OAuthScopeDefinition("features:read", "Read features", ["services:read"]),
            CancellationToken.None);
        await catalogue.DefineAsync(
            new OAuthScopeDefinition("features:write", "Write features", ["services:write"]),
            CancellationToken.None);

        var grant = await catalogue.ResolveGrantAsync(
            requestedScope: "features:read",
            allowedScopes: ["features:read", "features:write"],
            CancellationToken.None);

        grant.GrantedScopes.Should().BeEquivalentTo("features:read");
        grant.GrantedPermissions.Should().BeEquivalentTo("services:read");
    }

    [UnitTest]
    public async Task ScopeCatalogue_ResolveGrant_DropsScopeOutsideClientAllowList()
    {
        var catalogue = new InMemoryOAuthScopeCatalogue();
        await catalogue.DefineAsync(
            new OAuthScopeDefinition("features:read", "Read features", ["services:read"]),
            CancellationToken.None);
        await catalogue.DefineAsync(
            new OAuthScopeDefinition("admin:all", "Admin", ["admin:*"]),
            CancellationToken.None);

        // The client requests an admin scope it is not allowed to hold; it must be
        // dropped, never escalated.
        var grant = await catalogue.ResolveGrantAsync(
            requestedScope: "features:read admin:all",
            allowedScopes: ["features:read"],
            CancellationToken.None);

        grant.GrantedScopes.Should().BeEquivalentTo("features:read");
        grant.GrantedPermissions.Should().NotContain("admin:*");
    }

    [UnitTest]
    public async Task ScopeCatalogue_ResolveGrant_NoRequestedScope_GrantsClientDefaultScopes()
    {
        var catalogue = new InMemoryOAuthScopeCatalogue();
        await catalogue.DefineAsync(
            new OAuthScopeDefinition("features:read", "Read features", ["services:read"]),
            CancellationToken.None);

        var grant = await catalogue.ResolveGrantAsync(
            requestedScope: null,
            allowedScopes: ["features:read"],
            CancellationToken.None);

        grant.GrantedScopes.Should().BeEquivalentTo("features:read");
        grant.GrantedPermissions.Should().BeEquivalentTo("services:read");
    }

    [UnitTest]
    public async Task ScopeCatalogue_ResolveGrant_UndefinedAllowedScope_GrantsNoPermissions()
    {
        var catalogue = new InMemoryOAuthScopeCatalogue();

        // The client is allowed a scope that has no catalogue definition: it grants
        // the scope label but no permissions (cannot escalate to undefined).
        var grant = await catalogue.ResolveGrantAsync(
            requestedScope: "features:read",
            allowedScopes: ["features:read"],
            CancellationToken.None);

        grant.GrantedScopes.Should().BeEmpty();
        grant.GrantedPermissions.Should().BeEmpty();
    }

    private static Task<OAuthClientCreateResult> CreateConfidentialAsync(InMemoryOAuthClientStore store)
        => store.CreateAsync(
            new OAuthClientRegistration(
                Name: "svc",
                ClientType: OAuthClientType.Confidential,
                AllowedGrantTypes: ["client_credentials"],
                RedirectUris: [],
                AllowedScopes: ["features:read"],
                ExpiresAt: null,
                CreatedBy: "test"),
            CancellationToken.None);
}

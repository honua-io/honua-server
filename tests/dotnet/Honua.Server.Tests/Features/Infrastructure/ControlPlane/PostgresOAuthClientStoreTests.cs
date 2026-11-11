// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresOAuthClientStoreTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task ValidateSecretAsync_OnASecondStore_AcceptsTheSecretAndRecordsTheUse()
    {
        await using var harness = await CreateHarnessAsync();
        var created = await harness.CreateOAuthClientStore().CreateAsync(Registration(OAuthClientType.Confidential), CancellationToken.None);
        var otherNode = harness.CreateOAuthClientStore();

        var validated = await otherNode.ValidateSecretAsync(created.Record.ClientId, created.Secret!, CancellationToken.None);

        validated!.Id.Should().Be(created.Record.Id);
        validated.LastUsedAt.Should().NotBeNull();
        (await otherNode.ValidateSecretAsync(created.Record.ClientId, "secret_wrong", CancellationToken.None)).Should().BeNull();
        (await otherNode.ValidateSecretAsync("client_unknown", created.Secret!, CancellationToken.None)).Should().BeNull();
    }

    [IntegrationTest]
    public async Task CreateAsync_PersistsADigestAndNeverTheSecret()
    {
        await using var harness = await CreateHarnessAsync();
        var created = await harness.CreateOAuthClientStore().CreateAsync(Registration(OAuthClientType.Confidential), CancellationToken.None);

        var rowAsText = await harness.ScalarAsync<string>(
            $"SELECT record::text || encode(secret_hash, 'escape') FROM {harness.Table("oauth_clients")}");

        rowAsText.Should().NotContain(created.Secret!);
    }

    [IntegrationTest]
    public async Task ValidateSecretAsync_AfterDelete_ReturnsNull()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateOAuthClientStore();
        var created = await store.CreateAsync(Registration(OAuthClientType.Confidential), CancellationToken.None);

        (await harness.CreateOAuthClientStore().DeleteAsync(created.Record.Id, CancellationToken.None))!.Id.Should().Be(created.Record.Id);

        (await store.ValidateSecretAsync(created.Record.ClientId, created.Secret!, CancellationToken.None)).Should().BeNull();
        (await store.GetAsync(created.Record.Id, CancellationToken.None)).Should().BeNull();
        (await store.DeleteAsync(created.Record.Id, CancellationToken.None)).Should().BeNull();
    }

    [IntegrationTest]
    public async Task ValidateSecretAsync_ExpiredOrPublicClient_ReturnsNull()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateOAuthClientStore();
        var expiring = await store.CreateAsync(Registration(OAuthClientType.Confidential), CancellationToken.None);
        var publicClient = await store.CreateAsync(Registration(OAuthClientType.Public), CancellationToken.None);
        await harness.ExecuteAsync(
            $"UPDATE {harness.Table("oauth_clients")} SET expires_at = now() - interval '1 second' WHERE id = @id",
            new NpgsqlParameter("id", expiring.Record.Id));

        (await store.ValidateSecretAsync(expiring.Record.ClientId, expiring.Secret!, CancellationToken.None)).Should().BeNull();
        publicClient.Secret.Should().BeNull();
        (await store.ValidateSecretAsync(publicClient.Record.ClientId, "secret_anything", CancellationToken.None)).Should().BeNull();
    }

    [IntegrationTest]
    public async Task ListAsync_ReturnsClientsOldestFirst()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateOAuthClientStore();
        var first = await store.CreateAsync(Registration(OAuthClientType.Confidential), CancellationToken.None);
        var second = await store.CreateAsync(Registration(OAuthClientType.Public), CancellationToken.None);
        await harness.ExecuteAsync(
            $"UPDATE {harness.Table("oauth_clients")} SET created_at = now() - interval '1 hour' WHERE id = @id",
            new NpgsqlParameter("id", first.Record.Id));

        (await store.ListAsync(CancellationToken.None)).Select(client => client.Id).Should().Equal(first.Record.Id, second.Record.Id);
    }

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresOAuthClientStoreTests));

    private static OAuthClientRegistration Registration(OAuthClientType clientType)
        => new(
            Name: "integration",
            ClientType: clientType,
            AllowedGrantTypes: ["client_credentials"],
            RedirectUris: [],
            AllowedScopes: ["read"],
            ExpiresAt: null,
            CreatedBy: "operator");
}

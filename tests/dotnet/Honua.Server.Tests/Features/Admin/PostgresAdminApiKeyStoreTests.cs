// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Admin.Services;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Two independently constructed stores over one PostgreSQL schema stand in for two Lambda
/// execution environments that have no Redis (honua-server#6487, #6490). A key minted through one
/// must be known to the other, and revocation and rotation must take effect across both.
/// </summary>
[Collection("Database")]
[SecurityTest]
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.Security)]
public sealed class PostgresAdminApiKeyStoreTests(DatabaseFixtureAdapter database) : IAsyncLifetime
{
    private readonly string _schema = "adminkeys6487_" + Guid.NewGuid().ToString("N");

    public async Task InitializeAsync()
    {
        await database.CreateSchemaUnderLockAsync(_schema);
        await using var stream = typeof(PostgresAdminApiKeyStore).Assembly.GetManifestResourceStream(
            "Honua.Server.Migrations.136_CreateAdminApiKeys.sql")
            ?? throw new InvalidOperationException("Admin API key migration resource is missing.");
        using var reader = new StreamReader(stream);
        var migration = await reader.ReadToEndAsync();
        await database.ExecuteDdlUnderLockAsync(migration.Replace("$HonuaSchema$", _schema, StringComparison.Ordinal));
    }

    public Task DisposeAsync() => database.DropSchemaAsync(_schema);

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/api-keys")]
    [Endpoint("POST /api/v1/admin/api-keys/{id}/rotate")]
    [Endpoint("POST /api/v1/admin/api-keys/{id}/revoke")]
    public async Task KeyMintedOnOneInstance_AuthenticatesOnAnother_AndRevocationAndRotationApplyEverywhere()
    {
        var instanceA = new PostgresAdminApiKeyStore(database.DataSource, TimeProvider.System, _schema);
        var instanceB = new PostgresAdminApiKeyStore(database.DataSource, TimeProvider.System, _schema);

        var created = await instanceA.CreateAsync(
            "journey-operator", ["operations:propose"], DateTimeOffset.UtcNow.AddHours(1), "bootstrap", CancellationToken.None);

        // The other instance knows the key: validation, lookup and listing all see it.
        (await instanceB.ValidateAsync(created.Key, CancellationToken.None))!.Record.Id.Should().Be(created.Record.Id);
        (await instanceB.GetAsync(created.Record.Id, CancellationToken.None))!.Permissions.Should().Equal("operations:propose");
        (await instanceB.ListAsync(CancellationToken.None)).Select(key => key.Id).Should().Contain(created.Record.Id);
        (await instanceB.ValidateAsync("hnua_not-a-minted-key", CancellationToken.None)).Should().BeNull();

        // Rotation on B retires the old secret on A.
        var rotated = await instanceB.RotateAsync(created.Record.Id, CancellationToken.None);
        rotated.Should().NotBeNull();
        (await instanceA.ValidateAsync(created.Key, CancellationToken.None)).Should().BeNull();
        (await instanceA.ValidateAsync(rotated!.Key, CancellationToken.None)).Should().NotBeNull();

        // Revocation on A denies the new secret on B, and a revoked key cannot be rotated.
        var revoked = await instanceA.RevokeAsync(created.Record.Id, CancellationToken.None);
        revoked!.RevokedAt.Should().NotBeNull();
        (await instanceB.ValidateAsync(rotated.Key, CancellationToken.None)).Should().BeNull();
        (await instanceB.RotateAsync(created.Record.Id, CancellationToken.None)).Should().BeNull();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/api-keys")]
    public async Task ExpiredKey_IsRejected_AndPermissionsDefaultToAdminWildcard()
    {
        var store = new PostgresAdminApiKeyStore(database.DataSource, TimeProvider.System, _schema);
        var expired = await store.CreateAsync("old", ["admin:*"], DateTimeOffset.UtcNow.AddMinutes(-1), null, CancellationToken.None);
        (await store.ValidateAsync(expired.Key, CancellationToken.None)).Should().BeNull();

        var defaults = await store.CreateAsync("defaults", [], null, null, CancellationToken.None);
        defaults.Record.Permissions.Should().Equal("admin:*");
        (await store.RotateAsync(expired.Record.Id, CancellationToken.None)).Should().BeNull("an expired key cannot be rotated");
    }
}

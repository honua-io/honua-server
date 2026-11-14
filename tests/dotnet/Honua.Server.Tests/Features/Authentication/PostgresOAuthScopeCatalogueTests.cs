// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Infrastructure.Authentication;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Authentication;

[Collection("Database")]
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.Security)]
public sealed class PostgresOAuthScopeCatalogueTests(DatabaseFixtureAdapter database) : IAsyncLifetime
{
    private readonly string _schema = "oauth_scopes_" + Guid.NewGuid().ToString("N");

    public async Task InitializeAsync()
    {
        await database.CreateSchemaUnderLockAsync(_schema);
        await using var stream = typeof(PostgresOAuthScopeCatalogue).Assembly.GetManifestResourceStream(
            "Honua.Server.Migrations.165_CreateOAuthScopeCatalogue.sql")
            ?? throw new InvalidOperationException("OAuth catalogue migration is missing.");
        using var reader = new StreamReader(stream);
        await database.ExecuteDdlUnderLockAsync((await reader.ReadToEndAsync())
            .Replace("$HonuaSchema$", _schema, StringComparison.Ordinal));
    }

    public Task DisposeAsync() => database.DropSchemaAsync(_schema);

    [IntegrationTest]
    public async Task DefineAsync_RecreatedPool_PreservesNormalizationAndCaseInsensitiveLookup()
    {
        await using (var pool = NpgsqlDataSource.Create(database.ConnectionString))
        {
            var writer = new PostgresOAuthScopeCatalogue(new JobStoreDatabase(pool, _schema));
            await writer.DefineAsync(new("  Read  ", "  Read layers  ", [" layer:read ", "LAYER:READ", " "]), CancellationToken.None);
        }
        var reader = Store();
        (await reader.GetAsync(" READ ", CancellationToken.None)).Should().BeEquivalentTo(
            new OAuthScopeDefinition("Read", "Read layers", ["layer:read"]));
        await reader.DefineAsync(new("read", "updated", ["metadata:read"]), CancellationToken.None);
        (await reader.ListAsync(CancellationToken.None)).Should().ContainSingle();
        (await reader.GetAsync("Read", CancellationToken.None))!.Description.Should().Be("updated");
    }

    [IntegrationTest]
    public async Task ResolveGrantAsync_CrossNodeScopeChanges_NeverGrantsUndefinedOrDisallowedScopes()
    {
        var writer = Store();
        var reader = Store();
        await writer.DefineAsync(new("Read", "", ["layer:read"]), CancellationToken.None);
        await writer.DefineAsync(new("Write", "", ["layer:write"]), CancellationToken.None);
        var grant = await reader.ResolveGrantAsync("READ Write missing Read", ["read", "missing"], CancellationToken.None);
        grant.GrantedScopes.Should().Equal("Read");
        grant.GrantedPermissions.Should().Equal("layer:read");
        var defaults = await reader.ResolveGrantAsync(null, ["read", "missing"], CancellationToken.None);
        defaults.Should().BeEquivalentTo(grant);
        var empty = await reader.ResolveGrantAsync(null, [], CancellationToken.None);
        empty.GrantedScopes.Should().BeEmpty();
        empty.GrantedPermissions.Should().BeEmpty();
        (await writer.DeleteAsync("READ", CancellationToken.None))!.Scope.Should().Be("Read");
        (await reader.ResolveGrantAsync(null, ["read"], CancellationToken.None)).GrantedPermissions.Should().BeEmpty();
        (await reader.DeleteAsync("Read", CancellationToken.None)).Should().BeNull();
    }

    [IntegrationTest]
    public async Task DefineAsync_ConcurrentCaseVariants_KeepOneDefinition()
    {
        var first = Store();
        var second = Store();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(index => (index % 2 == 0 ? first : second)
            .DefineAsync(new(index % 2 == 0 ? "Read" : "READ", "description", ["layer:read"]), CancellationToken.None)));
        (await first.ListAsync(CancellationToken.None)).Should().ContainSingle();
        (await second.GetAsync("read", CancellationToken.None))!.Permissions.Should().Equal("layer:read");
    }

    [IntegrationTest]
    public async Task GetAndDeleteAsync_BlankScope_ReturnNull()
    {
        var store = Store();
        (await store.GetAsync(" ", CancellationToken.None)).Should().BeNull();
        (await store.DeleteAsync(" ", CancellationToken.None)).Should().BeNull();
    }

    private PostgresOAuthScopeCatalogue Store() => new(new JobStoreDatabase(database.DataSource, _schema));
}

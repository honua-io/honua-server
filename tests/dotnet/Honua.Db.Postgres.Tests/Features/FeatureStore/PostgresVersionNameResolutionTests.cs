// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.TestKit;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

/// <summary>Exercises real persisted version-name resolution alongside the persisted DEFAULT identity.</summary>
[Collection("Database")]
public sealed class PostgresVersionNameResolutionTests(PostgresFixture fixture) : IAsyncLifetime
{
    private string? _databaseName;
    private string _connectionString = null!;
    private PostgresVersionManager Manager => new(new ConnectionProvider(_connectionString));

    public async Task InitializeAsync()
    {
        var name = "version_name_" + Guid.NewGuid().ToString("N");
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\";", connection))
        {
            await command.ExecuteNonQueryAsync();
            _databaseName = name;
        }
        _connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Database = name,
            Pooling = false
        }.ConnectionString;
        await ExecuteAsync("CREATE SCHEMA honua; CREATE SEQUENCE honua.sync_generation;");
        var assembly = typeof(Program).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(item => item.EndsWith("047_CreateGdbVersions.sql", StringComparison.Ordinal));
        await using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        await ExecuteAsync(await reader.ReadToEndAsync());
        var associationResource = assembly.GetManifestResourceNames().Single(name => name.EndsWith("_AddVersionServiceAssociation.sql", StringComparison.Ordinal));
        await using var associationStream = assembly.GetManifestResourceStream(associationResource)!;
        using var associationReader = new StreamReader(associationStream);
        await ExecuteAsync(await associationReader.ReadToEndAsync());
        var identityResource = assembly.GetManifestResourceNames().Single(item => item.EndsWith("_CreateDefaultVersionIdentity.sql", StringComparison.Ordinal));
        await using var identityStream = assembly.GetManifestResourceStream(identityResource)!;
        using var identityReader = new StreamReader(identityStream);
        await ExecuteAsync(await identityReader.ReadToEndAsync());
    }

    public async Task DisposeAsync()
    {
        if (_databaseName is null)
        {
            return;
        }
        // Only the GUID-named database successfully created by this instance is removed.
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE \"{_databaseName}\" WITH (FORCE);", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task CreatePlainName_ResolvesExactCaseInsensitiveQualifiedAndGuidForms()
    {
        var created = await Manager.CreateAsync(new CreateVersionRequest("PlainName", "alice", VersionAccess.Private));
        foreach (var identity in new[] { "PlainName", "PLAINNAME", "alice.PlainName", "ALICE.PLAINNAME", created.VersionId.ToString("D"), created.VersionId.ToString("B") })
        {
            await AssertResolvesAsync(identity, created);
        }
    }

    [Theory]
    [InlineData("alice.QualifiedName")]
    [InlineData("alice.namespace.QualifiedName")]
    [InlineData("quoted'name")]
    public async Task CreateLiteralName_ResolvesActualOwnerQualifiedIdentity(string name)
    {
        var created = await Manager.CreateAsync(new CreateVersionRequest(name, "alice", VersionAccess.Protected));
        await AssertResolvesAsync("alice." + name, created);
        await AssertResolvesAsync(("alice." + name).ToUpperInvariant(), created);
        await AssertResolvesAsync(created.VersionId.ToString("D"), created);
        (await Manager.GetVersionAsync(created.VersionId)).Should().Be(created);
    }

    [Fact]
    public async Task RawDottedName_CannotShadowAnotherOwnersQualifiedIdentity()
    {
        var alice = await Manager.CreateAsync(new CreateVersionRequest("Review", "alice", VersionAccess.Private));
        var bob = await Manager.CreateAsync(new CreateVersionRequest("alice.Review", "bob", VersionAccess.Public));
        await AssertResolvesAsync("alice.Review", alice);
        await AssertResolvesAsync("bob.alice.Review", bob);
        await AssertResolvesAsync(bob.VersionId.ToString("D"), bob);
        (await Manager.GetVersionAsync(bob.VersionId))!.Value.Owner.Should().Be("bob");
        (await Manager.GetVersionAsync(bob.VersionId))!.Value.VersionName.Should().Be("alice.Review");
    }

    [Fact]
    public async Task RenameToAnotherOwnersQualifiedName_DoesNotRetargetThatOwner()
    {
        var alice = await Manager.CreateAsync(new CreateVersionRequest("Review", "alice", VersionAccess.Private));
        var bob = await Manager.CreateAsync(new CreateVersionRequest("Before", "bob", VersionAccess.Public));
        var changed = (await Manager.AlterAsync(new AlterVersionRequest(bob.VersionId, VersionName: "alice.Review")))!.Value;
        await AssertResolvesAsync("alice.Review", alice);
        await AssertResolvesAsync("bob.alice.Review", changed);
        (await Manager.ResolveAsync("bob.Before")).Should().BeNull();
    }

    [Fact]
    public async Task AmbiguousCanonicalIdentity_FailsClosedWhileGuidRemainsAuthoritative()
    {
        var first = await Manager.CreateAsync(new CreateVersionRequest("team.Review", "alice", VersionAccess.Private));
        var second = await Manager.CreateAsync(new CreateVersionRequest("Review", "alice.team", VersionAccess.Public));
        (await Manager.ResolveAsync("alice.team.Review")).Should().BeNull();
        (await Manager.ResolveAsync("ALICE.TEAM.REVIEW")).Should().BeNull();
        await AssertResolvesAsync(first.VersionId.ToString("D"), first);
        await AssertResolvesAsync(second.VersionId.ToString("D"), second);
    }

    [Fact]
    public async Task DottedRawAlias_IsNotAcceptedEvenBeforeTheOtherOwnerCreatesItsVersion()
    {
        var bob = await Manager.CreateAsync(new CreateVersionRequest("alice.Review", "bob", VersionAccess.Public));
        (await Manager.ResolveAsync("alice.Review")).Should().BeNull();
        await AssertResolvesAsync("bob.alice.Review", bob);
    }

    [Fact]
    public async Task OwnerQualifiedFallback_DisambiguatesSameNameAndRejectsOtherOwner()
    {
        var alice = await Manager.CreateAsync(new CreateVersionRequest("SharedName", "alice", VersionAccess.Private));
        var bob = await Manager.CreateAsync(new CreateVersionRequest("SharedName", "bob", VersionAccess.Public));
        await AssertResolvesAsync("alice.SharedName", alice);
        await AssertResolvesAsync("bob.SharedName", bob);
        (await Manager.ResolveAsync("SharedName")).Should().BeNull();
        (await Manager.ResolveAsync("mallory.SharedName")).Should().BeNull();
    }

    [Fact]
    public async Task DeletedVersion_IsExcludedFromEveryIdentityForm()
    {
        var created = await Manager.CreateAsync(new CreateVersionRequest("DeletedName", "alice", VersionAccess.Private));
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE honua.gdb_versions SET state=3 WHERE version_id=@id", connection);
        command.Parameters.AddWithValue("id", created.VersionId);
        await command.ExecuteNonQueryAsync();
        foreach (var identity in new[] { "DeletedName", "alice.DeletedName", created.VersionId.ToString("D") })
        {
            (await Manager.ResolveAsync(identity)).Should().BeNull();
        }
        (await Manager.GetVersionAsync(created.VersionId)).Should().BeNull();
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("alice.unknown")]
    [InlineData("e503e98d-00f6-48ec-a583-184c0202c7e7")]
    public async Task UnknownIdentity_ReturnsNullWithoutPostgresParameterError(string identity)
    {
        (await Manager.ResolveAsync(identity)).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("SDE.DEFAULT")]
    [InlineData(" sde.default ")]
    public async Task DefaultSentinel_RetainsImplicitNullOverlay(string? identity)
    {
        var resolved = (await Manager.ResolveAsync(identity))!.Value;
        resolved.IsDefault.Should().BeTrue();
        resolved.VersionId.Should().BeNull();
        (await Manager.ListAsync()).Should().BeEmpty();
    }

    private async Task AssertResolvesAsync(string identity, GdbVersion expected)
    {
        var resolved = await Manager.ResolveAsync(identity);
        resolved.Should().NotBeNull(identity);
        resolved!.Value.VersionId.Should().Be(expected.VersionId);
        resolved.Value.IsDefault.Should().BeFalse();
        var stored = await Manager.GetVersionAsync(resolved.Value.VersionId!.Value);
        stored.Should().Be(expected);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class ConnectionProvider(string connectionString) : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString() => connectionString;
        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead, CancellationToken cancellationToken = default)
        {
            var connection = await OpenConnectionAsync(cancellationToken);
            return (connection, await connection.BeginTransactionAsync(isolationLevel, cancellationToken));
        }
        public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default) => operation();
        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default) => operation();
    }
}

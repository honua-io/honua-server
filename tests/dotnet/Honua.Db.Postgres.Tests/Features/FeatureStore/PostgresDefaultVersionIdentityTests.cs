// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.ReadOnlyProviders;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Honua.TestKit;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

/// <summary>Real PostgreSQL persistence, resolution and immutable DEFAULT target regressions.</summary>
[Collection("Database")]
public sealed class PostgresDefaultVersionIdentityTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly List<string> _ownedDatabases = [];
    private string _connectionString = null!;
    private string _identityMigration = null!;
    private PostgresVersionManager Manager => new(new ConnectionProvider(_connectionString));

    public async Task InitializeAsync()
    {
        var assembly = typeof(Program).Assembly;
        var identityResource = assembly.GetManifestResourceNames().Single(name =>
            name.EndsWith("_CreateDefaultVersionIdentity.sql", StringComparison.Ordinal));
        await using var stream = assembly.GetManifestResourceStream(identityResource)!;
        using var reader = new StreamReader(stream);
        _identityMigration = await reader.ReadToEndAsync();
        _connectionString = await CreateOwnedDatabaseAsync();
        await ExecuteAsync(_connectionString, "CREATE SCHEMA honua;");
        await ExecuteAsync(_connectionString, _identityMigration);
        var registryResource = assembly.GetManifestResourceNames().Single(name =>
            name.EndsWith("047_CreateGdbVersions.sql", StringComparison.Ordinal));
        await using var registryStream = assembly.GetManifestResourceStream(registryResource)!;
        using var registryReader = new StreamReader(registryStream);
        await ExecuteAsync(_connectionString, await registryReader.ReadToEndAsync());
        await ExecuteAsync(_connectionString, "CREATE SEQUENCE honua.sync_generation;");
        var associationResource = assembly.GetManifestResourceNames().Single(name => name.EndsWith("_AddVersionServiceAssociation.sql", StringComparison.Ordinal));
        await using var associationStream = assembly.GetManifestResourceStream(associationResource)!;
        using var associationReader = new StreamReader(associationStream);
        await ExecuteAsync(_connectionString, await associationReader.ReadToEndAsync());
    }

    public async Task DisposeAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        foreach (var database in _ownedDatabases)
        {
            // Only names allocated by this instance enter the list; no user database is targeted.
            await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE);", connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Migration_RerunAndConcurrentStartupPreserveOneIdentity_SeparateStoreDiffers()
    {
        var first = (await Manager.GetDefaultVersionIdentityAsync())!.Value;
        first.VersionId.Should().NotBe(Guid.Empty);
        first.Kind.Should().Be(VersionIdentityKind.Default);
        first.Access.Should().Be(VersionAccess.Public);
        first.DisplayOwner.Should().Be("sde");
        await Task.WhenAll(ExecuteAsync(_connectionString, _identityMigration), ExecuteAsync(_connectionString, _identityMigration));
        (await Manager.GetDefaultVersionIdentityAsync()).Should().Be(first);
        (await ScalarAsync(_connectionString, "SELECT count(*) FROM honua.gdb_version_store_identity;")).Should().Be("1");
        (await ScalarAsync(_connectionString, "SELECT count(*) FROM honua.gdb_versions;")).Should().Be("0");
        (await ScalarAsync(_connectionString, "SELECT last_value FROM honua.sync_generation;")).Should().Be("1");

        var secondConnection = await CreateOwnedDatabaseAsync();
        await ExecuteAsync(secondConnection, "CREATE SCHEMA honua;");
        await Task.WhenAll(ExecuteAsync(secondConnection, _identityMigration), ExecuteAsync(secondConnection, _identityMigration));
        var second = await new PostgresVersionManager(new ConnectionProvider(secondConnection)).GetDefaultVersionIdentityAsync();
        second!.Value.VersionId.Should().NotBe(first.VersionId);
        (await ScalarAsync(secondConnection, "SELECT count(*) FROM honua.gdb_version_store_identity;")).Should().Be("1");
    }

    [Theory]
    [InlineData("name")]
    [InlineData("bare")]
    [InlineData("guid")]
    [InlineData("braces")]
    [InlineData("absent")]
    public async Task Resolve_DurableDefaultIdentityUsesNullOverlayContext(string form)
    {
        var identity = (await Manager.GetDefaultVersionIdentityAsync())!.Value;
        var requested = form switch
        {
            "name" => "SDE.DEFAULT",
            "bare" => "DEFAULT",
            "guid" => identity.VersionId.ToString(),
            "braces" => identity.VersionId.ToString("B"),
            _ => null
        };
        var resolved = (await Manager.ResolveAsync(requested))!.Value;
        resolved.IsDefault.Should().BeTrue();
        resolved.VersionId.Should().BeNull();
        (await Manager.ResolveAsync(Guid.NewGuid().ToString())).Should().BeNull();
        (await Manager.ListAsync()).Should().BeEmpty("the canonical branch-only registry remains branch-only");
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("alter")]
    [InlineData("reconcile")]
    [InlineData("post")]
    [InlineData("resolve-conflicts")]
    [InlineData("inspect-conflicts")]
    public async Task Default_CannotBecomeABranchLifecycleTarget(string operation)
    {
        var manager = Manager;
        var identity = (await manager.GetDefaultVersionIdentityAsync())!.Value;
        Func<Task> action = operation switch
        {
            "delete" => async () => { await manager.DeleteAsync(identity.VersionId); },
            "alter" => async () => { await manager.AlterAsync(new AlterVersionRequest(identity.VersionId, "renamed")); },
            "reconcile" => async () => { await manager.ReconcileAsync(identity.VersionId); },
            "post" => async () => { await manager.PostAsync(identity.VersionId); },
            "resolve-conflicts" => async () => { await manager.ResolveConflictsAsync(identity.VersionId, []); },
            _ => async () => { await manager.GetPendingConflictsAsync(identity.VersionId); }
        };
        await action.Should().ThrowAsync<ArgumentException>().WithMessage("*DEFAULT*system-managed*");
        (await manager.GetDefaultVersionIdentityAsync()).Should().Be(identity);
        (await manager.ListAsync()).Should().BeEmpty();
        (await ScalarAsync(_connectionString, "SELECT last_value FROM honua.sync_generation;")).Should().Be("1");
    }

    [Fact]
    public async Task CanonicalOwnerAndLiteralNameRemainAuthoritative_WithoutRawDottedHijack()
    {
        var alice = await Manager.CreateAsync(new CreateVersionRequest("branch", "alice", VersionAccess.Private));
        var bob = await Manager.CreateAsync(new CreateVersionRequest("alice.branch", "bob", VersionAccess.Public));
        (await Manager.ResolveAsync("alice.branch"))!.Value.VersionId.Should().Be(alice.VersionId);
        (await Manager.ResolveAsync("bob.alice.branch"))!.Value.VersionId.Should().Be(bob.VersionId);
        (await Manager.ResolveAsync(bob.VersionId.ToString()))!.Value.VersionId.Should().Be(bob.VersionId);
        var second = await Manager.CreateAsync(new CreateVersionRequest("branch", "alice.with.dot", VersionAccess.Public));
        await Manager.CreateAsync(new CreateVersionRequest("with.dot.branch", "alice", VersionAccess.Public));
        (await Manager.ResolveAsync("alice.with.dot.branch")).Should().BeNull();
        (await Manager.ResolveAsync(second.VersionId.ToString()))!.Value.VersionId.Should().Be(second.VersionId);
    }

    [Fact]
    public async Task LegacyReservedCollisionFailsClosedByName_BothDurableGuidsRemainUnambiguous()
    {
        var identity = (await Manager.GetDefaultVersionIdentityAsync())!.Value;
        await ExecuteAsync(_connectionString, """
            INSERT INTO honua.gdb_versions(version_name, owner, access, state, common_ancestor_gen, branch_gen)
            VALUES ('DEFAULT', 'sde', 0, 0, 1, 1);
            """);
        var legacy = (await Manager.ListAsync()).Single();
        (await Manager.ResolveAsync("sde.DEFAULT")).Should().BeNull();
        (await Manager.ResolveAsync("DEFAULT")).Should().BeNull();
        (await Manager.ResolveAsync(identity.VersionId.ToString()))!.Value.Should().Be(VersionContext.Default);
        (await Manager.ResolveAsync(legacy.VersionId.ToString()))!.Value.VersionId.Should().Be(legacy.VersionId);
        legacy.Owner.Should().Be("sde");
        legacy.VersionName.Should().Be("DEFAULT");
        await Manager.DeleteAsync(legacy.VersionId);
        (await Manager.ResolveAsync("sde.DEFAULT"))!.Value.Should().Be(VersionContext.Default);
    }

    [Theory]
    [InlineData("DEFAULT")]
    [InlineData("sDe.DeFaUlT")]
    public async Task DefaultParentGuidNormalizesToNull_ReservedDefaultNameCannotBeCreatedOrAssigned(string reserved)
    {
        var identity = (await Manager.GetDefaultVersionIdentityAsync())!.Value;
        var created = await Manager.CreateAsync(new CreateVersionRequest("child", "sde", VersionAccess.Private, identity.VersionId));
        created.ParentVersion.Should().BeNull();
        Func<Task> create = async () => { await Manager.CreateAsync(new CreateVersionRequest(reserved, "arbitrary-owner", VersionAccess.Public)); };
        Func<Task> alter = async () => { await Manager.AlterAsync(new AlterVersionRequest(created.VersionId, reserved)); };
        await create.Should().ThrowAsync<ArgumentException>().WithMessage("*reserved*");
        await alter.Should().ThrowAsync<ArgumentException>().WithMessage("*reserved*");
        (await Manager.ListAsync()).Should().ContainSingle().Which.VersionName.Should().Be("child");
        (await Manager.GetDefaultVersionIdentityAsync()).Should().Be(identity);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Create_WithParentGuidCompletesUsingSingleConnectionPool(bool defaultParent)
    {
        var identity = (await Manager.GetDefaultVersionIdentityAsync())!.Value;
        var branch = await Manager.CreateAsync(new CreateVersionRequest("parent", "alice", VersionAccess.Private));
        var parentId = defaultParent ? identity.VersionId : branch.VersionId;
        var pooledConnectionString = new NpgsqlConnectionStringBuilder(_connectionString)
        {
            Pooling = true,
            MinPoolSize = 0,
            MaxPoolSize = 1,
            Timeout = 3,
            ApplicationName = "default-parent-pool-" + Guid.NewGuid().ToString("N")
        }.ConnectionString;
        using var poolKey = new NpgsqlConnection(pooledConnectionString);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var manager = new PostgresVersionManager(new ConnectionProvider(pooledConnectionString));
            // The old ordering occupies the only pooled connection and times out acquiring
            // another for identity lookup. Both real parent forms must complete and persist.
            var child = await manager.CreateAsync(
                new CreateVersionRequest("child", "alice", VersionAccess.Private, parentId), timeout.Token);
            var persisted = (await manager.GetVersionAsync(child.VersionId, timeout.Token))!.Value;
            Guid? expectedParent = defaultParent ? null : branch.VersionId;
            persisted.ParentVersion.Should().Be(expectedParent);
            child.ParentVersion.Should().Be(persisted.ParentVersion);
            (await manager.GetDefaultVersionIdentityAsync(timeout.Token)).Should().Be(identity);
            (await manager.ListAsync(timeout.Token)).Should().HaveCount(2);
        }
        finally
        {
            // Clear only this test's uniquely named pool, never the fixture/global pools.
            NpgsqlConnection.ClearPool(poolKey);
        }
    }

    [Fact]
    public async Task MissingIdentityFailsExplicitly_UnsupportedProviderDoesNotInventOne()
    {
        await ExecuteAsync(_connectionString, "DELETE FROM honua.gdb_version_store_identity;");
        Func<Task> read = async () => { await Manager.GetDefaultVersionIdentityAsync(); };
        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*identity is missing*");
        (await new NoOpVersionManager().GetDefaultVersionIdentityAsync()).Should().BeNull();
    }

    [Theory]
    [InlineData("missing-table")]
    [InlineData("missing-row")]
    [InlineData("wrong-column")]
    public async Task SchemaGuard_JournalCannotConcealMissingPhysicalIdentity(string fault)
    {
        const string identityMigration = "Honua.Server.Migrations.fixture_CreateDefaultVersionIdentity.sql";
        await ExecuteAsync(_connectionString,
            $"CREATE TABLE public.schema_versions (scriptname text NOT NULL); INSERT INTO public.schema_versions VALUES ('{identityMigration}');");
        var guard = IdentityGuard(identityMigration);
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await guard.VerifyConsistencyAsync(connection);
        await ExecuteAsync(_connectionString, fault switch
        {
            "missing-table" => "DROP TABLE honua.gdb_version_store_identity;",
            "wrong-column" => "ALTER TABLE honua.gdb_version_store_identity RENAME COLUMN version_id TO missing_version_id;",
            _ => "DELETE FROM honua.gdb_version_store_identity;"
        });
        Func<Task> verify = () => guard.VerifyAsync(connection);
        var failure = (await verify.Should().ThrowAsync<DatabaseSchemaFloorException>()).Which;
        failure.MigrationScript.Should().Be(identityMigration);
        failure.FailureKind.Should().Be(DatabaseSchemaFloorFailureKind.JournalClaimsMissingSchema);
    }

    [Fact]
    public async Task SchemaGuard_DistinguishesPendingMigrationFromUnjournaledIdentity()
    {
        const string identityMigration = "Honua.Server.Migrations.fixture_CreateDefaultVersionIdentity.sql";
        var guard = IdentityGuard(identityMigration);
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        Func<Task> verify = () => guard.VerifyConsistencyAsync(connection);
        var failure = (await verify.Should().ThrowAsync<DatabaseSchemaFloorException>()).Which;
        failure.FailureKind.Should().Be(DatabaseSchemaFloorFailureKind.SchemaExistsWithoutJournal);
        await ExecuteAsync(_connectionString, "DROP TABLE honua.gdb_version_store_identity;");
        await guard.VerifyConsistencyAsync(connection);
        Func<Task> ready = () => guard.VerifyAsync(connection);
        (await ready.Should().ThrowAsync<DatabaseSchemaFloorException>()).Which.FailureKind
            .Should().Be(DatabaseSchemaFloorFailureKind.MigrationNotApplied);
    }

    [Theory]
    [InlineData("missing-column")]
    [InlineData("wrong-type")]
    [InlineData("not-null")]
    public async Task ServiceAssociationGuard_JournalCannotConcealMissingOrInvalidColumn(string fault)
    {
        const string migration = "Honua.Server.Migrations.fixture_AddVersionServiceAssociation.sql";
        await ExecuteAsync(_connectionString,
            $"CREATE TABLE public.schema_versions (scriptname text NOT NULL); INSERT INTO public.schema_versions VALUES ('{migration}');");
        var guard = AssociationGuard(migration);
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await guard.VerifyConsistencyAsync(connection);
        await ExecuteAsync(_connectionString, fault switch
        {
            "missing-column" => "ALTER TABLE honua.gdb_versions DROP COLUMN service_id;",
            "wrong-type" => "ALTER TABLE honua.gdb_versions ALTER COLUMN service_id TYPE integer USING NULL::integer;",
            _ => "ALTER TABLE honua.gdb_versions ALTER COLUMN service_id SET NOT NULL;"
        });
        Func<Task> verify = () => guard.VerifyAsync(connection);
        var failure = (await verify.Should().ThrowAsync<DatabaseSchemaFloorException>()).Which;
        failure.MigrationScript.Should().Be(migration);
        failure.FailureKind.Should().Be(DatabaseSchemaFloorFailureKind.JournalClaimsMissingSchema);
    }

    [Fact]
    public async Task ServiceAssociationGuard_DistinguishesPendingMigrationAndPreservesLegacyRow()
    {
        const string migration = "Honua.Server.Migrations.fixture_AddVersionServiceAssociation.sql";
        var branch = await Manager.CreateAsync(new CreateVersionRequest("LegacyMigration", "alice", VersionAccess.Public));
        var guard = AssociationGuard(migration);
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        Func<Task> consistency = () => guard.VerifyConsistencyAsync(connection);
        (await consistency.Should().ThrowAsync<DatabaseSchemaFloorException>()).Which.FailureKind
            .Should().Be(DatabaseSchemaFloorFailureKind.SchemaExistsWithoutJournal);
        await ExecuteAsync(_connectionString, "ALTER TABLE honua.gdb_versions DROP COLUMN service_id;");
        await guard.VerifyConsistencyAsync(connection);
        Func<Task> ready = () => guard.VerifyAsync(connection);
        (await ready.Should().ThrowAsync<DatabaseSchemaFloorException>()).Which.FailureKind
            .Should().Be(DatabaseSchemaFloorFailureKind.MigrationNotApplied);
        var assembly = typeof(Program).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith("_AddVersionServiceAssociation.sql", StringComparison.Ordinal));
        await using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync();
        await ExecuteAsync(_connectionString, sql);
        await ExecuteAsync(_connectionString, sql);
        await ExecuteAsync(_connectionString,
            $"CREATE TABLE public.schema_versions (scriptname text NOT NULL); INSERT INTO public.schema_versions VALUES ('{migration}');");
        await guard.VerifyConsistencyAsync(connection);
        (await Manager.GetVersionAsync(branch.VersionId)).Should().Be(branch);
        (await Manager.GetVersionAsync(branch.VersionId))!.Value.ServiceId.Should().BeNull();
    }

    private static PostgresCoreSchemaGuard AssociationGuard(string migration) => new(
        new PostgresCoreSchemaMigrationManifest("Honua.Server", "fixture.metadata", "fixture.packages",
            "fixture.raster", "fixture.sensorthings", "fixture.overviews", "fixture.footprints",
            "fixture.adoption", "fixture.lineage", versionServiceAssociationMigration: migration));

    private static PostgresCoreSchemaGuard IdentityGuard(string identityMigration) => new(
        new PostgresCoreSchemaMigrationManifest("Honua.Server", "fixture.metadata", "fixture.packages",
            "fixture.raster", "fixture.sensorthings", "fixture.overviews", "fixture.footprints",
            "fixture.adoption", "fixture.lineage", defaultVersionIdentityMigration: identityMigration));

    private async Task<string> CreateOwnedDatabaseAsync()
    {
        var name = "default_identity_" + Guid.NewGuid().ToString("N");
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\";", connection);
        await command.ExecuteNonQueryAsync();
        _ownedDatabases.Add(name);
        return new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name, Pooling = false }.ConnectionString;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static async Task<string> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture)!;
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

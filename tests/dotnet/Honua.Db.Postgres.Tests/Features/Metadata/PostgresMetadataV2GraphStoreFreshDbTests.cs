// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Honua.Db.Postgres.Features.Metadata;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Metadata;

/// <summary>
/// Metadata v2 store integration coverage. Migration 031 is provisioned explicitly by this
/// test fixture for normal store scenarios. Missing-migration cases assert a typed schema-floor
/// failure and prove ordinary reads/writes never self-apply the migration (#3899).
/// </summary>
[Collection("Database")]
public sealed class PostgresMetadataV2GraphStoreFreshDbTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task GetCurrentAsync_WhenMigration031Missing_FailsWithTypedSchemaFloorError()
    {
        // Isolated schema that deliberately does NOT create the metadata_v2 tables —
        // exactly the fresh-DB shape that surfaced the 500.
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresMetadataV2GraphStoreFreshDbTests));
        try
        {
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var store = new PostgresMetadataV2GraphStore(
                provider,
                environment: "Test",
                schemaGuard: CreateGuard(schema),
                schemaName: schema);

            var act = async () => await store.GetCurrentAsync();
            var exception = await act.Should().ThrowAsync<DatabaseSchemaFloorException>();
            exception.Which.MigrationScript.Should().Be(PostgresCoreSchemaGuard.MetadataV2SnapshotMigration);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task SaveAsync_WhenMigration031Missing_FailsWithoutCreatingTables()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresMetadataV2GraphStoreFreshDbTests));
        try
        {
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var store = new PostgresMetadataV2GraphStore(
                provider,
                environment: "Test",
                schemaGuard: CreateGuard(schema),
                schemaName: schema);

            // Mirrors the publish path: no current snapshot exists, so start from an
            // empty graph and force the first write (null expectedEtag).
            var graph = new MetadataV2Graph
            {
                Environment = "Test",
                Revision = 1,
                GeneratedAt = DateTimeOffset.UtcNow,
                Resources =
                [
                    new MetadataV2Resource
                    {
                        Metadata = new MetadataV2ObjectMetadata { Id = "res-layer-9000", Name = "fresh-db-layer" },
                        Type = MetadataV2ResourceType.FeatureDataset,
                    },
                ],
            };

            var act = () => store.SaveAsync(graph, expectedEtag: null);
            await act.Should().ThrowAsync<DatabaseSchemaFloorException>();

            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT COUNT(*)::int
                FROM information_schema.tables
                WHERE table_schema = '{schema}'
                  AND table_name LIKE 'metadata_v2_%';
                """;
            var tableCount = (int)(await command.ExecuteScalarAsync())!;
            tableCount.Should().Be(0, "SaveAsync must not replay migration 031 or create partial schema");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ActivateRevisionAsync_RepointsToRetainedSnapshotWithoutAllocatingCopy()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresMetadataV2GraphStoreFreshDbTests));
        try
        {
            await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, schema);
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var store = new PostgresMetadataV2GraphStore(
                provider,
                environment: "Test",
                schemaGuard: FixtureBypassDatabaseSchemaGuard.Instance,
                schemaName: schema);
            var first = await store.SaveAsync(
                new MetadataV2Graph
                {
                    Environment = "Test",
                    Revision = 1,
                    GeneratedAt = DateTimeOffset.UtcNow,
                    Resources =
                    [
                        new MetadataV2Resource
                        {
                            Metadata = new MetadataV2ObjectMetadata { Id = "retained-first", Name = "first" },
                            Type = MetadataV2ResourceType.FeatureDataset,
                        },
                    ],
                },
                expectedEtag: null);
            var second = await store.SaveAsync(
                first.Graph with
                {
                    Revision = 2,
                    GeneratedAt = DateTimeOffset.UtcNow,
                    Resources =
                    [
                        new MetadataV2Resource
                        {
                            Metadata = new MetadataV2ObjectMetadata { Id = "retained-second", Name = "second" },
                            Type = MetadataV2ResourceType.FeatureDataset,
                        },
                    ],
                },
                first.Etag);

            // Bootstrap reconciliation can preserve an immutable retained snapshot while
            // clearing its derived sidecars. Activation must reconstruct those indexes
            // before making the retained revision current again.
            await using (var corruptConnection = await fixture.DataSource.OpenConnectionAsync())
            await using (var deleteCommand = corruptConnection.CreateCommand())
            {
                deleteCommand.CommandText = $"""
                    DELETE FROM "{schema}".metadata_v2_resources_idx
                     WHERE environment = 'Test' AND revision = {first.Revision};
                    """;
                await deleteCommand.ExecuteNonQueryAsync();
            }

            var activated = await store.ActivateRevisionAsync(first.Revision, second.Etag);

            activated.Revision.Should().Be(first.Revision);
            activated.Etag.Should().Be(first.Etag);
            activated.Graph.Resources.Should().ContainSingle(resource => resource.Metadata.Id == "retained-first");
            var current = await store.GetCurrentAsync();
            current.Revision.Should().Be(first.Revision);
            current.Etag.Should().Be(first.Etag);

            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*)::int FROM \"{schema}\".metadata_v2_snapshots WHERE environment = 'Test'";
            var snapshotCount = (int)(await command.ExecuteScalarAsync())!;
            snapshotCount.Should().Be(2, "activation must retain revision identity instead of copying the document");

            command.CommandText = $"""
                SELECT COUNT(*)::int FROM "{schema}".metadata_v2_resources_idx
                 WHERE environment = 'Test' AND revision = {first.Revision}
                   AND resource_id = 'retained-first';
                """;
            var resourceIndexCount = (int)(await command.ExecuteScalarAsync())!;
            resourceIndexCount.Should().Be(1, "activation must rebuild sidecars cleared for a retained revision");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task SaveAsync_OnBootstrapWithOrphanedServiceSidecarRows_ReconcilesInsteadOf23505()
    {
        // honua-server#1395: a shared/partially-written DB can carry orphaned
        // metadata_v2_services_idx rows with NO activated metadata_v2_current row. The
        // publish path then bootstraps from an empty graph and forces a first write at
        // revision 1; without reconciliation the new service-name insert collided with
        // the unique idx_metadata_v2_services_name and surfaced a raw Postgres 23505.
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresMetadataV2GraphStoreFreshDbTests));
        try
        {
            await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, schema);
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var store = new PostgresMetadataV2GraphStore(
                provider,
                environment: "Test",
                schemaGuard: FixtureBypassDatabaseSchemaGuard.Instance,
                schemaName: schema);

            // First, write a real snapshot at revision 1 so the schema + sidecar rows exist.
            var firstGraph = new MetadataV2Graph
            {
                Environment = "Test",
                Revision = 1,
                GeneratedAt = DateTimeOffset.UtcNow,
                Services =
                [
                    new MetadataV2Service
                    {
                        Metadata = new MetadataV2ObjectMetadata { Id = "svc-orphan", Name = "Shared Service" },
                        Protocols = ["ogc-api-features"],
                    },
                ],
            };
            await store.SaveAsync(firstGraph, expectedEtag: null);

            // Now corrupt the store into the inconsistent state from the issue: the
            // services sidecar rows remain at revision 1 but no current snapshot is
            // activated. (DELETE current only; the snapshot + sidecar rows survive.)
            await using (var corrupt = await fixture.DataSource.OpenConnectionAsync())
            {
                await using var cmd = corrupt.CreateCommand();
                cmd.CommandText = $"""
                    DELETE FROM "{schema}".metadata_v2_current WHERE environment = 'Test';
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            // A fresh store instance (no in-memory cache) bootstrapping from empty and
            // forcing a first write at revision 1 again — the colliding scenario.
            var freshStore = new PostgresMetadataV2GraphStore(
                provider,
                environment: "Test",
                schemaGuard: FixtureBypassDatabaseSchemaGuard.Instance,
                schemaName: schema);
            var bootstrapGraph = new MetadataV2Graph
            {
                Environment = "Test",
                Revision = 1,
                GeneratedAt = DateTimeOffset.UtcNow,
                Services =
                [
                    new MetadataV2Service
                    {
                        // Same case-insensitive name as the orphaned row.
                        Metadata = new MetadataV2ObjectMetadata { Id = "svc-new", Name = "shared service" },
                        Protocols = ["ogc-api-features"],
                    },
                ],
            };

            var act = () => freshStore.SaveAsync(bootstrapGraph, expectedEtag: null);

            await act.Should().NotThrowAsync(
                "bootstrap must clear stale environment sidecar rows instead of colliding with idx_metadata_v2_services_name");

            var current = await freshStore.GetCurrentAsync();
            current.Graph.Revision.Should().Be(2, "the orphan revision is immutable and must not be overwritten");
            current.Graph.Services.Should().ContainSingle(service => service.Metadata.Id == "svc-new");

            // Exactly one services row remains for the environment — the orphan was cleared.
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var countCmd = connection.CreateCommand();
            countCmd.CommandText = $"""
                SELECT COUNT(*)::int FROM "{schema}".metadata_v2_services_idx WHERE environment = 'Test';
                """;
            var servicesCount = (int)(await countCmd.ExecuteScalarAsync())!;
            servicesCount.Should().Be(1, "the orphaned sidecar row must be reconciled away on bootstrap");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task TransactionOutcomeObserver_DistinguishesCommittedAndAbortedTransactions()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresMetadataV2GraphStoreFreshDbTests));
        try
        {
            await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, schema);
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            await using var committedConnection = (NpgsqlConnection)await provider.OpenConnectionAsync();
            await using var committedTransaction = await committedConnection.BeginTransactionAsync();
            var committedId = await PostgresTransactionOutcomeObserver.CaptureTransactionIdAsync(
                committedConnection,
                committedTransaction,
                CancellationToken.None);
            await committedTransaction.CommitAsync(CancellationToken.None);

            await using var abortedConnection = (NpgsqlConnection)await provider.OpenConnectionAsync();
            await using var abortedTransaction = await abortedConnection.BeginTransactionAsync();
            var abortedId = await PostgresTransactionOutcomeObserver.CaptureTransactionIdAsync(
                abortedConnection,
                abortedTransaction,
                CancellationToken.None);
            await abortedTransaction.RollbackAsync(CancellationToken.None);

            (await PostgresTransactionOutcomeObserver.TryObserveCommitAsync(provider, committedId))
                .Should().BeTrue();
            (await PostgresTransactionOutcomeObserver.TryObserveCommitAsync(provider, abortedId))
                .Should().BeFalse();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task SaveAsync_WithCurrentAndOrphanNextRevision_AllocatesAboveOrphan()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresMetadataV2GraphStoreFreshDbTests));
        try
        {
            await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, schema);
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var store = new PostgresMetadataV2GraphStore(
                provider,
                environment: "Test",
                schemaGuard: FixtureBypassDatabaseSchemaGuard.Instance,
                schemaName: schema);
            var first = await store.SaveAsync(
                new MetadataV2Graph
                {
                    Environment = "Test",
                    Revision = 1,
                    GeneratedAt = DateTimeOffset.UtcNow,
                },
                expectedEtag: null);

            await using (var connection = await fixture.DataSource.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $$"""
                    INSERT INTO "{{schema}}".metadata_v2_snapshots
                        (environment, revision, schema_version, api_version, document, etag, generated_at)
                    SELECT environment, 2, schema_version, api_version,
                           jsonb_set(document, '{revision}', '2'::jsonb),
                           'orphan-revision-2', generated_at
                      FROM "{{schema}}".metadata_v2_snapshots
                     WHERE environment = 'Test' AND revision = 1;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var saved = await store.SaveAsync(
                first.Graph with { Revision = 2, GeneratedAt = DateTimeOffset.UtcNow },
                expectedEtag: first.Etag);

            saved.Graph.Revision.Should().Be(3);
            await using var verify = await fixture.DataSource.OpenConnectionAsync();
            await using var verifyCommand = verify.CreateCommand();
            verifyCommand.CommandText = $"""
                SELECT etag FROM "{schema}".metadata_v2_snapshots
                 WHERE environment = 'Test' AND revision = 2;
                """;
            (await verifyCommand.ExecuteScalarAsync()).Should().Be("orphan-revision-2");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static PostgresCoreSchemaGuard CreateGuard(string schema)
        => new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Schema"] = schema })
            .Build());

    private sealed class TestConnectionProvider(NpgsqlDataSource dataSource, string schemaName) : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString() => dataSource.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var conn = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SET search_path TO \"{schemaName}\", public;";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            return conn;
        }

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var conn = await OpenConnectionAsync(cancellationToken);
            try
            {
                var tx = await conn.BeginTransactionAsync(isolationLevel, cancellationToken);
                return (conn, tx);
            }
            catch
            {
                await conn.DisposeAsync();
                throw;
            }
        }

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default)
            => operation();

        public Task ExecuteWithDeadlockRetryAsync(
            Func<Task> operation,
            CancellationToken cancellationToken = default)
            => operation();
    }
}

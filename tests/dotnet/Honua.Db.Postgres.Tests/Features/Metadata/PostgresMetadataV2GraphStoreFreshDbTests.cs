// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
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
            exception.Which.MigrationScript.Should().Be(
                TestCoreSchemaMigrations.Manifest.MetadataV2SnapshotMigration);
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
    public async Task StageAsync_KeepsReadersOnCurrent_AndActivationNeverOverwritesANewerRevision()
    {
        // #4619: a protected metadata release stages its candidate as an immutable retained revision,
        // keeps canonical readers on the active revision until an ETag-conditional activation, and a
        // concurrent publish that lands first is reported as a typed conflict instead of overwritten.
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
            var prior = await store.SaveAsync(GraphWith("parcels"), expectedEtag: null);

            var staged = await store.StageAsync(GraphWith("parcels", "candidate-only"));

            staged.Revision.Should().Be(prior.Revision + 1, "staging allocates a store-owned revision above every retained snapshot");
            (await store.GetCurrentAsync()).Revision.Should().Be(prior.Revision, "a staged candidate is invisible to canonical readers");
            (await store.GetByRevisionAsync(staged.Revision))!.Etag.Should().Be(staged.Etag);
            (await CountAsync(schema, $"metadata_v2_resources_idx WHERE environment = 'Test' AND revision = {staged.Revision}"))
                .Should().Be(0, "staging must not publish derived lookup rows for the candidate");

            // A concurrent publish (for example another service's update) becomes current first.
            var concurrent = await store.SaveAsync(GraphWith("parcels", "roads"), prior.Etag);
            concurrent.Revision.Should().Be(staged.Revision + 1, "revision allocation never reuses a staged revision");

            var activate = () => store.ActivateRevisionAsync(staged.Revision, prior.Etag);
            var conflict = await activate.Should().ThrowAsync<MetadataV2GraphConcurrencyException>();
            conflict.Which.ExpectedEtag.Should().Be(prior.Etag);
            conflict.Which.ActualEtag.Should().Be(concurrent.Etag);
            var afterConflict = await store.GetCurrentAsync();
            afterConflict.Revision.Should().Be(concurrent.Revision, "a newer update is never overwritten");
            afterConflict.Graph.Resources.Select(resource => resource.Metadata.Id).Should().BeEquivalentTo(new[] { "parcels", "roads" });

            var discardCurrent = () => store.DiscardStagedAsync(concurrent.Revision);
            await discardCurrent.Should().ThrowAsync<InvalidOperationException>("the current revision is never discardable");
            (await store.DiscardStagedAsync(staged.Revision)).Should().BeTrue();
            (await store.DiscardStagedAsync(staged.Revision)).Should().BeFalse("discarding is idempotent");
            (await store.GetByRevisionAsync(staged.Revision)).Should().BeNull();

            // Rebased candidate: staged on top of the concurrent revision and activated against its ETag.
            var rebased = await store.StageAsync(GraphWith("parcels", "roads", "candidate-only"));
            var activated = await store.ActivateRevisionAsync(rebased.Revision, concurrent.Etag);

            activated.Revision.Should().Be(rebased.Revision);
            var current = await store.GetCurrentAsync();
            current.Revision.Should().Be(rebased.Revision);
            current.Graph.Resources.Select(resource => resource.Metadata.Id)
                .Should().BeEquivalentTo(new[] { "parcels", "roads", "candidate-only" }, "the concurrent update survives the rebased activation");
            (await CountAsync(schema, $"metadata_v2_resources_idx WHERE environment = 'Test' AND revision = {rebased.Revision}"))
                .Should().Be(3, "activation publishes the candidate's derived lookup rows atomically with the pointer");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }

        static MetadataV2Graph GraphWith(params string[] resourceIds) => new()
        {
            Environment = "Test",
            GeneratedAt = DateTimeOffset.UtcNow,
            Resources = resourceIds
                .Select(id => new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = id, Name = id },
                    Type = MetadataV2ResourceType.FeatureDataset,
                })
                .ToArray(),
        };
    }

    private async Task<int> CountAsync(string schema, string tableAndPredicate)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*)::int FROM \"{schema}\".{tableAndPredicate}";
        return (int)(await command.ExecuteScalarAsync())!;
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

    [IntegrationTest]
    public async Task SaveAsync_BulkSidecarsReplaceOnlyTheCurrentRevision()
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
            var first = await store.SaveAsync(FullSidecarGraph(), expectedEtag: null);
            var second = await store.SaveAsync(
                new MetadataV2Graph
                {
                    Environment = "Test",
                    GeneratedAt = DateTimeOffset.UtcNow,
                    Resources =
                    [
                        new MetadataV2Resource
                        {
                            Metadata = new MetadataV2ObjectMetadata { Id = "res-next", Name = "next" },
                            Type = MetadataV2ResourceType.FeatureDataset,
                        },
                    ],
                },
                first.Etag);

            (await CountAsync(schema, $"metadata_v2_snapshots WHERE environment = 'Test' AND revision = {first.Revision}"))
                .Should().Be(1, "the previous document stays retained");
            (await CountAsync(schema, $"metadata_v2_resources_idx WHERE environment = 'Test' AND revision = {first.Revision}"))
                .Should().Be(0);
            (await CountAsync(schema, $"metadata_v2_services_idx WHERE environment = 'Test' AND revision = {first.Revision}"))
                .Should().Be(0);
            (await CountAsync(schema, $"metadata_v2_publications_idx WHERE environment = 'Test' AND revision = {first.Revision}"))
                .Should().Be(0);
            (await CountAsync(schema, $"metadata_v2_resources_idx WHERE environment = 'Test' AND revision = {second.Revision}"))
                .Should().Be(1);
            (await CountAsync(schema, $"metadata_v2_services_idx WHERE environment = 'Test' AND revision = {second.Revision}"))
                .Should().Be(0);

            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            await store.SaveAsync(FullSidecarGraph(), second.Etag);
            var current = await store.GetCurrentAsync();
            command.CommandText = $"""
                SELECT namespace IS NULL, type, primary_storage_binding_id
                FROM "{schema}".metadata_v2_resources_idx
                WHERE environment = 'Test' AND revision = {current.Revision} AND resource_id = 'res-1';
                """;
            await using (var reader = await command.ExecuteReaderAsync())
            {
                (await reader.ReadAsync()).Should().BeTrue();
                reader.GetBoolean(0).Should().BeTrue();
                reader.GetString(1).Should().Be("FeatureDataset");
                reader.GetString(2).Should().Be("bind-1");
            }

            command.CommandText = $"""
                SELECT route IS NULL, service_type
                FROM "{schema}".metadata_v2_services_idx
                WHERE environment = 'Test' AND revision = {current.Revision} AND service_id = 'svc-1';
                """;
            await using (var reader = await command.ExecuteReaderAsync())
            {
                (await reader.ReadAsync()).Should().BeTrue();
                reader.GetBoolean(0).Should().BeTrue();
                reader.GetString(1).Should().Be("FeatureServer");
            }

            command.CommandText = $"""
                SELECT publication_id, path IS NULL, layer_index IS NULL, layer_index, service_local_id, publication_type
                FROM "{schema}".metadata_v2_publications_idx
                WHERE environment = 'Test' AND revision = {current.Revision}
                ORDER BY publication_id;
                """;
            await using (var reader = await command.ExecuteReaderAsync())
            {
                (await reader.ReadAsync()).Should().BeTrue();
                reader.GetString(0).Should().Be("pub-1");
                reader.GetBoolean(1).Should().BeTrue();
                reader.GetBoolean(2).Should().BeFalse();
                reader.GetInt32(3).Should().Be(7);
                reader.GetString(4).Should().Be("7");
                reader.GetString(5).Should().Be("EsriFeatureLayer");
                (await reader.ReadAsync()).Should().BeTrue();
                reader.GetString(0).Should().Be("pub-2");
                reader.GetBoolean(1).Should().BeTrue();
                reader.GetBoolean(2).Should().BeTrue();
                reader.IsDBNull(4).Should().BeTrue();
                reader.GetString(5).Should().Be("StacCollection");
            }

            command.CommandText = $"""
                SELECT provider IS NULL, type
                FROM "{schema}".metadata_v2_connections_idx
                WHERE environment = 'Test' AND revision = {current.Revision} AND connection_id = 'conn-1';
                """;
            await using (var reader = await command.ExecuteReaderAsync())
            {
                (await reader.ReadAsync()).Should().BeTrue();
                reader.GetBoolean(0).Should().BeTrue();
                reader.GetString(1).Should().Be("Managed");
            }

            (await CountAsync(schema, $"metadata_v2_publications_idx WHERE environment = 'Test' AND revision = {second.Revision}"))
                .Should().Be(0);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }

        static MetadataV2Graph FullSidecarGraph() => new()
        {
            Environment = "Test",
            GeneratedAt = DateTimeOffset.UtcNow,
            Connections =
            [
                new MetadataV2Connection
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "conn-1", Name = "warehouse" },
                    Type = MetadataV2ConnectionType.Managed,
                    Provider = null,
                },
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "bind-1", Name = "bind-1" },
                    ResourceId = "res-1",
                    ConnectionId = "conn-1",
                    StorageType = MetadataV2StorageType.RelationalTable,
                    Locator = "public.parcels",
                    StorageLayerId = 7,
                },
            ],
            Resources =
            [
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "res-1", Name = "parcels" },
                    Type = MetadataV2ResourceType.FeatureDataset,
                    StorageBindingIds = ["bind-1"],
                },
            ],
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "svc-1", Name = "parcels" },
                    Protocols = ["FeatureServer"],
                },
            ],
            Publications =
            [
                new MetadataV2Publication
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "pub-1", Name = "pub-1" },
                    ResourceId = "res-1",
                    ServiceId = "svc-1",
                    StorageBindingId = "bind-1",
                    PublicationType = MetadataV2PublicationType.EsriFeatureLayer,
                    Identifier = new MetadataV2PublicationIdentifier { Value = "7", IsNumeric = true },
                    IsPrimary = true,
                },
                new MetadataV2Publication
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "pub-2", Name = "pub-2" },
                    ResourceId = "res-1",
                    ServiceId = "svc-1",
                    StorageBindingId = "bind-1",
                    PublicationType = MetadataV2PublicationType.StacCollection,
                    Identifier = new MetadataV2PublicationIdentifier { Value = string.Empty, IsNumeric = false },
                    IsPrimary = false,
                },
            ],
        };
    }

    [IntegrationTest]
    public async Task SaveAsync_PrunesSnapshotsBeyondRetentionUnlessReleasePackageReferencesThem()
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
            var retained = await store.SaveAsync(
                new MetadataV2Graph { Environment = "Test", GeneratedAt = DateTimeOffset.UtcNow },
                expectedEtag: null);

            await using (var connection = await fixture.DataSource.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    CREATE TABLE "{schema}".metadata_v2_release_packages (
                        source_environment text NOT NULL,
                        source_revision bigint NOT NULL,
                        status text NOT NULL);
                    INSERT INTO "{schema}".metadata_v2_release_packages
                        (source_environment, source_revision, status)
                    VALUES ('Test', {retained.Revision}, 'staged');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var current = retained;
            for (var index = 0; index < PostgresMetadataV2GraphStore.MetadataV2RetainedSnapshotCount; index++)
            {
                current = await store.SaveAsync(
                    new MetadataV2Graph { Environment = "Test", GeneratedAt = DateTimeOffset.UtcNow },
                    current.Etag);
            }

            (await CountAsync(schema, $"metadata_v2_snapshots WHERE environment = 'Test' AND revision = {retained.Revision}"))
                .Should().Be(1, "an in-flight release package keeps its source revision past the retention window");

            await using (var connection = await fixture.DataSource.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    UPDATE "{schema}".metadata_v2_release_packages
                       SET status = 'cancelled'
                     WHERE source_revision = {retained.Revision};
                    """;
                await command.ExecuteNonQueryAsync();
            }

            current = await store.SaveAsync(
                new MetadataV2Graph { Environment = "Test", GeneratedAt = DateTimeOffset.UtcNow },
                current.Etag);

            (await CountAsync(schema, $"metadata_v2_snapshots WHERE environment = 'Test' AND revision = {retained.Revision}"))
                .Should().Be(0);
            (await CountAsync(schema, "metadata_v2_snapshots WHERE environment = 'Test'"))
                .Should().Be(PostgresMetadataV2GraphStore.MetadataV2RetainedSnapshotCount);
            (await store.GetCurrentAsync()).Revision.Should().Be(current.Revision);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static PostgresCoreSchemaGuard CreateGuard(string schema)
        => new(
            TestCoreSchemaMigrations.Manifest,
            new ConfigurationBuilder()
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

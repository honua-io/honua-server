// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Db.Postgres.Features.Metadata;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

/// <summary>
/// PostgreSQL regression coverage for the schema-coupled demo STAC seed.
/// </summary>
[Collection("Database")]
public sealed class DemoStacSeedPostgresTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task Apply_MissingFeatureRelation_IsIdempotentAndAtomic()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync(nameof(DemoStacSeedPostgresTests));
        var databaseName = new NpgsqlConnectionStringBuilder(connectionString).Database!;

        try
        {
            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            await CreateMetadataV2TablesAsync(dataSource);
            await CreateChangeTrackingTablesAsync(dataSource);

            (await ScalarStringAsync(dataSource, "SELECT to_regclass('honua.features')::text"))
                .Should().BeNull("the regression must start from the live failure state");

            await ExecuteAsync(dataSource, "CREATE SCHEMA tenant");
            var unsupportedSchemaSeed = RenderSeed(injectFailure: false, schema: "tenant");
            Func<Task> applyToUnsupportedSchema = () => ExecuteAsync(dataSource, unsupportedSchemaSeed);
            var schemaFailure = await applyToUnsupportedSchema.Should().ThrowAsync<PostgresException>();
            schemaFailure.Which.SqlState.Should().Be("55000");
            (await ScalarStringAsync(dataSource, "SELECT to_regclass('tenant.features')::text"))
                .Should().BeNull("an unsupported schema must fail before relation recovery");
            (await ScalarInt64Async(dataSource, "SELECT count(*) FROM honua.metadata_v2_snapshots"))
                .Should().Be(0, "an unsupported schema must not publish metadata");

            var seed = RenderSeed(injectFailure: false);
            Func<Task> applyWithoutCurrentMigrations = () => ExecuteAsync(dataSource, seed);
            var prerequisiteFailure = await applyWithoutCurrentMigrations.Should().ThrowAsync<PostgresException>();
            prerequisiteFailure.Which.SqlState.Should().Be("55000");
            (await ScalarStringAsync(dataSource, "SELECT to_regclass('honua.features')::text"))
                .Should().BeNull("a missing change-tracking contract must roll back relation recovery");

            await InstallCurrentChangeTrackingFunctionsAsync(dataSource);
            var currentTrackingFunctionOid = await ScalarInt64Async(dataSource, TrackingFunctionOidSql);
            await InstallMigration067ChangeTrackingFunctionAsync(dataSource);
            (await ScalarInt64Async(dataSource, TrackingFunctionOidSql))
                .Should().Be(currentTrackingFunctionOid, "CREATE OR REPLACE preserves the stale function OID");
            var changeStateBeforeLegacyRecovery = await ScalarStringAsync(dataSource, FeatureChangeStateSql);
            Func<Task> applyWithMigration067Tracker = () => ExecuteAsync(dataSource, seed);
            var legacyFailure = await applyWithMigration067Tracker.Should().ThrowAsync<PostgresException>();
            legacyFailure.Which.SqlState.Should().Be("55000");
            (await ScalarStringAsync(dataSource, "SELECT to_regclass('honua.features')::text"))
                .Should().BeNull("stale tracking behavior must roll back relation recovery");
            (await ScalarInt64Async(dataSource, "SELECT count(*) FROM honua.metadata_v2_snapshots"))
                .Should().Be(0, "stale tracking behavior must not publish metadata");
            (await ScalarStringAsync(dataSource, FeatureChangeStateSql)).Should().Be(changeStateBeforeLegacyRecovery);

            await InstallCurrentChangeTrackingFunctionsAsync(dataSource);
            await ExecuteAsync(dataSource, StorageObjectIdOnlyResolverSql);
            var changeStateBeforeHostileResolverRecovery = await ScalarStringAsync(dataSource, FeatureChangeStateSql);
            Func<Task> applyWithHostileResolver = () => ExecuteAsync(dataSource, seed);
            var hostileResolverFailure = await applyWithHostileResolver.Should().ThrowAsync<PostgresException>();
            hostileResolverFailure.Which.SqlState.Should().Be("55000");
            (await ScalarStringAsync(dataSource, "SELECT to_regclass('honua.features')::text"))
                .Should().BeNull("a resolver that ignores configured public IDs must roll back relation recovery");
            (await ScalarInt64Async(dataSource, "SELECT count(*) FROM honua.metadata_v2_snapshots"))
                .Should().Be(0, "hostile resolver behavior must not publish metadata");
            (await ScalarStringAsync(dataSource, FeatureChangeStateSql))
                .Should().Be(changeStateBeforeHostileResolverRecovery);

            await InstallCurrentChangeTrackingFunctionsAsync(dataSource);
            await ExecuteAsync(dataSource, RaisingTrackingFunctionSql);
            var changeStateBeforeRaisingTrackerRecovery = await ScalarStringAsync(dataSource, FeatureChangeStateSql);
            Func<Task> applyWithRaisingTracker = () => ExecuteAsync(dataSource, seed);
            var raisingTrackerFailure = await applyWithRaisingTracker.Should().ThrowAsync<PostgresException>();
            raisingTrackerFailure.Which.SqlState.Should().Be("P0001");
            (await ScalarStringAsync(dataSource, "SELECT to_regclass('honua.features')::text"))
                .Should().BeNull("a tracker exception must not be mistaken for the intentional probe rollback");
            (await ScalarInt64Async(dataSource, "SELECT count(*) FROM honua.metadata_v2_snapshots"))
                .Should().Be(0, "a raising tracker must not publish metadata");
            (await ScalarStringAsync(dataSource, FeatureChangeStateSql))
                .Should().Be(changeStateBeforeRaisingTrackerRecovery);

            await InstallCurrentChangeTrackingFunctionsAsync(dataSource);
            await ExecuteAsync(dataSource, RetainedFeatureChangeSql);
            var retainedChangeState = await ScalarStringAsync(dataSource, FeatureChangeStateSql);
            Func<Task> applyWithLostHistory = () => ExecuteAsync(dataSource, seed);
            var historyFailure = await applyWithLostHistory.Should().ThrowAsync<PostgresException>();
            historyFailure.Which.SqlState.Should().Be("55000");
            (await ScalarStringAsync(dataSource, "SELECT to_regclass('honua.features')::text"))
                .Should().BeNull("ambiguous retained history must fail before relation recovery");
            (await ScalarStringAsync(dataSource, FeatureChangeStateSql)).Should().Be(retainedChangeState);
            (await ScalarInt64Async(dataSource, "SELECT count(*) FROM honua.metadata_v2_snapshots"))
                .Should().Be(0);
            await ExecuteAsync(dataSource, "DELETE FROM honua.feature_changes");

            await ExecuteAsync(dataSource, RetainedReplicaSql);
            Func<Task> applyWithRegisteredReplica = () => ExecuteAsync(dataSource, seed);
            var replicaFailure = await applyWithRegisteredReplica.Should().ThrowAsync<PostgresException>();
            replicaFailure.Which.SqlState.Should().Be("55000");
            (await ScalarStringAsync(dataSource, "SELECT to_regclass('honua.features')::text"))
                .Should().BeNull("registered replica cursors must fail before relation recovery");
            (await ScalarInt64Async(dataSource, "SELECT count(*) FROM honua.replicas"))
                .Should().Be(1, "the seed must not invent replica invalidation semantics");
            await ExecuteAsync(dataSource, "DELETE FROM honua.replicas");

            await ExecuteAsync(dataSource, seed);

            (await ScalarStringAsync(dataSource, "SELECT to_regclass('honua.features')::text"))
                .Should().NotBeNull("the recovered relation must exist regardless of regclass display qualification");
            (await ScalarInt64Async(dataSource, "SELECT count(*) FROM honua.features"))
                .Should().Be(7);
            (await ScalarInt64Async(dataSource, RequiredIndexesSql))
                .Should().Be(14);
            (await ScalarStringAsync(dataSource, IndexNamesSql))
                .Should().Be(ExpectedIndexNames);
            (await ScalarStringAsync(dataSource, IndexShapeSql))
                .Should().Be("btree:5,gin:4,gist:5,partial:5");
            await CreateMigrationIndexContractAsync(dataSource);
            (await ScalarInt64Async(dataSource, IndexDefinitionMismatchCountSql))
                .Should().Be(0, "every recovered index must match the current migration-owned definition");
            (await ScalarInt64Async(dataSource, TriggerCountSql))
                .Should().Be(1);
            var firstTriggerOid = await ScalarInt64Async(dataSource, TriggerOidSql);
            var firstIndexOids = await ScalarStringAsync(dataSource, IndexOidsSql);
            var firstSequenceOid = await ScalarInt64Async(dataSource, SequenceOidSql);
            var firstSequenceState = await ScalarStringAsync(dataSource, SequenceStateSql);
            (await ScalarStringAsync(dataSource, SequenceIdentitySql))
                .Should().Be("honua.features_objectid_seq");
            (await ScalarInt64Async(dataSource, CurrentRevisionSql)).Should().Be(1);
            var firstPublicationIdentity = await ScalarStringAsync(dataSource, PublicationIdentitySql);
            firstPublicationIdentity.Should().NotBeNullOrWhiteSpace();

            await AssertHealthyRerunAvoidsSchemaDdlAsync(dataSource, seed);

            (await ScalarInt64Async(dataSource, "SELECT count(*) FROM honua.features"))
                .Should().Be(7, "reapplying the seed must replace, not duplicate, fixture rows");
            (await ScalarInt64Async(dataSource, CurrentRevisionSql)).Should().Be(2);
            (await ScalarInt64Async(dataSource, TriggerCountSql))
                .Should().Be(1, "reapplying the seed must not duplicate its migration-owned trigger");
            (await ScalarInt64Async(dataSource, TriggerOidSql))
                .Should().Be(firstTriggerOid, "an idempotent rerun must not drop and recreate the valid trigger");
            (await ScalarStringAsync(dataSource, IndexOidsSql))
                .Should().Be(firstIndexOids, "a healthy rerun must retain every recovered index identity");
            (await ScalarInt64Async(dataSource, SequenceOidSql))
                .Should().Be(firstSequenceOid, "a healthy rerun must retain the recovered owned sequence");
            (await ScalarStringAsync(dataSource, SequenceStateSql))
                .Should().Be(firstSequenceState, "a healthy rerun must not reset or advance the objectid sequence");
            (await ScalarStringAsync(dataSource, PublicationIdentitySql))
                .Should().Be(firstPublicationIdentity, "publication ids and bindings must remain stable");

            await InstallMigration067ChangeTrackingFunctionAsync(dataSource);
            var stableHealthyFeatureState = await ScalarStringAsync(dataSource, FeatureStateSql);
            var stableHealthyChangeState = await ScalarStringAsync(dataSource, FeatureChangeStateSql);
            Func<Task> applyWithHealthyMigration067Tracker = () => ExecuteAsync(dataSource, seed);
            var healthyLegacyFailure = await applyWithHealthyMigration067Tracker.Should().ThrowAsync<PostgresException>();
            healthyLegacyFailure.Which.SqlState.Should().Be("55000");
            (await ScalarStringAsync(dataSource, FeatureStateSql)).Should().Be(stableHealthyFeatureState);
            (await ScalarStringAsync(dataSource, FeatureChangeStateSql)).Should().Be(stableHealthyChangeState);
            (await ScalarInt64Async(dataSource, CurrentRevisionSql)).Should().Be(2);
            await InstallCurrentChangeTrackingFunctionsAsync(dataSource);

            var featureMaxBeforeDefaultInsert = await ScalarInt64Async(dataSource, FeatureMaxObjectIdSql);
            var changeMaxBeforeDefaultInsert = await ScalarInt64Async(dataSource, FeatureChangeMaxObjectIdSql);
            var defaultObjectId = await ScalarInt64Async(dataSource, InsertDefaultFeatureSql);
            defaultObjectId.Should().BeGreaterThan(featureMaxBeforeDefaultInsert);
            defaultObjectId.Should().BeGreaterThan(changeMaxBeforeDefaultInsert);

            await AssertChangeTrackingAsync(dataSource);
            var stableFeatureState = await ScalarStringAsync(dataSource, FeatureStateSql);
            var stableChangeState = await ScalarStringAsync(dataSource, FeatureChangeStateSql);
            var failingSeed = RenderSeed(injectFailure: true);
            Func<Task> applyFailure = () => ExecuteAsync(dataSource, failingSeed);
            var failure = await applyFailure.Should().ThrowAsync<PostgresException>();
            failure.Which.SqlState.Should().Be("22012");

            (await ScalarInt64Async(dataSource, CurrentRevisionSql))
                .Should().Be(2, "the failed seed must not activate a partial revision");
            (await ScalarInt64Async(dataSource,
                    "SELECT count(*) FROM honua.metadata_v2_snapshots WHERE environment = 'seed-regression'"))
                .Should().Be(2, "the failed revision insert must roll back");
            (await ScalarStringAsync(dataSource, FeatureStateSql))
                .Should().Be(stableFeatureState, "fixture row replacement must roll back with publication changes");
            (await ScalarStringAsync(dataSource, FeatureChangeStateSql))
                .Should().Be(stableChangeState, "trigger-produced change rows must roll back with the seed");
            (await ScalarStringAsync(dataSource, PublicationIdentitySql))
                .Should().Be(firstPublicationIdentity);
            (await ScalarInt64Async(dataSource, TriggerCountSql)).Should().Be(1);
            await AssertColumnRestrictedTriggerRejectedAsync(dataSource, seed);
            await ExecuteAsync(dataSource, RestoreCanonicalTriggerSql);
            await AssertHostileTriggerRejectedAsync(dataSource, seed);
        }
        finally
        {
            await fixture.DropDatabaseAsync(databaseName);
        }
    }

    [IntegrationTest]
    public async Task Apply_SeedFirstCanonicalBootstrap_SerializesWithoutOverwritingSeedRevision()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync(
            nameof(Apply_SeedFirstCanonicalBootstrap_SerializesWithoutOverwritingSeedRevision));
        var databaseName = new NpgsqlConnectionStringBuilder(connectionString).Database!;
        var metadataGateHeld = false;

        try
        {
            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            await CreateMetadataV2TablesAsync(dataSource);
            await CreateChangeTrackingTablesAsync(dataSource);
            await InstallCurrentChangeTrackingFunctionsAsync(dataSource);

            var provider = new TestConnectionProvider(dataSource, "honua");
            var store = new PostgresMetadataV2GraphStore(
                provider,
                environment: "seed-regression",
                schemaGuard: FixtureBypassDatabaseSchemaGuard.Instance,
                schemaName: "honua");
            var canonicalGraph = new MetadataV2Graph
            {
                Environment = "seed-regression",
                Revision = 1,
                GeneratedAt = DateTimeOffset.UtcNow,
                Resources =
                [
                    new MetadataV2Resource
                    {
                        Metadata = new MetadataV2ObjectMetadata
                        {
                            Id = "res-canonical",
                            Name = "canonical-writer-resource",
                        },
                        Type = MetadataV2ResourceType.FeatureDataset,
                    },
                ],
            };

            await using var coordinator = await dataSource.OpenConnectionAsync();
            await using (var hold = coordinator.CreateCommand())
            {
                hold.CommandText =
                    $"SELECT pg_advisory_lock({MetadataLockNamespace}, hashtext('seed-regression'))";
                await hold.ExecuteNonQueryAsync();
                metadataGateHeld = true;
            }

            var seedWrite = ExecuteAsync(dataSource, RenderSeed(injectFailure: false));
            await WaitForMetadataLockWaitersAsync(dataSource, expectedCount: 1);
            var canonicalWrite = store.SaveAsync(canonicalGraph, expectedEtag: null);
            await WaitForMetadataLockWaitersAsync(dataSource, expectedCount: 2);

            await using (var release = coordinator.CreateCommand())
            {
                release.CommandText =
                    $"SELECT pg_advisory_unlock({MetadataLockNamespace}, hashtext('seed-regression'))";
                await release.ExecuteNonQueryAsync();
                metadataGateHeld = false;
            }

            await seedWrite.WaitAsync(TimeSpan.FromSeconds(30));
            await canonicalWrite.WaitAsync(TimeSpan.FromSeconds(30));

            (await ScalarInt64Async(dataSource, CurrentRevisionSql)).Should().Be(2);
            (await ScalarInt64Async(dataSource, SeedRevisionStillPresentSql))
                .Should().Be(1, "the force writer must allocate revision 2 instead of overwriting the seed snapshot");
            (await ScalarInt64Async(dataSource, CanonicalRevisionPresentSql)).Should().Be(1);
        }
        finally
        {
            if (metadataGateHeld)
            {
                await using var cleanupDataSource = NpgsqlDataSource.Create(connectionString);
                await ExecuteAsync(
                    cleanupDataSource,
                    $"SELECT pg_advisory_unlock({MetadataLockNamespace}, hashtext('seed-regression'))");
            }

            await fixture.DropDatabaseAsync(databaseName);
        }
    }

    [IntegrationTest]
    public async Task Apply_ConcurrentReplicaRegistration_CommitsCursorBeforeRecoveryFailsClosed()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync(
            nameof(Apply_ConcurrentReplicaRegistration_CommitsCursorBeforeRecoveryFailsClosed));
        var databaseName = new NpgsqlConnectionStringBuilder(connectionString).Database!;
        var recoveryGateHeld = false;

        try
        {
            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            await CreateMetadataV2TablesAsync(dataSource);
            await CreateChangeTrackingTablesAsync(dataSource);
            await InstallCurrentChangeTrackingFunctionsAsync(dataSource);
            var repository = new PostgresReplicaRepository(new TestConnectionProvider(dataSource, "honua"));
            var now = DateTimeOffset.UtcNow;
            var record = new ReplicaRecord
            {
                ReplicaId = "concurrent-replica",
                ReplicaName = "Concurrent replica",
                ServiceId = "demo-stac",
                SyncModel = "perReplica",
                LayerIds = [90810],
                CreatedAt = now,
                LastSyncTime = now,
                LastSyncGeneration = -1,
            };

            await using var coordinator = await dataSource.OpenConnectionAsync();
            await using (var hold = coordinator.CreateCommand())
            {
                hold.CommandText =
                    $"SELECT pg_advisory_lock({RecoveryLockNamespace}, {RecoveryLockKey})";
                await hold.ExecuteNonQueryAsync();
                recoveryGateHeld = true;
            }

            var registration = repository.RegisterAtCurrentGenerationAsync(record);
            await WaitForAdvisoryWaitersAsync(
                dataSource, RecoveryLockNamespace, RecoveryLockKey, expectedCount: 1);
            var recovery = ExecuteAsync(dataSource, RenderSeed(injectFailure: false));
            await WaitForAdvisoryWaitersAsync(
                dataSource, RecoveryLockNamespace, RecoveryLockKey, expectedCount: 2);

            await using (var release = coordinator.CreateCommand())
            {
                release.CommandText =
                    $"SELECT pg_advisory_unlock({RecoveryLockNamespace}, {RecoveryLockKey})";
                await release.ExecuteNonQueryAsync();
                recoveryGateHeld = false;
            }

            var registered = await registration.WaitAsync(TimeSpan.FromSeconds(30));
            registered.LastSyncGeneration.Should().Be(0);
            Func<Task> recoveryFailure = async () => await recovery.WaitAsync(TimeSpan.FromSeconds(30));
            var failure = await recoveryFailure.Should().ThrowAsync<PostgresException>();
            failure.Which.SqlState.Should().Be("55000");
            (await ScalarStringAsync(dataSource, "SELECT to_regclass('honua.features')::text"))
                .Should().BeNull("recovery must observe the serialized registration and fail closed");
            (await ScalarStringAsync(dataSource, RegisteredReplicaCursorSql))
                .Should().Be("concurrent-replica:0");
        }
        finally
        {
            if (recoveryGateHeld)
            {
                await using var cleanupDataSource = NpgsqlDataSource.Create(connectionString);
                await ExecuteAsync(
                    cleanupDataSource,
                    $"SELECT pg_advisory_unlock({RecoveryLockNamespace}, {RecoveryLockKey})");
            }

            await fixture.DropDatabaseAsync(databaseName);
        }
    }

    [IntegrationTest]
    public async Task Apply_ConcurrentRecovery_RechecksMissingStateBeforeDefaultWriter()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync(
            nameof(Apply_ConcurrentRecovery_RechecksMissingStateBeforeDefaultWriter));
        var databaseName = new NpgsqlConnectionStringBuilder(connectionString).Database!;
        var writerGateHeld = false;

        try
        {
            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            await CreateMetadataV2TablesAsync(dataSource);
            await CreateChangeTrackingTablesAsync(dataSource);
            await InstallCurrentChangeTrackingFunctionsAsync(dataSource);
            var seed = RenderSeed(injectFailure: false);

            await using var coordinator = await dataSource.OpenConnectionAsync();
            await using (var writerGate = coordinator.CreateCommand())
            {
                writerGate.CommandText =
                    $"SELECT pg_advisory_lock({WriterGateLockNamespace}, {WriterGateLockKey})";
                await writerGate.ExecuteNonQueryAsync();
                writerGateHeld = true;
            }

            await using var recoveryBlocker = await dataSource.OpenConnectionAsync();
            await using var recoveryBlockerTransaction = await recoveryBlocker.BeginTransactionAsync();
            await using (var recoveryLock = recoveryBlocker.CreateCommand())
            {
                recoveryLock.Transaction = recoveryBlockerTransaction;
                recoveryLock.CommandText =
                    $"SELECT pg_advisory_xact_lock({RecoveryLockNamespace}, {RecoveryLockKey})";
                await recoveryLock.ExecuteNonQueryAsync();
            }

            var firstSeed = ExecuteAsync(dataSource, seed);
            await WaitForAdvisoryWaitersAsync(
                dataSource,
                RecoveryLockNamespace,
                RecoveryLockKey,
                expectedCount: 1);

            var secondSeed = ExecuteAsync(dataSource, InjectWriterGate(seed));
            await WaitForAdvisoryWaitersAsync(
                dataSource,
                RecoveryLockNamespace,
                RecoveryLockKey,
                expectedCount: 2);

            await recoveryBlockerTransaction.CommitAsync();
            await firstSeed.WaitAsync(TimeSpan.FromSeconds(30));
            await WaitForAdvisoryWaitersAsync(
                dataSource,
                WriterGateLockNamespace,
                WriterGateLockKey,
                expectedCount: 1);

            var featureMaxBeforeWriter = await ScalarInt64Async(dataSource, FeatureMaxObjectIdSql);
            var changeMaxBeforeWriter = await ScalarInt64Async(dataSource, FeatureChangeMaxObjectIdSql);
            var writerObjectId = await ScalarInt64Async(dataSource, InsertDefaultFeatureSql);

            await using (var releaseWriterGate = coordinator.CreateCommand())
            {
                releaseWriterGate.CommandText =
                    $"SELECT pg_advisory_unlock({WriterGateLockNamespace}, {WriterGateLockKey})";
                await releaseWriterGate.ExecuteNonQueryAsync();
                writerGateHeld = false;
            }

            await secondSeed.WaitAsync(TimeSpan.FromSeconds(30));
            writerObjectId.Should().BeGreaterThan(featureMaxBeforeWriter);
            writerObjectId.Should().BeGreaterThan(changeMaxBeforeWriter);
            (await ScalarInt64Async(dataSource,
                    $"SELECT count(*) FROM honua.features WHERE objectid = {writerObjectId}"))
                .Should().Be(1, "the healthy second seed must not overwrite the concurrent writer");
            (await ScalarStringAsync(dataSource, SequenceStateSql))
                .Should().Be($"{writerObjectId}:t", "the healthy second seed must not rewind the sequence");
            (await ScalarInt64Async(dataSource, CurrentRevisionSql)).Should().Be(2);
        }
        finally
        {
            if (writerGateHeld)
            {
                await using var cleanupDataSource = NpgsqlDataSource.Create(connectionString);
                await ExecuteAsync(
                    cleanupDataSource,
                    $"SELECT pg_advisory_unlock({WriterGateLockNamespace}, {WriterGateLockKey})");
            }

            await fixture.DropDatabaseAsync(databaseName);
        }
    }

    [IntegrationTest]
    public async Task Apply_CurrentWithOrphanNextRevision_AllocatesAboveOrphan()
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync(
            nameof(Apply_CurrentWithOrphanNextRevision_AllocatesAboveOrphan));
        var databaseName = new NpgsqlConnectionStringBuilder(connectionString).Database!;

        try
        {
            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            await CreateMetadataV2TablesAsync(dataSource);
            await CreateChangeTrackingTablesAsync(dataSource);
            await InstallCurrentChangeTrackingFunctionsAsync(dataSource);
            var seed = RenderSeed(injectFailure: false);
            await ExecuteAsync(dataSource, seed);
            await ExecuteAsync(
                dataSource,
                """
                INSERT INTO honua.metadata_v2_snapshots
                    (environment, revision, schema_version, api_version, document, etag, generated_at)
                SELECT environment, 2, schema_version, api_version,
                       jsonb_set(document, '{revision}', '2'::jsonb),
                       'orphan-revision-2', generated_at
                  FROM honua.metadata_v2_snapshots
                 WHERE environment = 'seed-regression' AND revision = 1;
                """);

            await ExecuteAsync(dataSource, seed);

            (await ScalarInt64Async(dataSource, CurrentRevisionSql)).Should().Be(3);
            (await ScalarStringAsync(dataSource,
                    "SELECT etag FROM honua.metadata_v2_snapshots WHERE environment = 'seed-regression' AND revision = 2"))
                .Should().Be("orphan-revision-2", "the seed must not overwrite retained orphan snapshots");
        }
        finally
        {
            await fixture.DropDatabaseAsync(databaseName);
        }
    }

    private static string RenderSeed(bool injectFailure, string schema = "honua")
    {
        var seedPath = Path.Combine(AppContext.BaseDirectory, "Seed", "demo-stac-imagery-v1.sql");
        var source = File.ReadAllText(seedPath);
        var begin = source.IndexOf("\nBEGIN;", StringComparison.Ordinal);
        if (begin < 0)
        {
            throw new InvalidOperationException("Demo STAC seed has no transaction boundary.");
        }

        var rendered = source[(begin + 1)..]
            .Replace(":\"schema\"", $"\"{schema}\"", StringComparison.Ordinal)
            .Replace(":'schema'", $"'{schema}'", StringComparison.Ordinal)
            .Replace(":'env'", "'seed-regression'", StringComparison.Ordinal);
        if (rendered.Contains("\\set", StringComparison.Ordinal) || rendered.Contains(":'", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Demo STAC seed still contains psql-only substitutions.");
        }

        if (!injectFailure)
        {
            return rendered;
        }

        var commit = rendered.LastIndexOf("COMMIT;", StringComparison.Ordinal);
        if (commit < 0)
        {
            throw new InvalidOperationException("Demo STAC seed has no commit boundary.");
        }

        return rendered.Insert(commit, "SELECT 1 / 0; -- injected regression failure\n");
    }

    private static string InjectWriterGate(string seed)
    {
        const string marker = "DO $change_tracking$";
        if (!seed.Contains(marker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Demo STAC seed has no change-tracking boundary.");
        }

        return seed.Replace(
            marker,
            $"SELECT pg_advisory_xact_lock({WriterGateLockNamespace}, {WriterGateLockKey});\n\n{marker}",
            StringComparison.Ordinal);
    }

    private static async Task WaitForAdvisoryWaitersAsync(
        NpgsqlDataSource dataSource,
        int lockNamespace,
        int lockKey,
        int expectedCount)
    {
        var query = $"""
            SELECT count(*)
              FROM pg_locks
             WHERE locktype = 'advisory'
               AND classid = {lockNamespace}::oid
               AND objid = {lockKey}::oid
               AND NOT granted
            """;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (await ScalarInt64Async(dataSource, query) >= expectedCount)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        throw new TimeoutException(
            $"Expected {expectedCount} waiters for advisory lock ({lockNamespace}, {lockKey}).");
    }

    private static async Task WaitForMetadataLockWaitersAsync(
        NpgsqlDataSource dataSource,
        int expectedCount)
    {
        var query = $"""
            SELECT count(*)
              FROM pg_locks
             WHERE locktype = 'advisory'
               AND classid = {MetadataLockNamespace}::oid
               AND objid = hashtext('seed-regression')::oid
               AND NOT granted
            """;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (await ScalarInt64Async(dataSource, query) >= expectedCount)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        throw new TimeoutException($"Expected {expectedCount} metadata publication lock waiters.");
    }

    private static async Task CreateMetadataV2TablesAsync(NpgsqlDataSource dataSource)
        => await ExecuteAsync(dataSource, MetadataV2TablesSql);

    private static async Task CreateChangeTrackingTablesAsync(NpgsqlDataSource dataSource)
        => await ExecuteAsync(dataSource, ChangeTrackingTablesSql);

    private static async Task InstallCurrentChangeTrackingFunctionsAsync(NpgsqlDataSource dataSource)
    {
        var migrationPath = Path.Join(AppContext.BaseDirectory, "Migrations", "105_AddChangeLogPublicObjectId.sql");
        var migration = File.ReadAllText(migrationPath);
        var contractStart = migration.IndexOf(
            "CREATE OR REPLACE FUNCTION honua.resolve_feature_public_objectid(",
            StringComparison.Ordinal);
        var contractEnd = migration.IndexOf(
            "CREATE OR REPLACE FUNCTION honua.track_version_edits()",
            contractStart,
            StringComparison.Ordinal);
        if (contractStart < 0 || contractEnd < 0)
        {
            throw new InvalidOperationException("Migration 105 does not contain the current feature change-tracking contract.");
        }

        await ExecuteAsync(dataSource, migration[contractStart..contractEnd]);
    }

    private static async Task InstallMigration067ChangeTrackingFunctionAsync(NpgsqlDataSource dataSource)
    {
        var migrationPath = Path.Join(
            AppContext.BaseDirectory,
            "Migrations",
            "067_SerializeChangeLogGenerationAllocation.sql");
        var migration = File.ReadAllText(migrationPath);
        var contractStart = migration.IndexOf(
            "CREATE OR REPLACE FUNCTION honua.track_feature_changes()",
            StringComparison.Ordinal);
        var contractEnd = migration.IndexOf(
            "CREATE OR REPLACE FUNCTION honua.track_version_edits()",
            contractStart,
            StringComparison.Ordinal);
        if (contractStart < 0 || contractEnd < 0)
        {
            throw new InvalidOperationException("Migration 067 does not contain its feature tracking contract.");
        }

        await ExecuteAsync(dataSource, migration[contractStart..contractEnd]);
    }

    private static async Task CreateMigrationIndexContractAsync(NpgsqlDataSource dataSource)
    {
        var statements = new List<string>();
        foreach (var migrationName in new[] { "001_CreateHonuaSchema.sql", "018_AddPerformanceIndexes.sql" })
        {
            var migrationPath = Path.Join(AppContext.BaseDirectory, "Migrations", migrationName);
            var migration = File.ReadAllText(migrationPath);
            statements.AddRange(Regex.Matches(
                    migration,
                    @"(?ms)^CREATE INDEX IF NOT EXISTS idx_features_[a-z0-9_]+\b.*?;")
                .Select(match => Regex.Replace(
                    match.Value,
                    @"\bON\s+(?:honua\.)?features\b",
                    "ON migration_index_contract.features",
                    RegexOptions.IgnoreCase)));
        }

        if (statements.Count != 13)
        {
            throw new InvalidOperationException(
                $"Expected 13 migration-owned secondary feature indexes, found {statements.Count}.");
        }

        await ExecuteAsync(
            dataSource,
            MigrationIndexReferenceTableSql + Environment.NewLine + string.Join(Environment.NewLine, statements));
    }

    private static async Task AssertChangeTrackingAsync(NpgsqlDataSource dataSource)
    {
        await ExecuteAsync(dataSource, ChangeTrackingExerciseSql);
        (await ScalarStringAsync(dataSource, ChangeTrackingOperationsSql))
            .Should().Be("1:880001,2:880001,3:880001");
        (await ScalarInt64Async(dataSource, NonIncreasingChangeGenerationCountSql))
            .Should().Be(0, "insert, update, and delete generations must be strictly increasing");
    }

    private static async Task AssertHealthyRerunAvoidsSchemaDdlAsync(
        NpgsqlDataSource dataSource,
        string seed)
    {
        await using var blocker = await dataSource.OpenConnectionAsync();
        await using var blockerTransaction = await blocker.BeginTransactionAsync();
        await using var lockCommand = blocker.CreateCommand();
        lockCommand.Transaction = blockerTransaction;
        lockCommand.CommandText = "LOCK TABLE honua.features IN ROW EXCLUSIVE MODE";
        await lockCommand.ExecuteNonQueryAsync();

        try
        {
            var boundedSeed = seed.Replace(
                "BEGIN;\n",
                "BEGIN;\nSET LOCAL lock_timeout = '1s';\n",
                StringComparison.Ordinal);
            await ExecuteAsync(dataSource, boundedSeed);
        }
        finally
        {
            await blockerTransaction.RollbackAsync();
        }
    }

    private static async Task AssertHostileTriggerRejectedAsync(NpgsqlDataSource dataSource, string seed)
    {
        var stableFeatureState = await ScalarStringAsync(dataSource, FeatureStateSql);
        var stableChangeState = await ScalarStringAsync(dataSource, FeatureChangeStateSql);
        await ExecuteAsync(dataSource, ReplaceTriggerWithHostileDefinitionSql);
        var hostileTriggerOid = await ScalarInt64Async(dataSource, TriggerOidSql);

        Func<Task> applyWithHostileTrigger = () => ExecuteAsync(dataSource, seed);
        var failure = await applyWithHostileTrigger.Should().ThrowAsync<PostgresException>();
        failure.Which.SqlState.Should().Be("55000");
        (await ScalarInt64Async(dataSource, TriggerOidSql)).Should().Be(hostileTriggerOid);
        (await ScalarStringAsync(dataSource, TriggerFunctionSql))
            .Should().Be("honua.hostile_track_feature_changes()");
        (await ScalarStringAsync(dataSource, FeatureStateSql)).Should().Be(stableFeatureState);
        (await ScalarStringAsync(dataSource, FeatureChangeStateSql)).Should().Be(stableChangeState);
    }

    private static async Task AssertColumnRestrictedTriggerRejectedAsync(
        NpgsqlDataSource dataSource,
        string seed)
    {
        var stableFeatureState = await ScalarStringAsync(dataSource, FeatureStateSql);
        var stableChangeState = await ScalarStringAsync(dataSource, FeatureChangeStateSql);
        await ExecuteAsync(dataSource, ReplaceTriggerWithColumnRestrictedDefinitionSql);
        var restrictedTriggerOid = await ScalarInt64Async(dataSource, TriggerOidSql);

        (await ScalarInt64Async(dataSource, TriggerTypeSql)).Should().Be(29);
        (await ScalarStringAsync(dataSource, TriggerFunctionSql))
            .Should().Be("honua.track_feature_changes()");
        (await ScalarStringAsync(dataSource, TriggerAttributesSql))
            .Should().NotBeNullOrWhiteSpace("UPDATE OF objectid must be visible in pg_trigger.tgattr");

        Func<Task> applyWithRestrictedTrigger = () => ExecuteAsync(dataSource, seed);
        var failure = await applyWithRestrictedTrigger.Should().ThrowAsync<PostgresException>();
        failure.Which.SqlState.Should().Be("55000");
        (await ScalarInt64Async(dataSource, TriggerOidSql)).Should().Be(restrictedTriggerOid);
        (await ScalarStringAsync(dataSource, FeatureStateSql)).Should().Be(stableFeatureState);
        (await ScalarStringAsync(dataSource, FeatureChangeStateSql)).Should().Be(stableChangeState);
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarInt64Async(NpgsqlDataSource dataSource, string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ScalarStringAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private const string CurrentRevisionSql =
        "SELECT revision FROM honua.metadata_v2_current WHERE environment = 'seed-regression'";

    private const int RecoveryLockNamespace = 144047712;
    private const int RecoveryLockKey = 1;
    private const int WriterGateLockNamespace = 144047713;
    private const int WriterGateLockKey = 0;
    private const int MetadataLockNamespace = 144047714;
    private const string SeedRevisionStillPresentSql =
        """
        SELECT count(*)
          FROM honua.metadata_v2_snapshots
         WHERE environment = 'seed-regression'
           AND revision = 1
           AND document->'services' @> '[{"metadata":{"id":"svc-demo-stac"}}]'::jsonb
        """;

    private const string CanonicalRevisionPresentSql =
        """
        SELECT count(*)
          FROM honua.metadata_v2_snapshots
         WHERE environment = 'seed-regression'
           AND revision = 2
           AND document->'resources' @> '[{"metadata":{"id":"res-canonical"}}]'::jsonb
        """;

    private const string CanonicalAndSeedPublicationCountSql =
        """
        SELECT ((document->'resources') @> '[{"metadata":{"id":"res-canonical"}}]'::jsonb)::int
             + ((document->'services') @> '[{"metadata":{"id":"svc-demo-stac"}}]'::jsonb)::int
          FROM honua.metadata_v2_snapshots s
          JOIN honua.metadata_v2_current c USING (environment, revision)
         WHERE c.environment = 'seed-regression'
        """;

    private const string RegisteredReplicaCursorSql =
        "SELECT replica_id || ':' || last_sync_generation::text FROM honua.replicas WHERE replica_id = 'concurrent-replica'";

    private const string RequiredIndexesSql =
        """
        SELECT count(*)
          FROM pg_indexes
         WHERE schemaname = 'honua'
           AND tablename = 'features'
           AND indexname IN (
               'features_pkey',
               'idx_features_layer_id',
               'idx_features_geometry',
               'idx_features_geography',
               'idx_features_attributes',
               'idx_features_layer_objectid',
               'idx_features_attributes_gin',
               'idx_features_attributes_keys',
               'idx_features_geometry_nn',
               'idx_features_geometry_3d',
               'idx_features_envelope',
               'idx_features_attr_dates',
               'idx_features_attr_timestamps',
               'idx_features_temporal_attrs')
        """;

    private const string IndexNamesSql =
        """
        SELECT string_agg(indexname, ',' ORDER BY indexname)
          FROM pg_indexes
         WHERE schemaname = 'honua' AND tablename = 'features'
        """;

    private const string ExpectedIndexNames =
        "features_pkey,idx_features_attr_dates,idx_features_attr_timestamps,idx_features_attributes," +
        "idx_features_attributes_gin,idx_features_attributes_keys,idx_features_envelope," +
        "idx_features_geography,idx_features_geometry,idx_features_geometry_3d,idx_features_geometry_nn," +
        "idx_features_layer_id,idx_features_layer_objectid,idx_features_temporal_attrs";

    private const string IndexShapeSql =
        """
        SELECT format(
                   'btree:%s,gin:%s,gist:%s,partial:%s',
                   count(*) FILTER (WHERE access_method.amname = 'btree'),
                   count(*) FILTER (WHERE access_method.amname = 'gin'),
                   count(*) FILTER (WHERE access_method.amname = 'gist'),
                   count(*) FILTER (WHERE index.indpred IS NOT NULL))
          FROM pg_index AS index
          JOIN pg_class AS index_relation ON index_relation.oid = index.indexrelid
          JOIN pg_class AS table_relation ON table_relation.oid = index.indrelid
          JOIN pg_namespace AS namespace ON namespace.oid = table_relation.relnamespace
          JOIN pg_am AS access_method ON access_method.oid = index_relation.relam
         WHERE namespace.nspname = 'honua'
           AND table_relation.relname = 'features'
        """;

    private const string IndexOidsSql =
        """
        SELECT string_agg(indexrelid::text, ',' ORDER BY indexrelid)
          FROM pg_index
         WHERE indrelid = 'honua.features'::regclass
        """;

    private const string SequenceIdentitySql =
        "SELECT pg_get_serial_sequence('honua.features', 'objectid')";

    private const string SequenceOidSql =
        "SELECT pg_get_serial_sequence('honua.features', 'objectid')::regclass::oid::bigint";

    private const string SequenceStateSql =
        "SELECT format('%s:%s', last_value, is_called) FROM honua.features_objectid_seq";

    private const string FeatureMaxObjectIdSql =
        "SELECT max(objectid) FROM honua.features";

    private const string FeatureChangeMaxObjectIdSql =
        "SELECT max(objectid) FROM honua.feature_changes";

    private const string InsertDefaultFeatureSql =
        """
        INSERT INTO honua.features (layer_id, geometry, attributes)
        VALUES (90998, NULL, jsonb_build_object('source', 'sequence-regression'))
        RETURNING objectid
        """;

    private const string MigrationIndexReferenceTableSql =
        """
        CREATE SCHEMA migration_index_contract;
        CREATE TABLE migration_index_contract.features (
            objectid BIGSERIAL PRIMARY KEY,
            layer_id INT NOT NULL,
            geometry GEOMETRY,
            attributes JSONB,
            created_at TIMESTAMPTZ DEFAULT NOW(),
            updated_at TIMESTAMPTZ DEFAULT NOW()
        );
        """;

    private const string IndexDefinitionMismatchCountSql =
        """
        WITH recovered AS (
            SELECT index_relation.relname AS index_name,
                   regexp_replace(
                       regexp_replace(
                           pg_get_indexdef(index_relation.oid),
                           '(honua|migration_index_contract)\.',
                           '{schema}.',
                           'g'),
                       '[[:space:]]+',
                       ' ',
                       'g') AS definition
              FROM pg_index AS index
              JOIN pg_class AS index_relation ON index_relation.oid = index.indexrelid
              JOIN pg_class AS table_relation ON table_relation.oid = index.indrelid
              JOIN pg_namespace AS namespace ON namespace.oid = table_relation.relnamespace
             WHERE namespace.nspname = 'honua'
               AND table_relation.relname = 'features'
        ), expected AS (
            SELECT index_relation.relname AS index_name,
                   regexp_replace(
                       regexp_replace(
                           pg_get_indexdef(index_relation.oid),
                           '(honua|migration_index_contract)\.',
                           '{schema}.',
                           'g'),
                       '[[:space:]]+',
                       ' ',
                       'g') AS definition
              FROM pg_index AS index
              JOIN pg_class AS index_relation ON index_relation.oid = index.indexrelid
              JOIN pg_class AS table_relation ON table_relation.oid = index.indrelid
              JOIN pg_namespace AS namespace ON namespace.oid = table_relation.relnamespace
             WHERE namespace.nspname = 'migration_index_contract'
               AND table_relation.relname = 'features'
        )
        SELECT count(*)
          FROM recovered
          FULL JOIN expected USING (index_name)
         WHERE recovered.definition IS DISTINCT FROM expected.definition
        """;

    private const string TriggerCountSql =
        """
        SELECT count(*)
          FROM pg_trigger
         WHERE tgrelid = 'honua.features'::regclass
           AND tgname = 'trigger_track_feature_changes'
           AND NOT tgisinternal
        """;

    private const string TriggerOidSql =
        """
        SELECT oid::bigint
          FROM pg_trigger
         WHERE tgrelid = 'honua.features'::regclass
           AND tgname = 'trigger_track_feature_changes'
           AND NOT tgisinternal
        """;

    private const string TriggerFunctionSql =
        """
        SELECT quote_ident(function_namespace.nspname)
               || '.' || quote_ident(target_function.proname)
               || '(' || pg_get_function_identity_arguments(target_function.oid) || ')'
          FROM pg_trigger AS trigger
          JOIN pg_proc AS target_function ON target_function.oid = trigger.tgfoid
          JOIN pg_namespace AS function_namespace ON function_namespace.oid = target_function.pronamespace
         WHERE trigger.tgrelid = 'honua.features'::regclass
           AND trigger.tgname = 'trigger_track_feature_changes'
           AND NOT trigger.tgisinternal
        """;

    private const string TrackingFunctionOidSql =
        "SELECT 'honua.track_feature_changes()'::regprocedure::oid::bigint";

    private const string StorageObjectIdOnlyResolverSql =
        """
        CREATE OR REPLACE FUNCTION honua.resolve_feature_public_objectid(
            target_layer_id INT,
            storage_objectid BIGINT,
            row_attributes JSONB)
        RETURNS BIGINT AS $$
        BEGIN
            RETURN storage_objectid;
        END;
        $$ LANGUAGE plpgsql STABLE;
        """;

    private const string RaisingTrackingFunctionSql =
        """
        CREATE OR REPLACE FUNCTION honua.track_feature_changes()
        RETURNS TRIGGER AS $$
        BEGIN
            RAISE EXCEPTION 'hostile tracker';
        END;
        $$ LANGUAGE plpgsql;
        """;

    private const string TriggerTypeSql =
        """
        SELECT tgtype::bigint
          FROM pg_trigger
         WHERE tgrelid = 'honua.features'::regclass
           AND tgname = 'trigger_track_feature_changes'
           AND NOT tgisinternal
        """;

    private const string TriggerAttributesSql =
        """
        SELECT tgattr::text
          FROM pg_trigger
         WHERE tgrelid = 'honua.features'::regclass
           AND tgname = 'trigger_track_feature_changes'
           AND NOT tgisinternal
        """;

    private const string ReplaceTriggerWithColumnRestrictedDefinitionSql =
        """
        DROP TRIGGER trigger_track_feature_changes ON honua.features;
        CREATE TRIGGER trigger_track_feature_changes
            AFTER INSERT OR DELETE OR UPDATE OF objectid ON honua.features
            FOR EACH ROW
            EXECUTE FUNCTION honua.track_feature_changes();
        """;

    private const string RestoreCanonicalTriggerSql =
        """
        DROP TRIGGER trigger_track_feature_changes ON honua.features;
        CREATE TRIGGER trigger_track_feature_changes
            AFTER INSERT OR UPDATE OR DELETE ON honua.features
            FOR EACH ROW
            EXECUTE FUNCTION honua.track_feature_changes();
        """;

    private const string ReplaceTriggerWithHostileDefinitionSql =
        """
        CREATE OR REPLACE FUNCTION honua.hostile_track_feature_changes()
        RETURNS TRIGGER AS $$
        BEGIN
            RETURN NEW;
        END;
        $$ LANGUAGE plpgsql;
        DROP TRIGGER trigger_track_feature_changes ON honua.features;
        CREATE TRIGGER trigger_track_feature_changes
            AFTER INSERT OR UPDATE OR DELETE ON honua.features
            FOR EACH ROW
            EXECUTE FUNCTION honua.hostile_track_feature_changes();
        """;

    private const string PublicationIdentitySql =
        """
        SELECT jsonb_agg(
                   jsonb_build_array(
                       publication->'metadata'->>'id',
                       publication->>'serviceId',
                       publication->>'resourceId',
                       publication->>'storageBindingId')
                   ORDER BY publication->'metadata'->>'id')::text
          FROM honua.metadata_v2_current current_revision
          JOIN honua.metadata_v2_snapshots snapshot
            ON snapshot.environment = current_revision.environment
           AND snapshot.revision = current_revision.revision
         CROSS JOIN LATERAL jsonb_array_elements(snapshot.document->'publications') publication
         WHERE current_revision.environment = 'seed-regression'
           AND publication->'metadata'->>'id' LIKE 'pub-demo-stac-%'
        """;

    private const string FeatureStateSql =
        """
        SELECT count(*)::text || ':' || md5(string_agg(
                   objectid::text || '|' || layer_id::text || '|' || ST_AsEWKT(geometry) || '|' || attributes::text,
                   E'\n' ORDER BY objectid))
          FROM honua.features
        """;

    private const string FeatureChangeStateSql =
        """
        SELECT count(*)::text || ':' || COALESCE(md5(string_agg(
                   change_id::text || '|' || generation::text || '|' || layer_id::text || '|' ||
                   objectid::text || '|' || operation::text,
                   E'\n' ORDER BY change_id)), '')
          FROM honua.feature_changes
        """;

    private const string ChangeTrackingOperationsSql =
        """
        SELECT string_agg(operation::text || ':' || public_objectid::text, ',' ORDER BY change_id)
          FROM honua.feature_changes
         WHERE layer_id = 90999 AND objectid = 990001
        """;

    private const string NonIncreasingChangeGenerationCountSql =
        """
        SELECT count(*)
          FROM (
                SELECT generation,
                       lag(generation) OVER (ORDER BY change_id) AS previous_generation
                  FROM honua.feature_changes
                 WHERE layer_id = 90999 AND objectid = 990001
               ) changes
         WHERE previous_generation IS NOT NULL
           AND generation <= previous_generation
        """;

    private const string ChangeTrackingExerciseSql =
        """
        INSERT INTO honua.layers (
            layer_id,
            layer_name,
            table_name,
            geometry_type,
            primary_key_column)
        VALUES (90999, 'Change Tracking Exercise', 'features', 'Point', 'custom_id');
        INSERT INTO honua.features (objectid, layer_id, geometry, attributes)
        VALUES (
            990001,
            90999,
            ST_SetSRID(ST_MakePoint(-156.5, 20.8), 4326),
            '{"custom_id":880001,"stage":"insert"}'::jsonb);
        UPDATE honua.features
           SET attributes = '{"custom_id":880001,"stage":"update"}'::jsonb
         WHERE objectid = 990001 AND layer_id = 90999;
        DELETE FROM honua.features
         WHERE objectid = 990001 AND layer_id = 90999;
        """;

    private const string ChangeTrackingTablesSql =
        """
        CREATE SEQUENCE honua.sync_generation;
        CREATE TABLE honua.layers (
            layer_id INT PRIMARY KEY,
            layer_name TEXT NOT NULL,
            table_name TEXT NOT NULL,
            geometry_type TEXT NOT NULL,
            primary_key_column TEXT NOT NULL DEFAULT 'objectid'
        );
        CREATE TABLE honua.replicas (
            replica_id TEXT PRIMARY KEY,
            replica_name TEXT NOT NULL,
            service_id TEXT NOT NULL,
            sync_model TEXT NOT NULL,
            layer_ids INT[] NOT NULL,
            created_at TIMESTAMPTZ NOT NULL,
            last_sync_time TIMESTAMPTZ NOT NULL,
            last_sync_generation BIGINT NOT NULL DEFAULT 0,
            upload_base_generation BIGINT NOT NULL DEFAULT 0
        );
        CREATE TABLE honua.feature_changes (
            change_id BIGSERIAL PRIMARY KEY,
            generation BIGINT NOT NULL,
            layer_id INT NOT NULL,
            objectid BIGINT NOT NULL,
            operation SMALLINT NOT NULL,
            changed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
            version_id UUID,
            actor TEXT,
            source SMALLINT,
            operation_name TEXT,
            source_id TEXT,
            public_objectid BIGINT
        );
        """;

    private const string RetainedFeatureChangeSql =
        """
        INSERT INTO honua.feature_changes (
            generation,
            layer_id,
            objectid,
            operation,
            public_objectid)
        VALUES (
            nextval('honua.sync_generation'),
            90997,
            990000000,
            1,
            990000000)
        """;

    private const string RetainedReplicaSql =
        """
        INSERT INTO honua.replicas
            (replica_id, replica_name, service_id, sync_model, layer_ids, created_at, last_sync_time)
        VALUES
            ('retained-replica', 'Retained replica', 'demo-stac', 'perReplica', ARRAY[90810], now(), now())
        """;

    private const string MetadataV2TablesSql =
        """
        CREATE SCHEMA honua;

        CREATE TABLE honua.metadata_v2_snapshots (
            environment text NOT NULL,
            revision bigint NOT NULL,
            schema_version text NOT NULL,
            api_version text NOT NULL,
            document jsonb NOT NULL,
            etag text NOT NULL,
            generated_at timestamptz NOT NULL,
            PRIMARY KEY (environment, revision)
        );

        CREATE TABLE honua.metadata_v2_current (
            environment text PRIMARY KEY,
            revision bigint NOT NULL,
            etag text NOT NULL,
            activated_at timestamptz NOT NULL DEFAULT now(),
            FOREIGN KEY (environment, revision)
                REFERENCES honua.metadata_v2_snapshots(environment, revision)
        );

        CREATE TABLE honua.metadata_v2_resources_idx (
            environment text NOT NULL, revision bigint NOT NULL, resource_id text NOT NULL,
            name text NOT NULL, namespace text NULL, type text NOT NULL,
            primary_storage_binding_id text NULL,
            PRIMARY KEY (environment, revision, resource_id),
            FOREIGN KEY (environment, revision)
                REFERENCES honua.metadata_v2_snapshots(environment, revision) ON DELETE CASCADE
        );
        CREATE TABLE honua.metadata_v2_services_idx (
            environment text NOT NULL, revision bigint NOT NULL, service_id text NOT NULL,
            name text NOT NULL, service_type text NOT NULL, route text NULL,
            PRIMARY KEY (environment, revision, service_id),
            FOREIGN KEY (environment, revision)
                REFERENCES honua.metadata_v2_snapshots(environment, revision) ON DELETE CASCADE
        );
        CREATE TABLE honua.metadata_v2_publications_idx (
            environment text NOT NULL, revision bigint NOT NULL, publication_id text NOT NULL,
            service_id text NOT NULL, resource_id text NOT NULL, storage_binding_id text NULL,
            publication_type text NOT NULL, path text NULL, layer_index int NULL, service_local_id text NULL,
            PRIMARY KEY (environment, revision, publication_id),
            FOREIGN KEY (environment, revision)
                REFERENCES honua.metadata_v2_snapshots(environment, revision) ON DELETE CASCADE
        );
        CREATE TABLE honua.metadata_v2_storage_bindings_idx (
            environment text NOT NULL, revision bigint NOT NULL, storage_binding_id text NOT NULL,
            resource_id text NOT NULL, connection_id text NULL, storage_type text NOT NULL, locator text NOT NULL,
            PRIMARY KEY (environment, revision, storage_binding_id),
            FOREIGN KEY (environment, revision)
                REFERENCES honua.metadata_v2_snapshots(environment, revision) ON DELETE CASCADE
        );
        CREATE TABLE honua.metadata_v2_connections_idx (
            environment text NOT NULL, revision bigint NOT NULL, connection_id text NOT NULL,
            name text NOT NULL, type text NOT NULL, provider text NULL,
            PRIMARY KEY (environment, revision, connection_id),
            FOREIGN KEY (environment, revision)
                REFERENCES honua.metadata_v2_snapshots(environment, revision) ON DELETE CASCADE
        );
        """;

    private sealed class TestConnectionProvider(NpgsqlDataSource dataSource, string schemaName)
        : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString() => dataSource.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SET search_path TO \"{schemaName}\", public;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var connection = await OpenConnectionAsync(cancellationToken);
            try
            {
                var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
                return (connection, transaction);
            }
            catch
            {
                await connection.DisposeAsync();
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

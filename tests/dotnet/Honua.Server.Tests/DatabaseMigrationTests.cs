// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using DbUp;
using FluentAssertions;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Honua.Server.Startup;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests;

/// <summary>
/// Tests for database migration functionality using DbUp.
/// </summary>
/// <remarks>
/// These tests run raw DbUp migrations and seeds that target the hard-coded
/// <c>honua</c>/<c>honua_data</c> schemas and take catalog-wide DDL locks. The
/// <c>Database.*</c> collections run in parallel against a single shared PostGIS
/// database and rely on per-test <c>search_path</c> schema isolation. That isolation
/// does NOT protect fixed-name schemas or catalog locks, so these migration/seed tests
/// otherwise collide with sibling collections that also create/populate the
/// <c>honua</c> schema (intermittent count-assertion failures and <c>40P01</c>
/// deadlocks). Each instance therefore provisions its own dedicated, uniquely-named
/// database so its schema, DDL locks, and seed data are fully private.
/// </remarks>
[Protocol(TestProtocols.TestQuality)]
[Collection("Database.CoreFeatureStore")]
public sealed class DatabaseMigrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();
    private string _connectionString = null!;
    private string _schemaName = null!;
    private string _databaseName = null!;

    public async Task InitializeAsync()
    {
        await _postgres.InitializeAsync();

        // Dedicated database: isolates the literal honua/honua_data schemas and the
        // catalog-level DDL locks DbUp takes, which per-test search_path schemas cannot.
        _connectionString = await _postgres.CreateIsolatedDatabaseAsync(nameof(DatabaseMigrationTests));
        _databaseName = new Npgsql.NpgsqlConnectionStringBuilder(_connectionString).Database!;

        // A per-test schema inside the dedicated database keeps unqualified objects
        // (the `features` table and seed inserts) resolving through `current_schema()`,
        // preserving the storage-binding semantics the seed asserts.
        _schemaName = $"migration_{Guid.NewGuid():N}";
        await using var connection = new Npgsql.NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            CREATE SCHEMA {_schemaName};
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await _postgres.DropDatabaseAsync(_databaseName);
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task DbUpMigrations_OnFreshDatabase_CreatesSchemaAndTables()
    {
        // Arrange
        // Configure connection string with the isolated schema in search_path
        var connectionStringBuilder = new Npgsql.NpgsqlConnectionStringBuilder(_connectionString)
        {
            SearchPath = $"{_schemaName},public"
        };

        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionStringBuilder.ToString(), _schemaName)
            .JournalToPostgresqlTable(_schemaName, "schema_versions")
            .WithScriptsEmbeddedInAssembly(Assembly.GetAssembly(typeof(Program))!)
            .WithVariable("HonuaSchema", "\"honua\"")
            .WithTransaction()
            .Build();

        // Act
        var result = upgrader.PerformUpgrade();

        // Assert
        if (!result.Successful)
        {
            Console.WriteLine($"Migration failed. Error: {result.Error}");
        }
        result.Successful.Should().BeTrue($"migrations should complete successfully. Error: {result.Error}");
        result.Scripts.Should().HaveCountGreaterThan(0, "at least one migration script should exist");

        // Verify schema was created
        await using var connection = await OpenSchemaConnectionAsync();

        // Check metadata and operational-data schemas exist
        await using var schemaCmd = connection.CreateCommand();
        schemaCmd.CommandText = """
            SELECT COUNT(*)::int
            FROM information_schema.schemata
            WHERE schema_name IN ('honua', 'honua_data')
            """;
        var schemaCount = (int)(await schemaCmd.ExecuteScalarAsync())!;
        schemaCount.Should().Be(2, "metadata and operational-data schemas should be created");

        // Check PostGIS extension is enabled
        await using var postgisCmd = connection.CreateCommand();
        postgisCmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'postgis')";
        var postgisExists = (bool)(await postgisCmd.ExecuteScalarAsync())!;
        postgisExists.Should().BeTrue("PostGIS extension should be enabled");

        // Verify tables exist
        await using var tablesCmd = connection.CreateCommand();
        tablesCmd.CommandText = """
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_schema = 'honua'
            AND table_name IN ('services', 'layers', 'service_layers', 'layer_fields', 'relationships', 'attachments')
            """;
        var tablesExist = (int)(long)(await tablesCmd.ExecuteScalarAsync())!;
        tablesExist.Should().Be(6, "core metadata tables should exist");

        await using var roleTombstoneColumnCmd = connection.CreateCommand();
        roleTombstoneColumnCmd.CommandText = """
            SELECT COUNT(*)::int
            FROM information_schema.columns
            WHERE table_schema = 'honua'
              AND table_name = 'rbac_roles'
              AND column_name = 'deleted_at'
              AND data_type = 'timestamp with time zone'
              AND is_nullable = 'YES'
            """;
        var roleTombstoneColumnCount = (int)(await roleTombstoneColumnCmd.ExecuteScalarAsync())!;
        roleTombstoneColumnCount.Should().Be(1,
            "migration 107 must add the nullable role tombstone used by atomic role deletion");

        // Verify foreign key constraints
        await using var constraintsCmd = connection.CreateCommand();
        constraintsCmd.CommandText = """
            SELECT COUNT(*) FROM information_schema.table_constraints
            WHERE constraint_schema = 'honua'
            AND constraint_type = 'FOREIGN KEY'
            """;
        var constraintsExist = (int)(long)(await constraintsCmd.ExecuteScalarAsync())!;
        constraintsExist.Should().BeGreaterThan(0, "foreign key constraints should exist");

        // Verify indexes
        await using var indexesCmd = connection.CreateCommand();
        indexesCmd.CommandText = """
            SELECT COUNT(*) FROM pg_indexes
            WHERE schemaname = 'honua'
            AND indexname IN (
                'idx_service_layers_service_name',
                'idx_service_layers_layer_id',
                'idx_layer_fields_layer_id',
                'idx_relationships_layer_id',
                'idx_relationships_related_layer_id',
                'idx_relationships_lookup',
                'idx_attachments_feature_layer',
                'idx_attachments_created_at',
                'idx_attachments_layer_id'
            )
            """;
        var indexesExist = (int)(long)(await indexesCmd.ExecuteScalarAsync())!;
        indexesExist.Should().Be(9, "performance indexes should exist");

        await using var topologyGenerationCmd = connection.CreateCommand();
        topologyGenerationCmd.CommandText = """
            SELECT COUNT(*)::int
            FROM honua.network_topology_generations
            WHERE dataset_id = 'default'
              AND generation = 1
              AND state = 'active'
              AND row_version = 1
              AND edge_table = 'public.ways'
              AND vertex_table = 'public.ways_vertices_pgr'
              AND activated_at IS NOT NULL
            """;
        var activeTopologyGenerations = (int)(await topologyGenerationCmd.ExecuteScalarAsync())!;
        activeTopologyGenerations.Should().Be(1,
            "a fresh database should preserve the default solve mapping as one active generation");

        // A custom protocol id must remain recoverable from the change log after its feature row is
        // deleted. Replica conflict detection can no longer resolve the live row at that point.
        await using (var customIdChangeCmd = connection.CreateCommand())
        {
            customIdChangeCmd.CommandText = """
                INSERT INTO honua.layers
                    (layer_id, layer_name, table_name, primary_key_column, geometry_type)
                VALUES
                    (990105, 'migration_custom_id', 'features', 'asset_id', 'Point');

                INSERT INTO features (objectid, layer_id, attributes)
                VALUES (190105, 990105, '{"asset_id": 700105}'::jsonb);

                DELETE FROM features
                WHERE objectid = 190105;

                SELECT public_objectid
                FROM honua.feature_changes
                WHERE layer_id = 990105
                  AND objectid = 190105
                  AND operation = 3
                ORDER BY generation DESC
                LIMIT 1;
                """;
            var deletedPublicObjectId = (long)(await customIdChangeCmd.ExecuteScalarAsync())!;
            deletedPublicObjectId.Should().Be(700105,
                "the delete trigger should persist the configured public id.primary from OLD attributes");
        }

        // A protocol-facing primary id is an object identity, not an ordinary editable attribute.
        // Rejecting identity changes prevents the change log from losing the alias that an offline
        // replica may still use to address the row.
        await using (var arrangeImmutableCustomIdCmd = connection.CreateCommand())
        {
            arrangeImmutableCustomIdCmd.CommandText = """
                INSERT INTO features (objectid, layer_id, attributes)
                VALUES (190107, 990105, '{"asset_id": 700107}'::jsonb);
                """;
            await arrangeImmutableCustomIdCmd.ExecuteNonQueryAsync();
        }

        await using (var mutateCustomIdCmd = connection.CreateCommand())
        {
            mutateCustomIdCmd.CommandText = """
                UPDATE features
                SET attributes = jsonb_set(attributes, '{asset_id}', '700108'::jsonb)
                WHERE objectid = 190107;
                """;
            var exception = await Assert.ThrowsAsync<Npgsql.PostgresException>(
                () => mutateCustomIdCmd.ExecuteNonQueryAsync());
            exception.SqlState.Should().Be(Npgsql.PostgresErrorCodes.CheckViolation);
            exception.MessageText.Should().Contain("Cannot change the public id.primary");
        }

        await using (var preservedCustomIdCmd = connection.CreateCommand())
        {
            preservedCustomIdCmd.CommandText = """
                SELECT attributes ->> 'asset_id'
                FROM features
                WHERE objectid = 190107;
                """;
            var preservedPublicObjectId = (string)(await preservedCustomIdCmd.ExecuteScalarAsync())!;
            preservedPublicObjectId.Should().Be("700107",
                "a rejected identity mutation must leave the original row and alias intact");
        }

        await using (var arrangeImmutableBranchIdCmd = connection.CreateCommand())
        {
            arrangeImmutableBranchIdCmd.CommandText = """
                INSERT INTO honua.gdb_versions (version_id, version_name, owner)
                VALUES ('00000000-0000-0000-0000-000000990105', 'migration-immutable-id', 'migration');
                """;
            await arrangeImmutableBranchIdCmd.ExecuteNonQueryAsync();
        }

        await using (var mutateBranchCustomIdCmd = connection.CreateCommand())
        {
            mutateBranchCustomIdCmd.CommandText = """
                INSERT INTO honua.version_edits
                    (version_id, layer_id, objectid, operation, attributes, base_attributes)
                VALUES
                    ('00000000-0000-0000-0000-000000990105', 990105, 190107, 2,
                     '{"asset_id": 700108}'::jsonb, '{"asset_id": 700107}'::jsonb);
                """;
            var exception = await Assert.ThrowsAsync<Npgsql.PostgresException>(
                () => mutateBranchCustomIdCmd.ExecuteNonQueryAsync());
            exception.SqlState.Should().Be(Npgsql.PostgresErrorCodes.CheckViolation);
            exception.MessageText.Should().Contain("Cannot change the public id.primary");
        }

        await using (var deleteBranchCreatedCustomIdCmd = connection.CreateCommand())
        {
            deleteBranchCreatedCustomIdCmd.CommandText = """
                INSERT INTO honua.version_edits
                    (version_id, layer_id, objectid, operation, attributes)
                VALUES
                    ('00000000-0000-0000-0000-000000990105', 990105, 190109, 1,
                     '{"asset_id": 700109}'::jsonb);

                UPDATE honua.version_edits
                SET operation = 3,
                    attributes = NULL
                WHERE version_id = '00000000-0000-0000-0000-000000990105'
                  AND layer_id = 990105
                  AND objectid = 190109;

                SELECT public_objectid
                FROM honua.feature_changes
                WHERE version_id = '00000000-0000-0000-0000-000000990105'
                  AND layer_id = 990105
                  AND objectid = 190109
                  AND operation = 3
                ORDER BY generation DESC
                LIMIT 1;
                """;
            var deletedBranchPublicObjectId = (long)(await deleteBranchCreatedCustomIdCmd.ExecuteScalarAsync())!;
            deletedBranchPublicObjectId.Should().Be(700109,
                "a branch-created delete should retain the custom id from its prior branch row");
        }

        // A pre-migration custom-id delete has no surviving row image from which migration 105 can
        // recover its public alias. Invalidate only replicas whose cursors precede that unsafe
        // delete, forcing those clients to create a fresh replica instead of silently missing it.
        await using (var arrangeLegacyDeleteCmd = connection.CreateCommand())
        {
            arrangeLegacyDeleteCmd.CommandText = """
                INSERT INTO honua.feature_changes
                    (generation, layer_id, objectid, public_objectid, operation)
                VALUES
                    (nextval('honua.sync_generation'), 990105, 190106, NULL, 3);

                INSERT INTO honua.replicas
                    (replica_id, replica_name, service_id, sync_model, layer_ids, last_sync_generation)
                VALUES
                    ('legacy-custom-id', 'legacy custom id', 'test', 'perReplica', ARRAY[990105], 0),
                    ('caught-up-custom-id', 'caught up custom id', 'test', 'perReplica', ARRAY[990105],
                        (SELECT MAX(generation) FROM honua.feature_changes
                         WHERE layer_id = 990105 AND objectid = 190106 AND operation = 3)),
                    ('transient-custom-id', 'transient custom id', 'test', 'perReplica', ARRAY[990105],
                        (SELECT MAX(generation) FROM honua.feature_changes)),
                    ('ordinary-id', 'ordinary id', 'test', 'perReplica', ARRAY[0], 0);

                INSERT INTO honua.feature_changes
                    (generation, layer_id, objectid, public_objectid, operation)
                VALUES
                    (nextval('honua.sync_generation'), 990105, 190110, NULL, 1),
                    (nextval('honua.sync_generation'), 990105, 190110, NULL, 3);
                """;
            await arrangeLegacyDeleteCmd.ExecuteNonQueryAsync();
        }

        var publicIdMigrationSql = await ReadEmbeddedMigrationAsync("105_AddChangeLogPublicObjectId.sql");
        await using (var reapplyPublicIdMigrationCmd = connection.CreateCommand())
        {
            reapplyPublicIdMigrationCmd.CommandText = publicIdMigrationSql;
            await reapplyPublicIdMigrationCmd.ExecuteNonQueryAsync();
        }

        await using (var invalidatedReplicaCmd = connection.CreateCommand())
        {
            invalidatedReplicaCmd.CommandText = """
                SELECT replica_id
                FROM honua.replicas
                WHERE replica_id IN (
                    'legacy-custom-id',
                    'caught-up-custom-id',
                    'transient-custom-id',
                    'ordinary-id')
                ORDER BY replica_id;
                """;
            await using var reader = await invalidatedReplicaCmd.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(0).Should().Be("caught-up-custom-id");
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(0).Should().Be("ordinary-id");
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(0).Should().Be("transient-custom-id");
            (await reader.ReadAsync()).Should().BeFalse();
        }
    }

    [Fact]
    public async Task CanonicalRunner_OnFreshDatabase_JournalsBothMigrationRootsAndVerifiesPhysicalFloor()
    {
        var runner = new PostgresDatabaseMigrationRunner(
            new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest),
            ServerCoreSchemaMigrations.Manifest);

        var result = await runner.RunMigrationsAsync(
            _connectionString,
            Assembly.GetAssembly(typeof(Program))!);

        result.Successful.Should().BeTrue(
            $"both numbered migration roots should apply through the canonical runner. Error: {result.ErrorMessage}");
        result.AppliedScripts.Should().Contain(PostgresCoreSchemaGuard.RasterLayerStatisticsMigration);
        result.AppliedScripts.Should().Contain(PostgresCoreSchemaGuard.RasterLateProvisioningMigration);
        result.AppliedScripts.Should().Contain(ServerCoreSchemaMigrations.Manifest.MetadataV2SnapshotMigration);
        result.AppliedScripts.Should().Contain(ServerCoreSchemaMigrations.Manifest.RasterExternalStorageMigration);
        result.AppliedScripts.Should().Contain(ServerCoreSchemaMigrations.Manifest.SensorThingsMigration);
        result.AppliedScripts.Should().Contain(ServerCoreSchemaMigrations.Manifest.GovernedLineageMigration);
        result.AppliedScripts.Should().NotContain(ServerCoreSchemaMigrations.Manifest.ConfiguredSchemaAdoptionMigration,
            "the contract-gated adoption script has no work in the default schema");

        var existingDatabasePlan = await runner.PlanMigrationsAsync(
            _connectionString,
            Assembly.GetAssembly(typeof(Program))!);
        existingDatabasePlan.Successful.Should().BeTrue();
        existingDatabasePlan.PendingScripts.Should().NotContain(
            ServerCoreSchemaMigrations.Manifest.ConfiguredSchemaAdoptionMigration);
        existingDatabasePlan.HasContractScripts.Should().BeFalse(
            "an existing default-schema deployment must not require an adoption nonce for a no-op");

        var restart = await runner.RunMigrationsAsync(
            _connectionString,
            Assembly.GetAssembly(typeof(Program))!);
        restart.Successful.Should().BeTrue();
        restart.AppliedScripts.Should().BeEmpty();

        await using var connection = new Npgsql.NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)::int
            FROM public.schema_versions
            WHERE scriptname IN (
                'Honua.Postgres.Migrations.003_CreateRasterLayerStatistics.sql',
                'Honua.Postgres.Migrations.005_CompleteLateRasterProvisioning.sql',
                'Honua.Server.Migrations.031_CreateMetadataV2Snapshot.sql',
                'Honua.Server.Migrations.055_SetRasterDataExternalStorage.sql',
                'Honua.Server.Migrations.059_CreateSensorThings.sql',
                'Honua.Server.Migrations.110_PreserveGovernedLineage.sql')
            """;
        (await command.ExecuteScalarAsync()).Should().Be(6,
            "upgrade and restore receipts use one journal denominator for both numbered roots");

        var guard = new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest);
        var verify = () => guard.VerifyAsync(_connectionString);
        await verify.Should().NotThrowAsync("a fully migrated restore candidate must pass the guarded DR floor");
    }

    [Fact]
    public async Task CoreSchemaGuard_WhenRasterDataIsMissing_RejectsJournaledMigration()
    {
        var guard = new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest);
        var runner = new PostgresDatabaseMigrationRunner(guard, ServerCoreSchemaMigrations.Manifest);
        var result = await runner.RunMigrationsAsync(
            _connectionString,
            Assembly.GetAssembly(typeof(Program))!);
        result.Successful.Should().BeTrue();

        await using (var connection = new Npgsql.NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE honua.raster_data CASCADE;";
            await command.ExecuteNonQueryAsync();
        }

        var verify = () => guard.VerifyAsync(_connectionString);
        var exception = await verify.Should().ThrowAsync<Honua.Core.Features.Infrastructure.Domain.DatabaseSchemaFloorException>();
        exception.Which.MigrationScript.Should().Be(PostgresCoreSchemaGuard.RasterTablesMigration);
        exception.Which.FailureKind.Should().Be(
            Honua.Core.Features.Infrastructure.Domain.DatabaseSchemaFloorFailureKind.JournalClaimsMissingSchema);
        exception.Which.Message.Should().Contain("required raster table(s) are absent");
        exception.Which.Message.Should().Contain("honua.raster_data");
    }

    [Fact]
    public async Task DbUpMigrations_OnExistingDatabase_IsIdempotent()
    {
        // Arrange
        // Configure connection string with the isolated schema in search_path
        var connectionStringBuilder = new Npgsql.NpgsqlConnectionStringBuilder(_connectionString)
        {
            SearchPath = $"{_schemaName},public"
        };

        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionStringBuilder.ToString(), _schemaName)
            .JournalToPostgresqlTable(_schemaName, "schema_versions")
            .WithScriptsEmbeddedInAssembly(Assembly.GetAssembly(typeof(Program))!)
            .WithVariable("HonuaSchema", "\"honua\"")
            .WithTransaction()
            .Build();

        // Act - Run migrations twice
        var firstResult = upgrader.PerformUpgrade();
        var secondResult = upgrader.PerformUpgrade();

        // Assert
        firstResult.Successful.Should().BeTrue($"first migration should succeed. Error: {firstResult.Error}");
        secondResult.Successful.Should().BeTrue($"second migration should succeed. Error: {secondResult.Error}");

        firstResult.Scripts.Should().HaveCountGreaterThan(0, "first run should apply scripts");
        secondResult.Scripts.Should().BeEmpty("second run should apply no scripts");
    }

    [Fact]
    public async Task NetworkTopologyGenerationMigration_ExistingRegistry_BackfillsOnceAndIsRestartSafe()
    {
        await using var connection = await OpenSchemaConnectionAsync();
        await using (var arrange = connection.CreateCommand())
        {
            arrange.CommandText = """
                CREATE SCHEMA honua;
                CREATE TABLE honua.network_datasets (
                    id TEXT PRIMARY KEY,
                    edge_table TEXT NOT NULL,
                    vertex_table TEXT NOT NULL,
                    srid INTEGER NOT NULL,
                    topology_version INTEGER NOT NULL,
                    created_at TIMESTAMPTZ NOT NULL,
                    updated_at TIMESTAMPTZ NOT NULL
                );
                INSERT INTO honua.network_datasets
                    (id, edge_table, vertex_table, srid, topology_version, created_at, updated_at)
                VALUES
                    ('island', 'routing.island_edges', 'routing.island_vertices', 3857, 12,
                     '2026-01-01T00:00:00Z', '2026-02-01T00:00:00Z');
                """;
            await arrange.ExecuteNonQueryAsync();
        }

        var migrationSql = await ReadEmbeddedMigrationAsync("084_CreateNetworkTopologyGenerations.sql");
        var insertTriggerPosition = migrationSql.IndexOf(
            "CREATE TRIGGER network_datasets_seed_initial_generation",
            StringComparison.Ordinal);
        var updateTriggerPosition = migrationSql.IndexOf(
            "CREATE TRIGGER network_datasets_track_legacy_mapping_update",
            StringComparison.Ordinal);
        var backfillPosition = migrationSql.IndexOf(
            "-- Install the compatibility triggers before taking the backfill snapshot.",
            StringComparison.Ordinal);
        insertTriggerPosition.Should().BeGreaterThanOrEqualTo(0);
        updateTriggerPosition.Should().BeGreaterThan(insertTriggerPosition);
        backfillPosition.Should().BeGreaterThan(insertTriggerPosition,
            "the mixed-version INSERT trigger must be installed before the backfill snapshot");
        backfillPosition.Should().BeGreaterThan(updateTriggerPosition,
            "the mixed-version UPDATE trigger must be installed before the backfill snapshot");

        await using (var apply = connection.CreateCommand())
        {
            apply.CommandText = migrationSql;
            await apply.ExecuteNonQueryAsync();
        }

        await using (var sameNumberNonActive = connection.CreateCommand())
        {
            sameNumberNonActive.CommandText = """
                ALTER TABLE honua.network_datasets
                    DISABLE TRIGGER network_datasets_seed_initial_generation;
                INSERT INTO honua.network_datasets
                    (id, edge_table, vertex_table, srid, topology_version, created_at, updated_at)
                VALUES
                    ('ridge', 'routing.ridge_edges', 'routing.ridge_vertices', 4326, 4,
                     '2026-03-01T00:00:00Z', '2026-03-02T00:00:00Z');
                INSERT INTO honua.network_topology_generations
                    (dataset_id, generation, source_revision, state, row_version,
                     edge_table, vertex_table, srid, created_at, updated_at)
                VALUES
                    ('ridge', 4, 1, 'dirty', 1,
                     'routing.ridge_edges', 'routing.ridge_vertices', 4326,
                     '2026-03-01T00:00:00Z', '2026-03-02T00:00:00Z');
                ALTER TABLE honua.network_datasets
                    ENABLE TRIGGER network_datasets_seed_initial_generation;
                """;
            await sameNumberNonActive.ExecuteNonQueryAsync();
        }

        await using (var reapply = connection.CreateCommand())
        {
            reapply.CommandText = migrationSql;
            await reapply.ExecuteNonQueryAsync();
        }

        await using (var backfill = connection.CreateCommand())
        {
            backfill.CommandText = """
                SELECT generation, source_revision, state, row_version,
                       edge_table, vertex_table, srid, COUNT(*) OVER ()
                FROM honua.network_topology_generations
                WHERE dataset_id = 'island'
                """;
            await using var reader = await backfill.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt64(0).Should().Be(12);
            reader.GetInt64(1).Should().Be(0);
            reader.GetString(2).Should().Be("active");
            reader.GetInt64(3).Should().Be(1);
            reader.GetString(4).Should().Be("routing.island_edges");
            reader.GetString(5).Should().Be("routing.island_vertices");
            reader.GetInt32(6).Should().Be(3857);
            reader.GetInt64(7).Should().Be(1, "re-applying the migration must not allocate another generation");
        }

        await using (var collisionRecovery = connection.CreateCommand())
        {
            collisionRecovery.CommandText = """
                SELECT generation, state, COUNT(*) OVER ()
                FROM honua.network_topology_generations
                WHERE dataset_id = 'ridge'
                ORDER BY generation
                """;
            await using var reader = await collisionRecovery.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt64(0).Should().Be(4);
            reader.GetString(1).Should().Be("dirty");
            reader.GetInt64(2).Should().Be(2);
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt64(0).Should().Be(5,
                "backfill must allocate past a colliding non-active generation");
            reader.GetString(1).Should().Be("active");
        }

        await using (var oldReplicaInsert = connection.CreateCommand())
        {
            oldReplicaInsert.CommandText = """
                INSERT INTO honua.network_datasets
                    (id, edge_table, vertex_table, srid, topology_version, created_at, updated_at)
                VALUES
                    ('old-replica', 'routing.old_edges', 'routing.old_vertices', 4326, 7,
                     '2026-04-01T00:00:00Z', '2026-04-02T00:00:00Z');
                """;
            await oldReplicaInsert.ExecuteNonQueryAsync();
        }

        await using (var mixedVersionGeneration = connection.CreateCommand())
        {
            mixedVersionGeneration.CommandText = """
                SELECT generation, state, edge_table, vertex_table, COUNT(*) OVER ()
                FROM honua.network_topology_generations
                WHERE dataset_id = 'old-replica'
                """;
            await using var reader = await mixedVersionGeneration.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt64(0).Should().Be(7);
            reader.GetString(1).Should().Be("active");
            reader.GetString(2).Should().Be("routing.old_edges");
            reader.GetString(3).Should().Be("routing.old_vertices");
            reader.GetInt64(4).Should().Be(1,
                "a pre-084 registry insert must atomically seed exactly one generation");
        }

        await using (var oldReplicaUpdate = connection.CreateCommand())
        {
            oldReplicaUpdate.CommandText = """
                UPDATE honua.network_datasets
                SET edge_table = 'routing.old_edges_v2',
                    vertex_table = 'routing.old_vertices_v2',
                    srid = 3857,
                    updated_at = '2026-04-03T00:00:00Z'
                WHERE id = 'old-replica';
                """;
            (await oldReplicaUpdate.ExecuteNonQueryAsync()).Should().Be(1);
        }

        await using (var mixedVersionUpdateGenerations = connection.CreateCommand())
        {
            mixedVersionUpdateGenerations.CommandText = """
                SELECT generation, source_revision, state, row_version, edge_table, vertex_table, srid,
                       COUNT(*) OVER ()
                FROM honua.network_topology_generations
                WHERE dataset_id = 'old-replica'
                ORDER BY generation
                """;
            await using var reader = await mixedVersionUpdateGenerations.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt64(0).Should().Be(7);
            reader.GetInt64(1).Should().Be(0);
            reader.GetString(2).Should().Be("retired");
            reader.GetInt64(3).Should().Be(2);
            reader.GetString(4).Should().Be("routing.old_edges");
            reader.GetInt64(7).Should().Be(2);

            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt64(0).Should().Be(8);
            reader.GetInt64(1).Should().Be(1);
            reader.GetString(2).Should().Be("active");
            reader.GetInt64(3).Should().Be(1);
            reader.GetString(4).Should().Be("routing.old_edges_v2");
            reader.GetString(5).Should().Be("routing.old_vertices_v2");
            reader.GetInt32(6).Should().Be(3857);
        }

        await using var duplicateActive = connection.CreateCommand();
        duplicateActive.CommandText = """
            INSERT INTO honua.network_topology_generations
                (dataset_id, generation, state, edge_table, vertex_table, srid, activated_at)
            VALUES
                ('island', 13, 'active', 'routing.next_edges', 'routing.next_vertices', 3857, now())
            """;
        var exception = await Assert.ThrowsAsync<Npgsql.PostgresException>(
            () => duplicateActive.ExecuteNonQueryAsync());
        exception.SqlState.Should().Be(Npgsql.PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task DbUpMigrations_MobileOfflineDemoSeed_AppliesToCanonicalSchema()
    {
        // Arrange
        var connectionStringBuilder = new Npgsql.NpgsqlConnectionStringBuilder(_connectionString)
        {
            SearchPath = $"{_schemaName},public"
        };

        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionStringBuilder.ToString(), _schemaName)
            .JournalToPostgresqlTable(_schemaName, "schema_versions")
            .WithScriptsEmbeddedInAssembly(Assembly.GetAssembly(typeof(Program))!)
            .WithVariable("HonuaSchema", "\"honua\"")
            .WithTransaction()
            .Build();

        var migrationResult = upgrader.PerformUpgrade();
        migrationResult.Successful.Should().BeTrue($"migrations should complete successfully. Error: {migrationResult.Error}");

        var baselineSeedPath = RepositoryPaths.Resolve("tests", "seed", "mobile-offline-demo-v1.sql");
        var conflictSeedPath = RepositoryPaths.Resolve("tests", "seed", "mobile-offline-demo-conflict-delta.sql");

        // Act
        // The seeds run idempotent ALTER/INSERT DDL against the literal honua schema. Because
        // this test owns a dedicated database, that schema is fully private — no advisory lock or
        // cross-collection serialization is required to keep the mutation off the deadlock path.
        await using var connection = await OpenSchemaConnectionAsync();
        await using (var metadataPointers = connection.CreateCommand())
        {
            metadataPointers.CommandText = """
                INSERT INTO honua.metadata_v2_snapshots
                    (environment, revision, schema_version, api_version, document, etag, generated_at)
                VALUES
                    ('default', 42, '2.0.0-alpha.1', 'metadata.honua.io/v2alpha1', '{}'::jsonb, 'default-etag', now()),
                    ('unrelated', 42, '2.0.0-alpha.1', 'metadata.honua.io/v2alpha1', '{}'::jsonb, 'unrelated-etag', now());

                INSERT INTO honua.metadata_v2_current (environment, revision, etag)
                VALUES
                    ('default', 42, 'default-etag'),
                    ('unrelated', 42, 'unrelated-etag');
                """;
            await metadataPointers.ExecuteNonQueryAsync();
        }

        await ExecuteSeedFileAsync(connection, baselineSeedPath);
        await ExecuteSeedFileAsync(connection, conflictSeedPath);

        // Assert
        await using var countCmd = connection.CreateCommand();
        countCmd.CommandText = """
            SELECT COUNT(*)
            FROM features
            WHERE layer_id IN (68910, 68920)
            """;
        var featureCount = (long)(await countCmd.ExecuteScalarAsync())!;
        featureCount.Should().Be(5, "mobile offline baseline should seed the deterministic edit and context records");

        await using var metadataPointerCmd = connection.CreateCommand();
        metadataPointerCmd.CommandText = """
            SELECT environment
            FROM honua.metadata_v2_current
            ORDER BY environment
            """;
        await using (var metadataPointerReader = await metadataPointerCmd.ExecuteReaderAsync())
        {
            (await metadataPointerReader.ReadAsync()).Should().BeTrue();
            metadataPointerReader.GetString(0).Should().Be("unrelated");
            (await metadataPointerReader.ReadAsync()).Should().BeFalse();
        }

        await using var conflictCmd = connection.CreateCommand();
        conflictCmd.CommandText = """
            SELECT attributes ->> 'sync_version'
            FROM features
            WHERE layer_id = 68910
              AND objectid = 6891002
            """;
        var syncVersion = (string?)await conflictCmd.ExecuteScalarAsync();
        syncVersion.Should().Be("2", "conflict delta should advance the deterministic server-side conflict target");

        await using var serviceCmd = connection.CreateCommand();
        serviceCmd.CommandText = """
            SELECT COUNT(*)
            FROM honua.services s
            JOIN honua.service_layers sl ON sl.service_name = s.service_name
            JOIN honua.layers l ON l.layer_id = sl.layer_id
            WHERE s.service_name = 'mobile_offline_demo'
              AND l.layer_id IN (68910, 68920)
            """;
        var serviceLayerCount = (long)(await serviceCmd.ExecuteScalarAsync())!;
        serviceLayerCount.Should().Be(2, "fixture service should expose both mobile offline layers");

        await using var accessPolicyCmd = connection.CreateCommand();
        accessPolicyCmd.CommandText = """
            SELECT metadata #>> '{accessPolicy,allowAnonymousWrite}'
            FROM honua.services
            WHERE service_name = 'mobile_offline_demo'
            """;
        var allowAnonymousWrite = (string?)await accessPolicyCmd.ExecuteScalarAsync();
        allowAnonymousWrite.Should().Be("true", "fixture writes and replica sync should not require cloud-only credentials");

        await using var storageCmd = connection.CreateCommand();
        storageCmd.CommandText = """
            SELECT COUNT(*)
            FROM honua.layers l
            JOIN pg_namespace n ON n.nspname = l.table_schema
            JOIN pg_class c ON c.relnamespace = n.oid AND c.relname = l.table_name
            WHERE l.layer_id IN (68910, 68920)
              AND l.table_name = 'features'
              AND l.primary_key_column = 'objectid'
              AND l.geometry_column = 'geometry'
              AND l.storage_srid = 4326
            """;
        var storageBindingCount = (long)(await storageCmd.ExecuteScalarAsync())!;
        storageBindingCount.Should().Be(2, "fixture layers should declare provider-ready storage bindings that resolve the physical features table");
    }

    [Fact]
    public async Task DbUpMigrations_RasterSensorMetadata_CreatesCompanionTableAndCascades()
    {
        // Arrange — run the full Server migration set. Migration 060 guards on raster_data, which
        // is provisioned outside the Server set, so on a fresh schema it is a no-op until the raster
        // schema exists. After provisioning raster_data we re-run the embedded 060 script (it is
        // idempotent / IF NOT EXISTS) and assert the companion table + FK cascade.
        var connectionStringBuilder = new Npgsql.NpgsqlConnectionStringBuilder(_connectionString)
        {
            SearchPath = $"{_schemaName},public"
        };

        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionStringBuilder.ToString(), _schemaName)
            .JournalToPostgresqlTable(_schemaName, "schema_versions")
            .WithScriptsEmbeddedInAssembly(Assembly.GetAssembly(typeof(Program))!)
            .WithVariable("HonuaSchema", "\"honua\"")
            .WithTransaction()
            .Build();

        var migrationResult = upgrader.PerformUpgrade();
        migrationResult.Successful.Should().BeTrue($"migrations should complete successfully. Error: {migrationResult.Error}");

        await using var connection = await OpenSchemaConnectionAsync();

        // Provision the raster_data parent (normally created with the raster schema), then apply
        // the embedded 060 migration SQL so the guarded companion table is created.
        await using (var createParent = connection.CreateCommand())
        {
            createParent.CommandText = $"""
                CREATE TABLE IF NOT EXISTS {_schemaName}.raster_data (
                    id BIGSERIAL PRIMARY KEY,
                    layer_id INTEGER NOT NULL,
                    name VARCHAR(255) NOT NULL,
                    raster raster NOT NULL
                );
                """;
            await createParent.ExecuteNonQueryAsync();
        }

        // Substitute the same configured-schema variable used by the production DbUp runner so
        // this round-trip stays parallel-safe and does not collide with the shared honua schema.
        var migrationSql = (await ReadEmbeddedMigrationAsync("060_AddRasterSensorMetadata.sql"))
            .Replace("$HonuaSchema$", _schemaName, StringComparison.Ordinal);
        await using (var apply = connection.CreateCommand())
        {
            apply.CommandText = $"SET search_path TO {_schemaName}, public; {migrationSql}";
            await apply.ExecuteNonQueryAsync();
        }

        // Assert the companion table exists.
        await using (var tableCmd = connection.CreateCommand())
        {
            tableCmd.CommandText = $"""
                SELECT COUNT(*)::int FROM information_schema.tables
                WHERE table_schema = '{_schemaName}' AND table_name = 'raster_sensor_metadata'
                """;
            var exists = (int)(await tableCmd.ExecuteScalarAsync())!;
            exists.Should().Be(1, "the raster_sensor_metadata companion table should be created");
        }

        // Insert a parent raster + a sensor metadata row, then delete the parent and assert the
        // ON DELETE CASCADE removed the companion row (round-trip).
        long rasterId;
        await using (var insertParent = connection.CreateCommand())
        {
            insertParent.CommandText = $"""
                INSERT INTO {_schemaName}.raster_data (layer_id, name, raster)
                SELECT 4242, 'sensor-raster',
                       ST_AddBand(ST_MakeEmptyRaster(1, 1, 0, 1, 1, -1, 0, 0, 4326), '8BUI'::text, 1, NULL)
                RETURNING id;
                """;
            rasterId = (long)(await insertParent.ExecuteScalarAsync())!;
        }

        await using (var insertSensor = connection.CreateCommand())
        {
            insertSensor.CommandText = $"""
                INSERT INTO {_schemaName}.raster_sensor_metadata
                    (raster_data_id, sensor_name, exterior_orientation, rpc, dem_source)
                VALUES (@id, 'WorldView-3', @exterior::jsonb, @rpc::jsonb, '99');
                """;
            insertSensor.Parameters.AddWithValue("id", rasterId);
            insertSensor.Parameters.AddWithValue("exterior", """{"offNadirAngle": 12.5}""");
            insertSensor.Parameters.AddWithValue("rpc", """{"sampOff": 0}""");
            await insertSensor.ExecuteNonQueryAsync();
        }

        await using (var readback = connection.CreateCommand())
        {
            readback.CommandText = $"""
                SELECT sensor_name, exterior_orientation ->> 'offNadirAngle', dem_source
                FROM {_schemaName}.raster_sensor_metadata WHERE raster_data_id = @id
                """;
            readback.Parameters.AddWithValue("id", rasterId);
            await using var reader = await readback.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(0).Should().Be("WorldView-3");
            reader.GetString(1).Should().Be("12.5");
            reader.GetString(2).Should().Be("99");
        }

        await using (var deleteParent = connection.CreateCommand())
        {
            deleteParent.CommandText = $"DELETE FROM {_schemaName}.raster_data WHERE id = @id";
            deleteParent.Parameters.AddWithValue("id", rasterId);
            await deleteParent.ExecuteNonQueryAsync();
        }

        await using (var cascadeCmd = connection.CreateCommand())
        {
            cascadeCmd.CommandText = $"""
                SELECT COUNT(*)::int FROM {_schemaName}.raster_sensor_metadata WHERE raster_data_id = @id
                """;
            cascadeCmd.Parameters.AddWithValue("id", rasterId);
            var remaining = (int)(await cascadeCmd.ExecuteScalarAsync())!;
            remaining.Should().Be(0, "deleting the parent raster should cascade-delete the sensor metadata row");
        }
    }

    private static async Task<string> ReadEmbeddedMigrationAsync(string scriptName)
    {
        var assembly = Assembly.GetAssembly(typeof(Program))!;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(scriptName, StringComparison.Ordinal));
        await using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var streamReader = new StreamReader(stream);
        return await streamReader.ReadToEndAsync();
    }

    [Fact]
    public async Task DbUpMigrations_WithInvalidConnectionString_FailsGracefully()
    {
        // Arrange
        var invalidConnectionString = "Host=invalid;Database=invalid;Username=invalid;Password=invalid";
        var upgrader = DeployChanges.To
            .PostgresqlDatabase(invalidConnectionString)
            .WithScriptsEmbeddedInAssembly(Assembly.GetAssembly(typeof(Program))!)
            .WithVariable("HonuaSchema", "\"honua\"")
            .WithTransaction()
            .Build();

        // Act
        var result = upgrader.PerformUpgrade();

        // Assert
        result.Successful.Should().BeFalse("migration should fail with invalid connection");
        result.Error.Should().NotBeNull("error details should be provided");
    }

    [Fact]
    public async Task CoreSchemaGuard_WhenInitialJournaledTableIsMissing_RejectsStartupPlan()
    {
        var isolatedConnectionString = await _postgres.CreateIsolatedDatabaseAsync(
            nameof(CoreSchemaGuard_WhenInitialJournaledTableIsMissing_RejectsStartupPlan));
        var databaseName = new Npgsql.NpgsqlConnectionStringBuilder(isolatedConnectionString).Database!;
        try
        {
            var connectionString = isolatedConnectionString;
            var guard = new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest);
            var runner = new PostgresDatabaseMigrationRunner(guard, ServerCoreSchemaMigrations.Manifest);

            (await runner.RunMigrationsAsync(
                connectionString,
                Assembly.GetAssembly(typeof(Program))!)).Successful.Should().BeTrue();

            await using (var connection = new Npgsql.NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "DROP TABLE honua.layers CASCADE;";
                await command.ExecuteNonQueryAsync();
            }

            var failedPlan = await runner.PlanMigrationsAsync(
                connectionString,
                Assembly.GetAssembly(typeof(Program))!);
            failedPlan.Successful.Should().BeFalse();
            failedPlan.Error.Should().BeOfType<Honua.Core.Features.Infrastructure.Domain.DatabaseSchemaFloorException>();
            failedPlan.Error!.Message.Should().Contain("table layers");

            var failedRun = await runner.RunMigrationsAsync(
                connectionString,
                Assembly.GetAssembly(typeof(Program))!);
            failedRun.Successful.Should().BeFalse();
            failedRun.Error.Should().BeOfType<Honua.Core.Features.Infrastructure.Domain.DatabaseSchemaFloorException>();
            failedRun.Error!.Message.Should().Contain("Honua.Server.Migrations.001_CreateHonuaSchema.sql");
            failedRun.Error.Message.Should().Contain("table layers");
        }
        finally
        {
            await _postgres.DropDatabaseAsync(databaseName);
        }
    }

    private async Task<Npgsql.NpgsqlConnection> OpenSchemaConnectionAsync()
    {
        var connection = new Npgsql.NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SET search_path TO {_schemaName}, public;";
        await cmd.ExecuteNonQueryAsync();
        return connection;
    }

    private static async Task ExecuteSeedFileAsync(Npgsql.NpgsqlConnection connection, string path)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = await File.ReadAllTextAsync(path);
        await command.ExecuteNonQueryAsync();
    }
}

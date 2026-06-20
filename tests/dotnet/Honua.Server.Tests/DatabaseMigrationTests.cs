// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using DbUp;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests;

/// <summary>
/// Tests for database migration functionality using DbUp
/// </summary>
[Protocol(TestProtocols.TestQuality)]
[Collection("Database.CoreFeatureStore")]
public sealed class DatabaseMigrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();
    private string _connectionString = null!;
    private string _schemaName = null!;

    public async Task InitializeAsync()
    {
        await _postgres.InitializeAsync();
        _schemaName = await _postgres.CreateIsolatedSchemaAsync(nameof(DatabaseMigrationTests));
        _connectionString = _postgres.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        await _postgres.DropSchemaAsync(_schemaName);
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
            .WithTransaction()
            .Build();

        // Act
        DbUp.Engine.DatabaseUpgradeResult result = null!;
        await _postgres.RunUnderSchemaMutationLockAsync(() =>
        {
            result = upgrader.PerformUpgrade();
            return Task.CompletedTask;
        });

        // Assert
        if (!result.Successful)
        {
            Console.WriteLine($"Migration failed. Error: {result.Error}");
        }
        result.Successful.Should().BeTrue($"migrations should complete successfully. Error: {result.Error}");
        result.Scripts.Should().HaveCountGreaterThan(0, "at least one migration script should exist");

        // Verify schema was created
        await using var connection = await _postgres.GetConnectionAsync(_schemaName);

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
            .WithTransaction()
            .Build();

        // Act - Run migrations twice
        DbUp.Engine.DatabaseUpgradeResult firstResult = null!;
        DbUp.Engine.DatabaseUpgradeResult secondResult = null!;
        await _postgres.RunUnderSchemaMutationLockAsync(() =>
        {
            firstResult = upgrader.PerformUpgrade();
            secondResult = upgrader.PerformUpgrade();
            return Task.CompletedTask;
        });

        // Assert
        firstResult.Successful.Should().BeTrue($"first migration should succeed. Error: {firstResult.Error}");
        secondResult.Successful.Should().BeTrue($"second migration should succeed. Error: {secondResult.Error}");

        firstResult.Scripts.Should().HaveCountGreaterThan(0, "first run should apply scripts");
        secondResult.Scripts.Should().BeEmpty("second run should apply no scripts");
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
            .WithTransaction()
            .Build();

        DbUp.Engine.DatabaseUpgradeResult migrationResult = null!;
        await _postgres.RunUnderSchemaMutationLockAsync(() =>
        {
            migrationResult = upgrader.PerformUpgrade();
            return Task.CompletedTask;
        });
        migrationResult.Successful.Should().BeTrue($"migrations should complete successfully. Error: {migrationResult.Error}");

        var baselineSeedPath = RepositoryPaths.Resolve("tests", "seed", "mobile-offline-demo-v1.sql");
        var conflictSeedPath = RepositoryPaths.Resolve("tests", "seed", "mobile-offline-demo-conflict-delta.sql");

        // Act
        // honua-server#1568 (signature 2): both seeds run idempotent ALTER/INSERT DDL against the
        // literal, process-global honua schema (which per-test search_path isolation does not
        // scope), so apply them under the shared seed advisory lock — like SeedRunner and the
        // DbUp upgrade above — to keep parallel global-honua mutation off the deadlock (40P01)
        // path rather than racing ACCESS EXCLUSIVE catalog/table locks.
        await ExecuteSeedFileAsync(baselineSeedPath);
        await ExecuteSeedFileAsync(conflictSeedPath);

        await using var connection = await _postgres.GetConnectionAsync(_schemaName);

        // Assert
        await using var countCmd = connection.CreateCommand();
        countCmd.CommandText = """
            SELECT COUNT(*)
            FROM features
            WHERE layer_id IN (68910, 68920)
            """;
        var featureCount = (long)(await countCmd.ExecuteScalarAsync())!;
        featureCount.Should().Be(5, "mobile offline baseline should seed the deterministic edit and context records");

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
            FROM honua.layers
            WHERE layer_id IN (68910, 68920)
              AND table_schema = current_schema()
              AND table_name = 'features'
              AND primary_key_column = 'objectid'
              AND geometry_column = 'geometry'
              AND storage_srid = 4326
            """;
        var storageBindingCount = (long)(await storageCmd.ExecuteScalarAsync())!;
        storageBindingCount.Should().Be(2, "fixture layers should declare provider-ready storage bindings that resolve features to the active schema");
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
            .WithTransaction()
            .Build();

        DbUp.Engine.DatabaseUpgradeResult migrationResult = null!;
        await _postgres.RunUnderSchemaMutationLockAsync(() =>
        {
            migrationResult = upgrader.PerformUpgrade();
            return Task.CompletedTask;
        });
        migrationResult.Successful.Should().BeTrue($"migrations should complete successfully. Error: {migrationResult.Error}");

        await using var connection = await _postgres.GetConnectionAsync(_schemaName);

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

        // The shipped migration hard-codes the honua.* schema (matching the runtime deployment).
        // Retarget it to the isolated test schema so this round-trip stays parallel-safe and does
        // not collide with the shared honua schema other tests use.
        var migrationSql = (await ReadEmbeddedMigrationAsync("060_AddRasterSensorMetadata.sql"))
            .Replace("honua.", $"{_schemaName}.", StringComparison.Ordinal);
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
            .WithTransaction()
            .Build();

        // Act
        var result = upgrader.PerformUpgrade();

        // Assert
        result.Successful.Should().BeFalse("migration should fail with invalid connection");
        result.Error.Should().NotBeNull("error details should be provided");
    }

    private async Task ExecuteSeedFileAsync(string path)
    {
        var sql = await File.ReadAllTextAsync(path);
        await _postgres.ApplyGlobalSeedSqlAsync(sql, _schemaName);
    }
}

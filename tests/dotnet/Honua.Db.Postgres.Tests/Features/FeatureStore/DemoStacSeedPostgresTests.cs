// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
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
            await ExecuteAsync(dataSource, seed);

            (await ScalarStringAsync(dataSource, "SELECT to_regclass('honua.features')::text"))
                .Should().Be("honua.features");
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
            (await ScalarInt64Async(dataSource, CurrentRevisionSql)).Should().Be(1);
            var firstPublicationIdentity = await ScalarStringAsync(dataSource, PublicationIdentitySql);
            firstPublicationIdentity.Should().NotBeNullOrWhiteSpace();

            await ExecuteAsync(dataSource, seed);

            (await ScalarInt64Async(dataSource, "SELECT count(*) FROM honua.features"))
                .Should().Be(7, "reapplying the seed must replace, not duplicate, fixture rows");
            (await ScalarInt64Async(dataSource, CurrentRevisionSql)).Should().Be(2);
            (await ScalarInt64Async(dataSource, TriggerCountSql))
                .Should().Be(1, "reapplying the seed must not duplicate its migration-owned trigger");
            (await ScalarInt64Async(dataSource, TriggerOidSql))
                .Should().Be(firstTriggerOid, "an idempotent rerun must not drop and recreate the valid trigger");
            (await ScalarStringAsync(dataSource, PublicationIdentitySql))
                .Should().Be(firstPublicationIdentity, "publication ids and bindings must remain stable");

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
            .Should().Be("1:990001,2:990001,3:990001");
        (await ScalarInt64Async(dataSource, NonIncreasingChangeGenerationCountSql))
            .Should().Be(0, "insert, update, and delete generations must be strictly increasing");
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
        SELECT tgfoid::regprocedure::text
          FROM pg_trigger
         WHERE tgrelid = 'honua.features'::regclass
           AND tgname = 'trigger_track_feature_changes'
           AND NOT tgisinternal
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
        INSERT INTO honua.features (objectid, layer_id, geometry, attributes)
        VALUES (990001, 90999, ST_SetSRID(ST_MakePoint(-156.5, 20.8), 4326), '{"stage":"insert"}'::jsonb);
        UPDATE honua.features
           SET attributes = '{"stage":"update"}'::jsonb
         WHERE objectid = 990001 AND layer_id = 90999;
        DELETE FROM honua.features
         WHERE objectid = 990001 AND layer_id = 90999;
        """;

    private const string ChangeTrackingTablesSql =
        """
        CREATE SEQUENCE honua.sync_generation;
        CREATE TABLE honua.layers (
            layer_id INT PRIMARY KEY,
            primary_key_column TEXT
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
}

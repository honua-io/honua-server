// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

            (await ScalarStringAsync(dataSource, "SELECT to_regclass('honua.features')::text"))
                .Should().BeNull("the regression must start from the live failure state");

            var seed = RenderSeed(injectFailure: false);
            await ExecuteAsync(dataSource, seed);

            (await ScalarStringAsync(dataSource, "SELECT to_regclass('honua.features')::text"))
                .Should().Be("honua.features");
            (await ScalarInt64Async(dataSource, "SELECT count(*) FROM honua.features"))
                .Should().Be(7);
            (await ScalarInt64Async(dataSource, RequiredIndexesSql))
                .Should().Be(3);
            (await ScalarInt64Async(dataSource, CurrentRevisionSql)).Should().Be(1);
            var firstPublicationIdentity = await ScalarStringAsync(dataSource, PublicationIdentitySql);
            firstPublicationIdentity.Should().NotBeNullOrWhiteSpace();

            await ExecuteAsync(dataSource, seed);

            (await ScalarInt64Async(dataSource, "SELECT count(*) FROM honua.features"))
                .Should().Be(7, "reapplying the seed must replace, not duplicate, fixture rows");
            (await ScalarInt64Async(dataSource, CurrentRevisionSql)).Should().Be(2);
            (await ScalarStringAsync(dataSource, PublicationIdentitySql))
                .Should().Be(firstPublicationIdentity, "publication ids and bindings must remain stable");

            var stableFeatureState = await ScalarStringAsync(dataSource, FeatureStateSql);
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
            (await ScalarStringAsync(dataSource, PublicationIdentitySql))
                .Should().Be(firstPublicationIdentity);
        }
        finally
        {
            await fixture.DropDatabaseAsync(databaseName);
        }
    }

    private static string RenderSeed(bool injectFailure)
    {
        var seedPath = Path.Combine(AppContext.BaseDirectory, "Seed", "demo-stac-imagery-v1.sql");
        var source = File.ReadAllText(seedPath);
        var begin = source.IndexOf("\nBEGIN;", StringComparison.Ordinal);
        if (begin < 0)
        {
            throw new InvalidOperationException("Demo STAC seed has no transaction boundary.");
        }

        var rendered = source[(begin + 1)..]
            .Replace(":\"schema\"", "\"honua\"", StringComparison.Ordinal)
            .Replace(":'schema'", "'honua'", StringComparison.Ordinal)
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
           AND indexname IN ('idx_features_layer_id', 'idx_features_geometry', 'idx_features_attributes')
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

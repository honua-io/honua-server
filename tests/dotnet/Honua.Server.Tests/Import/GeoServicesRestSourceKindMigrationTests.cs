// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Honua.Server.Tests.Import;

[Collection("Database.CoreEndpoints")]
[Protocol(TestProtocols.Infrastructure)]
public sealed class GeoServicesRestSourceKindMigrationTests
{
    private const string PostgisImage = "postgis/postgis:16-3.4";
    private const string LegacyKind = "arcgis-geoservices-rest";
    private const string CurrentKind = "geoservices-rest";
    private const string Migration = "144_RenameGeoServicesRestSourceKind.sql";

    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public async Task Migration144_OnAnExistingDatabase_RewritesOnlySourceKindFieldsAndIsRepeatable()
    {
        await using var container = new PostgreSqlBuilder().WithImage(PostgisImage).WithDatabase("honua_source_kind").Build();
        await container.StartAsync();
        var connectionString = container.GetConnectionString();
        await ExecuteAsync(connectionString, "CREATE EXTENSION IF NOT EXISTS postgis; CREATE EXTENSION IF NOT EXISTS postgis_raster; "
            + "CREATE EXTENSION IF NOT EXISTS unaccent; CREATE EXTENSION IF NOT EXISTS pgcrypto;");
        await MigrateAsync(connectionString);
        await ExecuteAsync(connectionString, SeedLegacyRows);
        await ReapplyMigration144Async(connectionString);
        var firstManifest = await ScalarAsync(connectionString, ManifestQuery);
        await ReapplyMigration144Async(connectionString);

        (await ScalarAsync(connectionString, "SELECT source_kind FROM honua.migration_runs WHERE run_id = 'run-legacy'"))
            .Should().Be(CurrentKind);
        (await ScalarAsync(connectionString, "SELECT source_kind FROM honua.migration_batch_runs WHERE batch_id = 'batch-legacy'"))
            .Should().Be(CurrentKind);
        (await ScalarAsync(connectionString, "SELECT count(*)::text FROM honua.migration_data_sources WHERE source_id = 'parcels'"))
            .Should().Be("1");
        (await ScalarAsync(connectionString, "SELECT string_agg(source_kind, ',' ORDER BY source_kind) FROM honua.migration_styles WHERE source_id = 'roads'"))
            .Should().Be($"{LegacyKind},{CurrentKind}", "a row already stored under the current kind is not overwritten");
        (await ScalarAsync(connectionString, "SELECT manifest_json ->> 'sourceKind' FROM honua.arcgis_migration_runs WHERE run_id = 'evidence-legacy'"))
            .Should().Be(CurrentKind);
        (await ScalarAsync(connectionString, "SELECT manifest_json #>> '{targetResources,0,sourceKind}' FROM honua.arcgis_migration_runs WHERE run_id = 'evidence-legacy'"))
            .Should().Be(LegacyKind, "a target resource kind is not the source family");
        (await ScalarAsync(connectionString, "SELECT manifest_json #>> '{targetResources,0,targetResourceName}' FROM honua.arcgis_migration_runs WHERE run_id = 'evidence-legacy'"))
            .Should().Be(LegacyKind, "names that equal the legacy value are data, not discriminators");
        (await ScalarAsync(connectionString, ManifestQuery)).Should().Be(firstManifest, "re-applying the migration changes nothing");
    }

    private const string ManifestQuery = "SELECT manifest_json::text FROM honua.arcgis_migration_runs WHERE run_id = 'evidence-legacy'";

    private static async Task ReapplyMigration144Async(string connectionString)
    {
        await ExecuteAsync(connectionString, $"DELETE FROM public.schema_versions WHERE scriptname LIKE '%{Migration}';");
        await MigrateAsync(connectionString);
    }

    private const string SeedLegacyRows = $$"""
        INSERT INTO honua.migration_runs (run_id, source_kind) VALUES ('run-legacy', '{{LegacyKind}}');
        INSERT INTO honua.migration_batch_runs (batch_id, source_kind) VALUES ('batch-legacy', '{{LegacyKind}}');
        INSERT INTO honua.migration_data_sources (source_kind, source_id, data_source_type)
            VALUES ('{{LegacyKind}}', 'parcels', 'postgis');
        INSERT INTO honua.migration_styles (source_kind, source_id, style_name, target_style_id)
            VALUES ('{{LegacyKind}}', 'roads', 'roads', 'roads-legacy'), ('{{CurrentKind}}', 'roads', 'roads', 'roads-current');
        INSERT INTO honua.arcgis_migration_runs (run_id, source_url, manifest_json)
            VALUES ('evidence-legacy', 'https://gis.example.test/rest/services/Parcels/FeatureServer',
                    '{"sourceKind": "{{LegacyKind}}", "targetResources": [{"sourceKind": "{{LegacyKind}}", "targetResourceName": "{{LegacyKind}}"}]}');
        """;

    private static async Task MigrateAsync(string connectionString)
    {
        var runner = new PostgresDatabaseMigrationRunner(
            new PostgresCoreSchemaGuard(ServerCoreSchemaMigrations.Manifest), ServerCoreSchemaMigrations.Manifest);
        var result = await runner.RunMigrationsAsync(connectionString, typeof(Program).Assembly);
        result.Successful.Should().BeTrue(result.ErrorMessage);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 180 };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (await command.ExecuteScalarAsync())?.ToString();
    }
}

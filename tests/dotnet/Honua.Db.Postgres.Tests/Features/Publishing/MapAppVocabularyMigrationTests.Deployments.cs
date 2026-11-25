// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Publishing;

[Collection("Database")]
public sealed class MapAppDeploymentVocabularyMigrationTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("configured")]
    [InlineData("honua")]
    [Trait("Category", "Integration")]
    public async Task Migration_Reapplied_PreservesDeploymentIdentityRevisionAndOpaqueTarget(string physicalSchema)
    {
        await WithDatabaseAsync(async dataSource =>
        {
            await ExecuteAsync(dataSource, "CREATE SCHEMA configured;");
            if (physicalSchema != "configured")
            {
                await ExecuteAsync(dataSource, "CREATE SCHEMA honua;");
            }
            await ExecuteAsync(dataSource, SeedDeployments.Replace("{schema}", physicalSchema, StringComparison.Ordinal));
            var identity = await ScalarAsync(dataSource, DeploymentFingerprint.Replace("{schema}", physicalSchema, StringComparison.Ordinal));
            await ApplyMigrationAsync(dataSource);
            await ApplyMigrationAsync(dataSource);
            (await ScalarAsync(dataSource, DeploymentFingerprint.Replace("{schema}", physicalSchema, StringComparison.Ordinal))).Should().Be(identity);
            (await ScalarAsync(dataSource, """
                SELECT source_kind || ':' || (document #>> '{source,kind}') || ':' ||
                    (document #>> '{source,appId}') || ':' || (document #>> '{source,mapId}')
                FROM "{schema}".promotion_deployments
                """.Replace("{schema}", physicalSchema, StringComparison.Ordinal))).Should().Be("App:app:app-stable:map-stable");
            (await ScalarAsync(dataSource, """
                SELECT (document #>> '{target,configuration,mapPackageId}') || ':' ||
                    (document #>> '{source,sourceId}') FROM "{schema}".promotion_deployments
                """.Replace("{schema}", physicalSchema, StringComparison.Ordinal))).Should().Be("opaque-user-value:app-stable");
            (await ScalarAsync(dataSource, """
                SELECT count(*)::text FROM "{schema}".promotion_deployments
                WHERE (document -> 'source') ? 'mapPackageId' OR (document -> 'source') ? 'appPackageId'
                """.Replace("{schema}", physicalSchema, StringComparison.Ordinal))).Should().Be("0");
        });
    }

    [Theory]
    [InlineData("ambiguous")]
    [InlineData("partial")]
    [InlineData("non-table")]
    [InlineData("missing")]
    [Trait("Category", "Integration")]
    public async Task Migration_InvalidPromotionFamily_RejectsBeforeChangingDeployment(string layout)
    {
        await WithDatabaseAsync(async dataSource =>
        {
            await ExecuteAsync(dataSource, "CREATE SCHEMA configured; CREATE SCHEMA honua;");
            await ExecuteAsync(dataSource, SeedDeployments);
            var original = await ScalarAsync(dataSource, "SELECT document::text FROM configured.promotion_deployments");
            var extraLayout = layout switch
            {
                "ambiguous" => "CREATE TABLE honua.promotion_deployments (id int); CREATE TABLE honua.promotion_published_services (id int);",
                "partial" => "DROP TABLE configured.promotion_published_services;",
                "non-table" => "DROP TABLE configured.promotion_published_services; CREATE VIEW configured.promotion_published_services AS SELECT 1 AS id;",
                _ => "ALTER TABLE configured.promotion_deployments RENAME TO preserved_deployments; DROP TABLE configured.promotion_published_services;"
            };
            await ExecuteAsync(dataSource, extraLayout);
            var apply = () => ApplyMigrationAsync(dataSource);
            await apply.Should().ThrowAsync<PostgresException>().Where(exception => exception.SqlState == "P0001");
            var table = layout == "missing" ? "preserved_deployments" : "promotion_deployments";
            (await ScalarAsync(dataSource, $"SELECT document::text FROM configured.{table}")).Should().Be(original);
        });
    }

    [IntegrationTest]
    public async Task Migration_ConflictingCanonicalIdentity_RejectsBeforeChangingDeployment()
    {
        await WithDatabaseAsync(async dataSource =>
        {
            await ExecuteAsync(dataSource, "CREATE SCHEMA configured;");
            await ExecuteAsync(dataSource, SeedDeployments);
            await ExecuteAsync(dataSource, """
                UPDATE configured.promotion_deployments
                SET document = jsonb_set(document, '{source,mapId}', '"another-map"')
                """);
            var original = await ScalarAsync(dataSource, "SELECT document::text FROM configured.promotion_deployments");
            var apply = () => ApplyMigrationAsync(dataSource);
            await apply.Should().ThrowAsync<PostgresException>().Where(exception => exception.SqlState == "P0001");
            (await ScalarAsync(dataSource, "SELECT document::text FROM configured.promotion_deployments")).Should().Be(original);
        });
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"source\":null}")]
    [InlineData("{\"source\":[]}")]
    [Trait("Category", "Integration")]
    public async Task Migration_InvalidSourceDocument_RejectsWithoutChangingDeployment(string document)
    {
        await WithDatabaseAsync(async dataSource =>
        {
            await ExecuteAsync(dataSource, "CREATE SCHEMA configured;");
            await ExecuteAsync(dataSource, SeedDeployments);
            await using (var connection = await dataSource.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand(
                "UPDATE configured.promotion_deployments SET document = @document::jsonb", connection))
            {
                command.Parameters.AddWithValue("document", document);
                await command.ExecuteNonQueryAsync();
            }
            var original = await ScalarAsync(dataSource, "SELECT source_kind || ':' || document::text FROM configured.promotion_deployments");
            var apply = () => ApplyMigrationAsync(dataSource);
            await apply.Should().ThrowAsync<PostgresException>().Where(exception => exception.SqlState == "P0001");
            (await ScalarAsync(dataSource, "SELECT source_kind || ':' || document::text FROM configured.promotion_deployments")).Should().Be(original);
        });
    }

    private async Task WithDatabaseAsync(Func<NpgsqlDataSource, Task> action)
    {
        var connectionString = await fixture.CreateIsolatedDatabaseAsync(nameof(MapAppDeploymentVocabularyMigrationTests));
        var databaseName = new NpgsqlConnectionStringBuilder(connectionString).Database!;
        try
        {
            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            await action(dataSource);
        }
        finally
        {
            await fixture.DropDatabaseAsync(databaseName);
        }
    }

    private static async Task ApplyMigrationAsync(NpgsqlDataSource dataSource)
    {
        var path = Directory.EnumerateFiles(RepositoryPaths.Resolve("src", "Honua.Server", "Migrations"),
            "*_RenameMapAppDeploymentSources.sql").Single();
        await ExecuteAsync(dataSource, (await File.ReadAllTextAsync(path))
            .Replace("$HonuaSchema$", "configured", StringComparison.Ordinal));
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql.Replace("{schema}", "configured", StringComparison.Ordinal), connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ScalarAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql.Replace("{schema}", "configured", StringComparison.Ordinal), connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private const string DeploymentFingerprint = """
        SELECT deployment_id || ':' || source_id || ':' || version::text || ':' || status || ':' ||
            created_at::text || ':' || updated_at::text || ':' || (document ->> 'version')
        FROM "{schema}".promotion_deployments
        """;

    private const string SeedDeployments = """
        CREATE TABLE "{schema}".promotion_deployments (
            deployment_id text PRIMARY KEY, source_kind text, source_id text, target_id text,
            status text, version bigint, document jsonb, created_at timestamptz, updated_at timestamptz);
        CREATE TABLE "{schema}".promotion_published_services (service_id text PRIMARY KEY);
        INSERT INTO "{schema}".promotion_deployments VALUES
            ('deployment-stable', 'AppPackage', 'app-stable', 'target-stable', 'Active', 7,
             '{"deploymentId":"deployment-stable","version":7,"source":{"kind":"app_package","sourceId":"app-stable","appPackageId":"app-stable","mapPackageId":"map-stable"},"target":{"configuration":{"mapPackageId":"opaque-user-value"}}}',
             now(), now());
        """;
}

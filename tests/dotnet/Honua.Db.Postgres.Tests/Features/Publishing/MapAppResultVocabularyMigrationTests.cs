// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Publishing;

[Collection("Database")]
public sealed class MapAppResultVocabularyMigrationTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task Migration_Reapplied_PreservesResultIdentityExpiryAndOpaqueArtifacts()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(MapAppResultVocabularyMigrationTests));
        try
        {
            await ExecuteAsync(schema, """
                CREATE TABLE "{schema}".geoprocessing_result_packages (job_id text PRIMARY KEY, package json, expires_at timestamptz);
                INSERT INTO "{schema}".geoprocessing_result_packages VALUES
                    ('job-stable', '{"resultPackageId":"result-stable","mapPackageId":"map-stable","appPackageId":"app-stable","artifacts":[{"metadata":{"mapPackageId":"opaque-user-value"}}]}', now() + interval '1 hour');
                """);
            var identity = await ScalarAsync(schema, IdentityFingerprint);
            await ApplyMigrationAsync(schema);
            await ApplyMigrationAsync(schema);
            (await ScalarAsync(schema, IdentityFingerprint)).Should().Be(identity);
            (await ScalarAsync(schema, """
                SELECT (package::jsonb ->> 'mapId') || ':' || (package::jsonb ->> 'appId') || ':' ||
                    (package::jsonb #>> '{artifacts,0,metadata,mapPackageId}') FROM "{schema}".geoprocessing_result_packages
                """)).Should().Be("map-stable:app-stable:opaque-user-value");
            (await ScalarAsync(schema, """
                SELECT count(*)::text FROM "{schema}".geoprocessing_result_packages
                WHERE package::jsonb ? 'mapPackageId' OR package::jsonb ? 'appPackageId'
                """)).Should().Be("0");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task Migration_ConflictingCanonicalIdentity_RejectsWithoutChangingResult()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(MapAppResultVocabularyMigrationTests));
        try
        {
            await ExecuteAsync(schema, """
                CREATE TABLE "{schema}".geoprocessing_result_packages (job_id text PRIMARY KEY, package json, expires_at timestamptz);
                INSERT INTO "{schema}".geoprocessing_result_packages VALUES
                    ('job-stable', '{"mapPackageId":"map-stable","mapId":"another-map"}', now());
                """);
            var original = await ScalarAsync(schema, "SELECT package::text FROM \"{schema}\".geoprocessing_result_packages");
            var apply = () => ApplyMigrationAsync(schema);
            await apply.Should().ThrowAsync<PostgresException>().Where(exception => exception.SqlState == "P0001");
            (await ScalarAsync(schema, "SELECT package::text FROM \"{schema}\".geoprocessing_result_packages")).Should().Be(original);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task ApplyMigrationAsync(string schema)
    {
        var path = Directory.EnumerateFiles(RepositoryPaths.Resolve("src", "Honua.Server", "Migrations"),
            "*_RenameMapAppResultReferences.sql").Single();
        await ExecuteAsync(schema, (await File.ReadAllTextAsync(path)).Replace("$HonuaSchema$", "\"{schema}\"", StringComparison.Ordinal));
    }

    private async Task ExecuteAsync(string schema, string sql)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql.Replace("{schema}", schema, StringComparison.Ordinal), connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> ScalarAsync(string schema, string sql)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql.Replace("{schema}", schema, StringComparison.Ordinal), connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private const string IdentityFingerprint = """
        SELECT job_id || ':' || (package::jsonb ->> 'resultPackageId') || ':' || expires_at::text
        FROM "{schema}".geoprocessing_result_packages
        """;
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Publishing;

[Collection("Database")]
public sealed class MapAppReportVocabularyMigrationTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task Migration_Reapplied_PreservesReportIdentityExpiryAndOpaqueSections()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(MapAppReportVocabularyMigrationTests));
        try
        {
            await ExecuteAsync(schema, SeedReport);
            var identity = await ScalarAsync(schema, IdentityFingerprint);
            await ApplyMigrationAsync(schema);
            await ApplyMigrationAsync(schema);
            (await ScalarAsync(schema, IdentityFingerprint)).Should().Be(identity);
            (await ScalarAsync(schema, """
                SELECT (body::jsonb #>> '{sections,0,mapUri}') || ':' ||
                    (body::jsonb #>> '{sections,1,mapPackageUri}') FROM "{schema}".analysis_reports
                """)).Should().Be("honua://maps/map-stable:opaque-user-value");
            (await ScalarAsync(schema, """
                SELECT count(*)::text FROM "{schema}".analysis_reports
                WHERE body::jsonb #> '{sections,0}' ? 'mapPackageUri'
                """)).Should().Be("0");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task Migration_ConflictingCanonicalReference_RejectsWithoutChangingReport()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("ReportMapReferenceConflict");
        try
        {
            await ExecuteAsync(schema, SeedReport);
            await ExecuteAsync(schema, """
                UPDATE "{schema}".analysis_reports SET body = jsonb_set(body::jsonb,
                    '{sections,0,mapUri}', '"honua://maps/another-map"')::json
                """);
            const string read = "SELECT body::text FROM \"{schema}\".analysis_reports";
            var original = await ScalarAsync(schema, read);
            var apply = () => ApplyMigrationAsync(schema);
            await apply.Should().ThrowAsync<PostgresException>().Where(exception => exception.SqlState == "P0001");
            (await ScalarAsync(schema, read)).Should().Be(original);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task ApplyMigrationAsync(string schema)
    {
        var path = Directory.EnumerateFiles(RepositoryPaths.Resolve("src", "Honua.Server", "Migrations"),
            "*_RenameMapAppReportReferences.sql").Single();
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
        SELECT job_id || ':' || contract_version || ':' || (body::jsonb ->> 'reportId') || ':' || stored_at::text || ':' || expires_at::text
        FROM "{schema}".analysis_reports
        """;

    private const string SeedReport = """
        CREATE TABLE "{schema}".analysis_reports (job_id text, contract_version text, body json, stored_at timestamptz, expires_at timestamptz);
        INSERT INTO "{schema}".analysis_reports VALUES ('job-stable', '1.0',
            '{"reportId":"report-stable","sections":[{"kind":"map-embed","mapPackageUri":"honua://map-packages/map-stable"},{"kind":"table","mapPackageUri":"opaque-user-value"}]}', now(), now() + interval '1 hour');
        """;
}

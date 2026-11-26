// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Publishing;

[Collection("Database")]
public sealed class MapAppVocabularyMigrationTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task Migration_Reapplied_PreservesDraftIdentityOwnershipExpiryAndOpaquePayload()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(MapAppVocabularyMigrationTests));
        try
        {
            await ExecuteAsync(schema, SeedDrafts);
            var identity = await ScalarAsync(schema, IdentityFingerprint);
            await ApplyMigrationAsync(schema);
            await ApplyMigrationAsync(schema);
            (await ScalarAsync(schema, IdentityFingerprint)).Should().Be(identity);
            (await ScalarAsync(schema, """
                SELECT string_agg(kind || ':' || coalesce(record::jsonb ->> 'mapId', '') || ':' || coalesce(record::jsonb ->> 'appId', ''), ',' ORDER BY kind)
                FROM "{schema}".package_drafts
                """)).Should().Be("app:map-stable:app-stable,map:map-stable:");
            (await ScalarAsync(schema, """
                SELECT count(*)::text FROM "{schema}".package_drafts
                WHERE record::jsonb ? 'mapPackageId' OR record::jsonb ? 'appPackageId'
                """)).Should().Be("0");
            (await ScalarAsync(schema, """
                SELECT record::jsonb #>> '{runtimeConfig,mapPackageId}'
                FROM "{schema}".package_drafts WHERE kind = 'app'
                """)).Should().Be("opaque-user-value");
            (await ScalarAsync(schema, """
                SELECT string_agg(record::jsonb ->> 'format', ',' ORDER BY kind) FROM "{schema}".package_drafts
                """)).Should().Be("honua_app.v1,honua_map.v1");
            (await ScalarAsync(schema, """
                SELECT record::jsonb #>> '{mapSpec,mapPackageId}' FROM "{schema}".package_drafts WHERE kind = 'map'
                """)).Should().Be("opaque-user-value");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task Migration_ConflictingCanonicalIdentity_RejectsWithoutChangingDrafts()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(MapAppVocabularyMigrationTests));
        try
        {
            await ExecuteAsync(schema, SeedDrafts);
            await ExecuteAsync(schema, """
                UPDATE "{schema}".package_drafts SET record =
                    (record::jsonb || '{"mapId":"another-map"}'::jsonb)::json WHERE kind = 'map'
                """);
            var original = await ScalarAsync(schema, """
                SELECT string_agg(record::text, ',' ORDER BY kind) FROM "{schema}".package_drafts
                """);
            await FluentActions.Invoking(() => ApplyMigrationAsync(schema))
                .Should().ThrowAsync<PostgresException>()
                .Where(error => error.SqlState == "P0001");
            (await ScalarAsync(schema, """
                SELECT string_agg(record::text, ',' ORDER BY kind) FROM "{schema}".package_drafts
                """)).Should().Be(original);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task ApplyMigrationAsync(string schema)
    {
        var directory = RepositoryPaths.Resolve("src", "Honua.Server", "Migrations");
        var path = Directory.EnumerateFiles(directory, "*_RenameMapAppDraftIdentities.sql").Single();
        await ExecuteAsync(schema, (await File.ReadAllTextAsync(path))
            .Replace("$HonuaSchema$", "\"{schema}\"", StringComparison.Ordinal));
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
        SELECT string_agg(kind || ':' || draft_id || ':' || owner_id || ':' || written_at::text || ':' || expires_at::text, ',' ORDER BY kind)
        FROM "{schema}".package_drafts
        """;

    private const string SeedDrafts = """
        CREATE TABLE "{schema}".package_drafts (
            kind text, draft_id text, owner_id text, written_at timestamptz, record json, expires_at timestamptz,
            PRIMARY KEY (kind, draft_id));
        INSERT INTO "{schema}".package_drafts VALUES
            ('map', 'map-stable', 'owner', now(), '{"mapPackageId":"map-stable","format":"honua_map_package.v1","mapSpec":{"mapPackageId":"opaque-user-value"}}', now() + interval '1 hour'),
            ('app', 'app-stable', 'owner', now(), '{"appPackageId":"app-stable","mapPackageId":"map-stable","format":"honua_app_package.v1","runtimeConfig":{"mapPackageId":"opaque-user-value"}}', now() + interval '2 hours');
        """;
}

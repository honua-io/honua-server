// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Publishing;

[Collection("Database")]
public sealed class MapAppStudioDraftVocabularyMigrationTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task Migration_Reapplied_ConvertsDraftOnceAndPreservesImmutableVersionBytes()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(MapAppStudioDraftVocabularyMigrationTests));
        try
        {
            await ExecuteAsync(schema, SeedContent);
            var history = await ScalarAsync(schema, HistoryFingerprint);
            await ApplyMigrationAsync(schema);
            await ApplyMigrationAsync(schema);
            (await ScalarAsync(schema, HistoryFingerprint)).Should().Be(history);
            (await ScalarAsync(schema, """
                SELECT string_agg(family || ':' || generation::text || ':' ||
                    (envelope #>> '{body,format}') || ':' || (envelope #>> '{body,mapId}') || ':' ||
                    coalesce(envelope #>> '{body,appId}', ''), ',' ORDER BY family)
                FROM "{schema}".studio_package_drafts
                """)).Should().Be("app:8:honua_app.v1:map-stable:app-stable,map:8:honua_map.v1:map-stable:");
            (await ScalarAsync(schema, """
                SELECT envelope #>> '{body,mapSpec,mapPackageId}' FROM "{schema}".studio_package_drafts WHERE family = 'map'
                """)).Should().Be("opaque-user-value");
            (await ScalarAsync(schema, """
                SELECT string_agg(envelope ->> 'format', ',' ORDER BY family) FROM "{schema}".studio_package_drafts
                """)).Should().Be("honua_app.v1,honua_map.v1");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task Migration_ConflictingCanonicalIdentity_RejectsWithoutChangingDraft()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("StudioDraftIdentityConflict");
        try
        {
            await ExecuteAsync(schema, SeedContent);
            await ExecuteAsync(schema, """
                UPDATE "{schema}".studio_package_drafts
                SET envelope = jsonb_set(envelope, '{body,mapId}', '"another-map"') WHERE family = 'map'
                """);
            const string draft = "SELECT envelope::text FROM \"{schema}\".studio_package_drafts WHERE family = 'map'";
            var before = await ScalarAsync(schema, draft);
            var apply = () => ApplyMigrationAsync(schema);
            (await apply.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0001");
            (await ScalarAsync(schema, draft)).Should().Be(before);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [Theory]
    [InlineData("{}", "7:{}")]
    [InlineData("{\"body\": null}", "7:{\"body\": null}")]
    [InlineData("{\"body\": []}", "7:{\"body\": []}")]
    [InlineData("{\"body\": null, \"format\": \"honua_map_package.v1\"}", "8:{\"body\": null, \"format\": \"honua_map.v1\"}")]
    [Trait("Category", "Integration")]
    public async Task Migration_IncompleteDraftBody_KeepsDraftAndRenamesOnlyEnvelopeFormat(string envelope, string expected)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("StudioDraftIncompleteBody");
        try
        {
            await ExecuteAsync(schema, SeedContent);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"UPDATE \"{schema}\".studio_package_drafts SET envelope = @envelope::jsonb WHERE family = 'map'", connection);
            command.Parameters.AddWithValue("envelope", envelope);
            await command.ExecuteNonQueryAsync();
            const string draft = "SELECT generation::text || ':' || envelope::text FROM \"{schema}\".studio_package_drafts WHERE family = 'map'";
            await ApplyMigrationAsync(schema);
            await ApplyMigrationAsync(schema);
            (await ScalarAsync(schema, draft)).Should().Be(expected);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task ApplyMigrationAsync(string schema)
    {
        var path = Directory.EnumerateFiles(RepositoryPaths.Resolve("src", "Honua.Server", "Migrations"),
            "*_RenameMapAppStudioDraftBodies.sql").Single();
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

    private const string HistoryFingerprint = """
        SELECT content_hash || ':' || encode(content_hash_input, 'hex') || ':' || envelope::text
        FROM "{schema}".studio_content_versions
        """;

    private const string SeedContent = """
        CREATE TABLE "{schema}".studio_package_drafts (draft_id text PRIMARY KEY, family text, generation bigint, envelope jsonb);
        CREATE TABLE "{schema}".studio_content_versions (content_hash text, content_hash_input bytea, envelope jsonb);
        INSERT INTO "{schema}".studio_package_drafts VALUES
            ('map-draft', 'map', 7, '{"family":"map","format":"honua_map_package.v1","body":{"mapPackageId":"map-stable","format":"honua_map_package.v1","mapSpec":{"mapPackageId":"opaque-user-value"}}}'),
            ('app-draft', 'app', 7, '{"family":"app","format":"honua_app_package.v1","body":{"appPackageId":"app-stable","mapPackageId":"map-stable","format":"honua_app_package.v1"}}');
        INSERT INTO "{schema}".studio_content_versions VALUES
            ('immutable-digest', decode('010203', 'hex'), '{"family":"map","body":{"mapPackageId":"map-stable","format":"honua_map_package.v1"}}');
        """;
}

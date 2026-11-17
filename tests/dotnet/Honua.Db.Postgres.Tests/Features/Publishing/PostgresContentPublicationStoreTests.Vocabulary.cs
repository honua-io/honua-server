// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Publishing.Content.Domain;
using Honua.Db.Postgres.Features.Publishing;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Publishing;

public sealed partial class PostgresContentPublicationStoreTests
{
    [IntegrationTest]
    public async Task VocabularyMigration_PreservesHistoryAndReadsCanonicalReferences()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("PublicationVocabulary");
        try
        {
            await ApplyVocabularySqlAsync(schema, "036_CreateContentPublications.sql");
            await SeedLegacyPublicationAsync(schema);
            var before = await ReadPublicationHistoryAsync(schema);
            await ApplyVocabularySqlAsync(schema, "*_AdoptContentPublicationVocabulary.sql");
            await ApplyVocabularySqlAsync(schema, "*_AdoptContentPublicationVocabulary.sql");
            (await ReadPublicationHistoryAsync(schema)).Should().Be(before);
            var store = new PostgresContentPublicationStore(new TestConnectionProvider(fixture.DataSource, schema), schema);
            var version = await store.GetVersionByRevisionAsync(PublicationVocabularyId, 1);
            version!.Kind.ToString().Should().Be("App");
            version.Dependencies.Select(value => value.Kind.ToString()).Should().Equal("Dataset", "Layer", "Map", "App");
            (await store.GetRouteByPublicationIdAsync(PublicationVocabularyId))!.Kind.ToString().Should().Be("App");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task VocabularyMigration_NewWritesUseAppAndCanonicalDependencies()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("PublicationVocabularyWrites");
        try
        {
            await ApplyVocabularySqlAsync(schema, "036_CreateContentPublications.sql");
            await ApplyVocabularySqlAsync(schema, "*_AdoptContentPublicationVocabulary.sql");
            var store = new PostgresContentPublicationStore(new TestConnectionProvider(fixture.DataSource, schema), schema);
            var (version, route, audit) = BuildPublish(PublicationVocabularyId, "canonical-app", "hash-stable");
            var kind = Enum.Parse<ContentPublicationKind>("App");
            version = version with { Kind = kind, Dependencies = [new ContentPublicationDependencyRef
            {
                Kind = Enum.Parse<ContentPublicationDependencyKind>("Map"), RefId = "map-stable"
            }] };
            await store.AppendVersionAndSetRouteAsync(version, route with { Kind = kind }, audit, null);
            await using var connection = await OpenSchemaConnectionAsync(schema);
            await using var command = new NpgsqlCommand("SELECT kind || ':' || (dependencies->0->>'kind') FROM content_publication_versions", connection);
            (await command.ExecuteScalarAsync()).Should().Be("app:map");
            (await store.GetVersionByRevisionAsync(PublicationVocabularyId, 1))!.Kind.Should().Be(kind);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task ApplyVocabularySqlAsync(string schema, string pattern)
    {
        var path = Directory.EnumerateFiles(RepositoryPaths.Resolve("src", "Honua.Server", "Migrations"), pattern).Single();
        await using var connection = await OpenSchemaConnectionAsync(schema);
        await using var command = new NpgsqlCommand((await File.ReadAllTextAsync(path))
            .Replace("$HonuaSchema$", "\"" + schema + "\"", StringComparison.Ordinal), connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task SeedLegacyPublicationAsync(string schema)
    {
        await using var connection = await OpenSchemaConnectionAsync(schema);
        await using var command = new NpgsqlCommand("""
            INSERT INTO content_publication_versions
                (version_id, publication_id, revision, kind, route_slug, route_path, source_package_id,
                 content_hash, policy, dependencies, created_by)
            VALUES ('22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111', 1,
                'generated-app', 'legacy-app', '/published/legacy-app', 'app-stable', 'hash-stable', '{}',
                '[{"kind":"resource","refId":"dataset-stable"},{"kind":"publication","refId":"layer-stable"},
                  {"kind":"map-package","refId":"map-stable"},{"kind":"app-package","refId":"app-stable"}]', 'owner-stable');
            INSERT INTO content_publication_routes
                (publication_id, route_slug, route_path, kind, active_version_id, active_revision,
                 policy, etag, updated_by)
            VALUES ('11111111-1111-1111-1111-111111111111', 'legacy-app', '/published/legacy-app', 'generated-app',
                '22222222-2222-2222-2222-222222222222', 1, '{}', 'etag-stable', 'owner-stable');
            INSERT INTO content_publication_events
                (event_id, publication_id, operation, route_slug, actor, detail)
            VALUES ('33333333-3333-3333-3333-333333333333', '11111111-1111-1111-1111-111111111111', 'publish',
                'legacy-app', 'owner-stable', 'original-history');
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> ReadPublicationHistoryAsync(string schema)
    {
        await using var connection = await OpenSchemaConnectionAsync(schema);
        await using var command = new NpgsqlCommand("""
            SELECT (SELECT jsonb_agg(to_jsonb(v))::text FROM content_publication_versions v) || ':' ||
                (SELECT jsonb_agg(to_jsonb(r))::text FROM content_publication_routes r) || ':' ||
                (SELECT jsonb_agg(to_jsonb(e))::text FROM content_publication_events e)
            """, connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private const string PublicationVocabularyId = "11111111-1111-1111-1111-111111111111";
}

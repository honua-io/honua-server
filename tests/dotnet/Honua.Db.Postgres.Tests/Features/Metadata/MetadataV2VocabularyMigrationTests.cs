// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Metadata;

[Collection("Database")]
public sealed class MetadataV2VocabularyMigrationTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task Migration_ExistingSnapshotsAndSidecars_PreservesDocumentsEtagsAndObjectKinds()
    {
        await WithSchemaAsync(async schema =>
        {
            await ExecuteAsync(schema, Seed);
            var before = await ScalarAsync<string>(schema, SnapshotFingerprint);
            await ApplyMigrationAsync(schema);
            await ApplyMigrationAsync(schema);

            (await ScalarAsync<string>(schema, SnapshotFingerprint)).Should().Be(before);
            (await ScalarAsync<string>(schema, """
                SELECT string_agg(object_id || ':' || object_type, ',' ORDER BY object_id)
                FROM "{schema}".metadata_v2_objects_idx
                """)).Should().Be("dataset:FeatureDataset,map:Map");
            (await ScalarAsync<string>(schema, """
                SELECT layer_id || ':' || object_id || ':' || layer_type
                FROM "{schema}".metadata_v2_layers_idx
                """)).Should().Be("layer:dataset:OgcCollection");
            (await ScalarAsync<string>(schema, """
                SELECT storage_binding_id || ':' || object_id || ':' || locator
                FROM "{schema}".metadata_v2_object_storage_bindings_idx
                """)).Should().Be("binding:dataset:public.parcels");
        });
    }

    [IntegrationTest]
    public async Task Migration_FreshSchemaAndMixedVersionWrites_ShareOnePhysicalAuthority()
    {
        await WithSchemaAsync(async schema =>
        {
            await ApplyMigrationAsync(schema);
            await ExecuteAsync(schema, Seed);
            await ExecuteAsync(schema, """
                UPDATE "{schema}".metadata_v2_objects_idx SET name = 'canonical edit' WHERE object_id = 'map';
                UPDATE "{schema}".metadata_v2_layers_idx SET path = '/canonical' WHERE layer_id = 'layer';
                UPDATE "{schema}".metadata_v2_object_storage_bindings_idx SET locator = 'public.changed'
                  WHERE storage_binding_id = 'binding';
                """);
            (await ScalarAsync<string>(schema, """
                SELECT name FROM "{schema}".metadata_v2_resources_idx WHERE resource_id = 'map'
                """)).Should().Be("canonical edit");
            (await ScalarAsync<string>(schema, """
                SELECT path FROM "{schema}".metadata_v2_publications_idx WHERE publication_id = 'layer'
                """)).Should().Be("/canonical");
            (await ScalarAsync<string>(schema, """
                SELECT locator FROM "{schema}".metadata_v2_storage_bindings_idx WHERE storage_binding_id = 'binding'
                """)).Should().Be("public.changed");
            await ExecuteAsync(schema, """
                INSERT INTO "{schema}".metadata_v2_objects_idx
                    (environment, revision, object_id, name, object_type)
                    VALUES ('Test', 2, 'style', 'style', 'Style');
                UPDATE "{schema}".metadata_v2_resources_idx SET name = 'legacy edit' WHERE resource_id = 'style';
                DELETE FROM "{schema}".metadata_v2_layers_idx WHERE layer_id = 'layer';
                DELETE FROM "{schema}".metadata_v2_object_storage_bindings_idx WHERE storage_binding_id = 'binding';
                """);
            (await ScalarAsync<string>(schema, """
                SELECT name FROM "{schema}".metadata_v2_objects_idx WHERE object_id = 'style'
                """)).Should().Be("legacy edit");
            (await ScalarAsync<long>(schema, "SELECT count(*) FROM \"{schema}\".metadata_v2_publications_idx")).Should().Be(0);
            (await ScalarAsync<long>(schema, "SELECT count(*) FROM \"{schema}\".metadata_v2_storage_bindings_idx")).Should().Be(0);
        });
    }

    private const string SnapshotFingerprint = """
        SELECT string_agg(revision::text || ':' || etag || ':' || document::text, '|' ORDER BY revision)
        FROM "{schema}".metadata_v2_snapshots
        """;

    private const string Seed = """
        INSERT INTO "{schema}".metadata_v2_snapshots
            (environment, revision, schema_version, api_version, document, etag, generated_at)
        VALUES ('Test', 1, '2', '2', '{"resources":[{"type":"map"}],"future":true}', 'retained-etag', NOW()),
               ('Test', 2, '2', '2', '{"resources":[{"type":"feature-dataset"}]}', 'current-etag', NOW());
        INSERT INTO "{schema}".metadata_v2_current (environment, revision, etag) VALUES ('Test', 2, 'current-etag');
        INSERT INTO "{schema}".metadata_v2_resources_idx (environment, revision, resource_id, name, type)
        VALUES ('Test', 1, 'map', 'map', 'Map'), ('Test', 2, 'dataset', 'dataset', 'FeatureDataset');
        INSERT INTO "{schema}".metadata_v2_publications_idx
            (environment, revision, publication_id, service_id, resource_id, publication_type)
        VALUES ('Test', 2, 'layer', 'service', 'dataset', 'OgcCollection');
        INSERT INTO "{schema}".metadata_v2_storage_bindings_idx
            (environment, revision, storage_binding_id, resource_id, storage_type, locator)
        VALUES ('Test', 2, 'binding', 'dataset', 'RelationalTable', 'public.parcels');
        """;

    private async Task WithSchemaAsync(Func<string, Task> action)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(MetadataV2VocabularyMigrationTests));
        try
        {
            await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, schema);
            await action(schema);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task ApplyMigrationAsync(string schema)
    {
        var directory = RepositoryPaths.Resolve("src", "Honua.Server", "Migrations");
        var path = Directory.EnumerateFiles(directory, "*_ExpandMetadataV2Vocabulary.sql").Single();
        await ExecuteAsync(schema, (await File.ReadAllTextAsync(path))
            .Replace("$HonuaSchema$", "\"{schema}\"", StringComparison.Ordinal));
    }

    private async Task ExecuteAsync(string schema, string sql)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql.Replace("{schema}", schema, StringComparison.Ordinal), connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string schema, string sql)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql.Replace("{schema}", schema, StringComparison.Ordinal), connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}

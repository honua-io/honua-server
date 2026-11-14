// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Metadata;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Metadata;

public sealed partial class PostgresMetadataV2GraphStoreFreshDbTests
{
    [IntegrationTest]
    public async Task SaveAsync_MetadataHistory_RecordsOnlyChangedFields()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(SaveAsync_MetadataHistory_RecordsOnlyChangedFields));
        try
        {
            var store = await CreateHistoryStoreAsync(schema);
            var original = await store.SaveAsync(HistoryGraph("Original"), null);
            var changed = await store.SaveAsync(HistoryGraph("Revised", ["survey"]), original.Etag);
            var entries = await ReadHistoryAsync(schema, changed.Revision);

            entries.Should().BeEquivalentTo(new[]
            {
                new HistoryEntry("resources", "survey", ["title"], "\"Original\"", "\"Revised\"", "system:metadata-publisher"),
                new HistoryEntry("resources", "survey", ["keywords"], "[]", "[\"survey\"]", "system:metadata-publisher")
            });
            var unchanged = await store.SaveAsync(changed.Graph, changed.Etag);
            (await ReadHistoryAsync(schema, unchanged.Revision)).Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ActivateRevisionAsync_MetadataHistory_RecordsReversionBeyondSnapshotRetention()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(ActivateRevisionAsync_MetadataHistory_RecordsReversionBeyondSnapshotRetention));
        try
        {
            var store = await CreateHistoryStoreAsync(schema);
            var original = await store.SaveAsync(HistoryGraph("Original"), null);
            var changed = await store.SaveAsync(HistoryGraph("Revised"), original.Etag);
            await store.ActivateRevisionAsync(original.Revision, changed.Etag);
            var entries = await ReadHistoryAsync(schema, original.Revision);
            entries.Should().ContainEquivalentOf(new HistoryEntry(
                "resources", "survey", ["title"], "\"Revised\"", "\"Original\"", "system:metadata-publisher"));

            var current = original;
            for (var index = 0; index <= PostgresMetadataV2GraphStore.MetadataV2RetainedSnapshotCount; index++)
                current = await store.SaveAsync(current.Graph, current.Etag);

            (await CountAsync(schema, $"metadata_v2_snapshots WHERE revision = {changed.Revision}"))
                .Should().Be(0);
            (await ReadHistoryAsync(schema, changed.Revision)).Should().ContainSingle()
                .Which.NewValue.Should().Be("\"Revised\"");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ActivateRevisionAsync_MetadataHistory_RollsBackWithCurrentPointerAndUsesTransactionActor()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(ActivateRevisionAsync_MetadataHistory_RollsBackWithCurrentPointerAndUsesTransactionActor));
        try
        {
            var store = await CreateHistoryStoreAsync(schema);
            var original = await store.SaveAsync(HistoryGraph("Original"), null);
            var changed = await store.SaveAsync(HistoryGraph("Revised"), original.Etag);
            var before = await CountAsync(schema, "metadata_v2_field_history");
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var command = new NpgsqlCommand($"""
                SELECT set_config('honua.metadata_actor', 'human:reviewer', true);
                UPDATE "{schema}".metadata_v2_current SET revision = @revision, etag = @etag
                WHERE environment = 'Test';
                SELECT actor FROM "{schema}".metadata_v2_field_history ORDER BY id DESC LIMIT 1;
                """, connection, transaction);
            command.Parameters.AddWithValue("revision", original.Revision);
            command.Parameters.AddWithValue("etag", original.Etag);
            await using (var reader = await command.ExecuteReaderAsync())
            {
                await reader.NextResultAsync();
                (await reader.ReadAsync()).Should().BeTrue();
                reader.GetString(0).Should().Be("human:reviewer");
            }
            await transaction.RollbackAsync();

            (await CountAsync(schema, "metadata_v2_field_history")).Should().Be(before);
            (await store.GetCurrentAsync()).Etag.Should().Be(changed.Etag);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task SaveAsync_MetadataHistory_TracksNestedAdditionsAndRemovalsWithoutRuntimeFields()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(SaveAsync_MetadataHistory_TracksNestedAdditionsAndRemovalsWithoutRuntimeFields));
        try
        {
            var store = await CreateHistoryStoreAsync(schema);
            var graph = HistoryGraph("Original");
            var resource = graph.Resources[0];
            graph = graph with
            {
                Resources = [resource with { Metadata = resource.Metadata with
            {
                AdditionalProperties = new() { ["custom"] = JsonSerializer.SerializeToElement(new { source = "survey", pending = (string?)null }) }
            }}]
            };
            var original = await store.SaveAsync(graph, null);
            var changed = await store.SaveAsync(graph with
            {
                Resources = [resource with { Metadata = resource.Metadata with
            {
                AdditionalProperties = new() { ["custom"] = JsonSerializer.SerializeToElement(new { source = "field", empty = new { } }) }
            }, Display = new() { MaxScale = 5000 } }]
            }, original.Etag);

            (await ReadHistoryAsync(schema, changed.Revision)).Should().BeEquivalentTo(new[]
            {
                new HistoryEntry("resources", "survey", ["custom", "source"], "\"survey\"", "\"field\"", "system:metadata-publisher"),
                new HistoryEntry("resources", "survey", ["custom", "pending"], "null", null, "system:metadata-publisher"),
                new HistoryEntry("resources", "survey", ["custom", "empty"], null, "{}", "system:metadata-publisher")
            });
            var removed = await store.SaveAsync(changed.Graph with { Resources = [] }, changed.Etag);
            (await ReadHistoryAsync(schema, removed.Revision)).Should().ContainEquivalentOf(new HistoryEntry(
                "resources", "survey", ["title"], "\"Original\"", null, "system:metadata-publisher"));
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task StageAsync_MetadataHistory_RecordsOnlyActivationAndSeparatesEnvironments()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(StageAsync_MetadataHistory_RecordsOnlyActivationAndSeparatesEnvironments));
        try
        {
            var store = await CreateHistoryStoreAsync(schema);
            var original = await store.SaveAsync(HistoryGraph("Original"), null);
            var before = await CountAsync(schema, "metadata_v2_field_history");
            var staged = await store.StageAsync(HistoryGraph("Proposed"));
            (await CountAsync(schema, "metadata_v2_field_history")).Should().Be(before);
            await store.ActivateRevisionAsync(staged.Revision, original.Etag);
            (await ReadHistoryAsync(schema, staged.Revision)).Should().ContainSingle().Which
                .Should().BeEquivalentTo(new HistoryEntry("resources", "survey", ["title"], "\"Original\"", "\"Proposed\"", "system:metadata-publisher"));

            var other = new PostgresMetadataV2GraphStore(new TestConnectionProvider(fixture.DataSource, schema),
                "Other", FixtureBypassDatabaseSchemaGuard.Instance, schema);
            await other.SaveAsync(HistoryGraph("Other") with { Environment = "Other" }, null);
            (await ReadHistoryAsync(schema, original.Revision)).Should().NotContain(entry => entry.NewValue == "\"Other\"");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task<PostgresMetadataV2GraphStore> CreateHistoryStoreAsync(string schema)
    {
        await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, schema);
        return new(new TestConnectionProvider(fixture.DataSource, schema),
            "Test", FixtureBypassDatabaseSchemaGuard.Instance, schema);
    }

    private static MetadataV2Graph HistoryGraph(string title, string[]? keywords = null) => new()
    {
        Environment = "Test",
        GeneratedAt = DateTimeOffset.UtcNow,
        Resources = [new MetadataV2Resource
        {
            Metadata = new() { Id = "survey", Name = "survey", Title = title, Keywords = keywords ?? [] },
            Type = MetadataV2ResourceType.FeatureDataset
        }]
    };

    private async Task<List<HistoryEntry>> ReadHistoryAsync(string schema, long revision)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT item_kind, item_id, field_path, old_value::text, new_value::text, actor
            FROM "{schema}".metadata_v2_field_history
            WHERE environment = 'Test' AND revision = @revision ORDER BY id;
            """, connection);
        command.Parameters.AddWithValue("revision", revision);
        await using var reader = await command.ExecuteReaderAsync();
        var entries = new List<HistoryEntry>();
        while (await reader.ReadAsync())
            entries.Add(new(reader.GetString(0), reader.GetString(1), reader.GetFieldValue<string[]>(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5)));
        return entries;
    }

    private sealed record HistoryEntry(string ItemKind, string ItemId, string[] FieldPath,
        string? OldValue, string? NewValue, string Actor);
}

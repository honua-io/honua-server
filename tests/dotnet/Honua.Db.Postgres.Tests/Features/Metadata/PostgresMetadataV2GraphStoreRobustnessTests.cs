// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Metadata;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Metadata;

public sealed partial class PostgresMetadataV2GraphStoreFreshDbTests
{
    [IntegrationTheory]
    [InlineData("future-dataset-kind", "future-lifecycle", "future-state")]
    [InlineData("feature-dataset", "active", "ready")]
    public async Task SaveAsync_FutureSnapshotValues_PreservesUnknownPropertiesAfterKnownEdit(
        string datasetKind, string lifecycle, string operationalState)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresMetadataV2GraphStoreFreshDbTests));
        try
        {
            await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, schema);
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var store = new PostgresMetadataV2GraphStore(provider, "Test",
                schemaGuard: FixtureBypassDatabaseSchemaGuard.Instance, schemaName: schema);
            await store.SaveAsync(new MetadataV2Graph { Environment = "Test" }, expectedEtag: null);
            await using (var connection = await fixture.DataSource.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    UPDATE "{schema}".metadata_v2_snapshots
                    SET document = document || $1::jsonb
                    WHERE environment = 'Test';
                    """;
                command.Parameters.AddWithValue($$$"""
                    {
                      "futureRoot": {"values": [1, null, {"enabled": true}]},
                      "resources": [{
                        "metadata": {"id": "future-dataset", "name": "future", "futureMetadata": "retained"},
                        "type": "{{{datasetKind}}}",
                        "futureDataset": ["retained"],
                        "status": {"lifecycle": "{{{lifecycle}}}", "state": "{{{operationalState}}}", "futureStatus": false,
                          "conditions": [{"type": "future", "status": "True", "futureCondition": {"value": 7}}]}
                      }]
                    }
                    """);
                await command.ExecuteNonQueryAsync();
            }

            var loaded = await new PostgresMetadataV2GraphStore(provider, "Test",
                schemaGuard: FixtureBypassDatabaseSchemaGuard.Instance, schemaName: schema).GetCurrentAsync();
            var dataset = loaded.Graph.Resources.Should().ContainSingle().Subject;
            dataset.Type.ToString().Should().Be(datasetKind == "feature-dataset" ? "FeatureDataset" : "Unknown");
            dataset.Status.Lifecycle.ToString().Should().Be(lifecycle == "active" ? "Active" : "Unknown");
            var saved = await store.SaveAsync(loaded.Graph with
            {
                Resources = [dataset with { Metadata = dataset.Metadata with { Title = "Edited title" } }]
            }, loaded.Etag);
            var reloaded = await new PostgresMetadataV2GraphStore(provider, "Test",
                schemaGuard: FixtureBypassDatabaseSchemaGuard.Instance, schemaName: schema).GetCurrentAsync();
            reloaded.Revision.Should().BeGreaterThan(loaded.Revision);
            reloaded.Etag.Should().Be(saved.Etag);
            reloaded.Graph.Resources[0].Metadata.Title.Should().Be("Edited title");
            AssertUnknownPropertiesRetained(reloaded.Graph);
            using var persisted = await ReadPersistedSnapshotAsync(schema);
            var persistedDataset = persisted.RootElement.GetProperty("resources")[0];
            persistedDataset.GetProperty("type").GetString().Should().Be(datasetKind);
            persistedDataset.GetProperty("status").GetProperty("lifecycle").GetString().Should().Be(lifecycle);
            persistedDataset.GetProperty("status").GetProperty("state").GetString().Should().Be(operationalState);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task<JsonDocument> ReadPersistedSnapshotAsync(string schema)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT document::text FROM "{schema}".metadata_v2_snapshots WHERE environment = 'Test';
            """;
        return JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);
    }

    private static void AssertUnknownPropertiesRetained(MetadataV2Graph graph)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(graph,
            MetadataV2JsonContext.Default.MetadataV2Graph));
        var root = document.RootElement;
        root.GetProperty("futureRoot").GetProperty("values")[2].GetProperty("enabled").GetBoolean().Should().BeTrue();
        var persistedDataset = root.GetProperty("resources")[0];
        persistedDataset.GetProperty("futureDataset")[0].GetString().Should().Be("retained");
        persistedDataset.GetProperty("metadata").GetProperty("futureMetadata").GetString().Should().Be("retained");
        var status = persistedDataset.GetProperty("status");
        status.GetProperty("futureStatus").GetBoolean().Should().BeFalse();
        status.GetProperty("conditions")[0].GetProperty("futureCondition").GetProperty("value").GetInt32().Should().Be(7);
    }
}

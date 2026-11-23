// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Metadata;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Metadata;

public sealed partial class PostgresMetadataV2GraphStoreFreshDbTests
{
    // A managed layer that declares Create/Update/Delete on an edit-capable binding is modifiable,
    // whatever canModify value an older release stored for it.
    [IntegrationTest]
    public async Task GetCurrentAsync_StoredManagedEditableLayerWithStaleCanModify_ReadsAsModifiable()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("editing_upgrade");
        try
        {
            await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, schema);
            var saved = await CreateStore(schema).SaveAsync(StaleCanModifyGraph(schema), expectedEtag: null);
            (await ReadStoredCanModifyAsync(schema, saved.Etag)).Should().Be("true", "writes persist the repaired value");
            await PlantStoredCanModifyFalseAsync(schema, saved.Etag);

            var reader = CreateStore(schema);
            var current = await reader.GetCurrentAsync();
            current.Etag.Should().Be(saved.Etag);
            current.Graph.Resources.Single().Editing!.CanModify.Should().BeTrue();
            current.Graph.Resources.Single().Editing!.SupportsAttachments.Should().BeTrue();
            (await reader.GetByRevisionAsync(saved.Graph.Revision))!.Graph.Resources.Single().Editing!.CanModify
                .Should().BeTrue();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task SaveAsync_StaleCanModifyGraph_CachedSnapshotIsModifiable()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("editing_upgrade_cache");
        try
        {
            await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, schema);
            var store = CreateStore(schema);

            var saved = await store.SaveAsync(StaleCanModifyGraph(schema), expectedEtag: null);

            saved.Graph.Resources.Single().Editing!.CanModify.Should().BeTrue();
            (await store.GetCurrentAsync()).Graph.Resources.Single().Editing!.CanModify.Should().BeTrue();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private PostgresMetadataV2GraphStore CreateStore(string schema)
        => new(new TestConnectionProvider(fixture.DataSource, schema), "Test", FixtureBypassDatabaseSchemaGuard.Instance, schema);

    private async Task<object?> ReadStoredCanModifyAsync(string schema, string etag)
    {
        await using var connection = await fixture.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT document -> 'resources' -> 0 -> 'editing' ->> 'canModify'
            FROM "{schema}".metadata_v2_snapshots WHERE etag = @etag
            """;
        command.Parameters.AddWithValue("etag", etag);
        return await command.ExecuteScalarAsync();
    }

    // Rewrites the stored document to the value older releases wrote for this layer.
    private async Task PlantStoredCanModifyFalseAsync(string schema, string etag)
    {
        await using var connection = await fixture.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $$"""
            UPDATE "{{schema}}".metadata_v2_snapshots
            SET document = jsonb_set(document, '{resources,0,editing,canModify}', 'false'::jsonb)
            WHERE etag = @etag
            """;
        command.Parameters.AddWithValue("etag", etag);
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
        (await ReadStoredCanModifyAsync(schema, etag)).Should().Be("false");
    }

    private static MetadataV2Graph StaleCanModifyGraph(string schema) => new()
    {
        Environment = "Test",
        GeneratedAt = DateTimeOffset.UtcNow,
        Connections = [new MetadataV2Connection { Metadata = new() { Id = "conn", Name = "managed" } }],
        StorageBindings = [new MetadataV2StorageBinding
        {
            Metadata = new() { Id = "binding", Name = "features" },
            ResourceId = "resource", StorageLayerId = 7,
            Locator = $"{schema}.features",
            Capabilities = [MetadataV2StorageBindingCapability.Query, MetadataV2StorageBindingCapability.Edit]
        }],
        Resources = [new MetadataV2Object
        {
            Metadata = new() { Id = "resource", Name = "features" },
            StorageBindingIds = ["binding"],
            Editing = new() { CanModify = false, SupportsAttachments = true }
        }],
        Services = [new MetadataV2Service { Metadata = new() { Id = "service", Name = "upgrade" } }],
        Publications = [new MetadataV2Layer
        {
            Metadata = new() { Id = "publication", Name = "features" },
            ResourceId = "resource", ServiceId = "service", StorageBindingId = "binding",
            Capabilities = ["Query", "Create", "Update", "Delete"]
        }]
    };
}

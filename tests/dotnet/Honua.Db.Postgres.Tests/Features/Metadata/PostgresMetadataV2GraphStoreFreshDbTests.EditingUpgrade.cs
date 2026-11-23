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
    // A managed layer published before canModify was derived on publish is stored with
    // editing.canModify=false; reading the graph after an upgrade must make it editable.
    [IntegrationTest]
    public async Task GetCurrentAsync_StoredManagedEditableLayerWithStaleCanModify_ReadsAsModifiable()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("editing_upgrade");
        try
        {
            await CoreMigrationTestFixture.ApplyMetadataV2Async(fixture, schema);
            var writer = new PostgresMetadataV2GraphStore(new TestConnectionProvider(fixture.DataSource, schema),
                "Test", FixtureBypassDatabaseSchemaGuard.Instance, schema);
            var saved = await writer.SaveAsync(PreUpgradeManagedEditableGraph(schema), expectedEtag: null);
            await using (var connection = await fixture.GetConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    SELECT document -> 'resources' -> 0 -> 'editing' ->> 'canModify'
                    FROM "{schema}".metadata_v2_snapshots WHERE etag = @etag
                    """;
                command.Parameters.AddWithValue("etag", saved.Etag);
                (await command.ExecuteScalarAsync()).Should().Be("false", "the stored document keeps the pre-upgrade value");
            }

            var reader = new PostgresMetadataV2GraphStore(new TestConnectionProvider(fixture.DataSource, schema),
                "Test", FixtureBypassDatabaseSchemaGuard.Instance, schema);
            var current = await reader.GetCurrentAsync();
            current.Etag.Should().Be(saved.Etag);
            var editing = current.Graph.Resources.Single().Editing!;
            editing.CanModify.Should().BeTrue();
            editing.SupportsAttachments.Should().BeTrue();
            (await reader.GetByRevisionAsync(saved.Graph.Revision))!.Graph.Resources.Single().Editing!.CanModify
                .Should().BeTrue();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static MetadataV2Graph PreUpgradeManagedEditableGraph(string schema) => new()
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

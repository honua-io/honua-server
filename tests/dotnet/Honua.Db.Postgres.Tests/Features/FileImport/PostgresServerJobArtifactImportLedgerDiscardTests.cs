// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Admin;
using Honua.Db.Postgres.Features.FileImport;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.FileImport;

[Collection("Database")]
[Protocol(ProtocolNames.TestQuality)]
public sealed partial class PostgresServerJobArtifactImportLedgerDiscardTests(PostgresFixture fixture)
{
    private const string TenantId = "tenant-discard";
    private const string ActorId = "actor-discard";

    [IntegrationTest]
    [Operation(Operations.Import)]
    public async Task DiscardAsync_PublishedLayerUnderNonDefaultMetadataSchema_RefusesDrop()
    {
        var metaSchema = await fixture.CreateIsolatedSchemaAsync("meta_7664");
        var dataSchema = await fixture.CreateIsolatedSchemaAsync("data_7664");
        try
        {
            var seed = new ReceiptSeed
            {
                MetaSchema = metaSchema,
                DataSchema = dataSchema,
                IntentId = "intent-publish-then-discard",
                LogicalTable = "staged_7664_published_table",
                PhysicalTable = "imported_staged_7664_published_table",
            };
            await SeedOwnedStagedImportAsync(seed);
            var schemaConfig = new PostgresSchemaConfiguration(metaSchema, dataSchema, [dataSchema]);
            var published = await PublishStagedTableAsync(schemaConfig, seed);
            published.StorageLayerId.Should().BeGreaterThan(0);
            (await CountLayersInSchemaAsync(metaSchema, dataSchema, seed.PhysicalTable)).Should().Be(1);

            await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
            var ledger = new PostgresServerJobArtifactImportLedger(dataSource, schemaConfig);
            var discard = () => ledger.DiscardAsync(DiscardScope(TenantId, seed), CancellationToken.None);

            await discard.Should().ThrowAsync<ServerJobArtifactImportReferencedException>();
            (await TableExistsAsync(dataSchema, seed.PhysicalTable)).Should().BeTrue();
        }
        finally
        {
            await fixture.DropSchemaAsync(dataSchema);
            await fixture.DropSchemaAsync(metaSchema);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Import)]
    public async Task DiscardAsync_UnreferencedOwnedTable_DropsIsIdempotentAndBlocksRetry()
    {
        var metaSchema = await fixture.CreateIsolatedSchemaAsync("meta_7632");
        var dataSchema = await fixture.CreateIsolatedSchemaAsync("data_7632");
        try
        {
            var seed = new ReceiptSeed
            {
                MetaSchema = metaSchema,
                DataSchema = dataSchema,
                IntentId = "intent-drop-and-retry",
                LogicalTable = "staged_7632_unreferenced",
                PhysicalTable = "imported_staged_7632_unreferenced",
            };
            await SeedOwnedStagedImportAsync(seed);
            var schemaConfig = new PostgresSchemaConfiguration(metaSchema, dataSchema, [dataSchema]);
            await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
            var ledger = new PostgresServerJobArtifactImportLedger(dataSource, schemaConfig);
            await AssertDiscardLifecycleAsync(ledger, seed);
        }
        finally
        {
            await fixture.DropSchemaAsync(dataSchema);
            await fixture.DropSchemaAsync(metaSchema);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Import)]
    public async Task DiscardAsync_TableWithoutImportOwnershipMarker_RefusesDrop()
    {
        var metaSchema = await fixture.CreateIsolatedSchemaAsync("meta_7632_owned");
        var dataSchema = await fixture.CreateIsolatedSchemaAsync("data_7632_owned");
        try
        {
            var seed = new ReceiptSeed
            {
                MetaSchema = metaSchema,
                DataSchema = dataSchema,
                IntentId = "intent-not-owned",
                LogicalTable = "staged_7632_unmarked",
                PhysicalTable = "imported_staged_7632_unmarked",
            };
            await CreateMetadataSchemaCatalogAsync(metaSchema);
            await CreateUnmarkedStagedTableAsync(dataSchema, seed.PhysicalTable);
            await InsertCommittedReceiptAsync(seed);

            var schemaConfig = new PostgresSchemaConfiguration(metaSchema, dataSchema, [dataSchema]);
            await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
            var ledger = new PostgresServerJobArtifactImportLedger(dataSource, schemaConfig);
            var act = () => ledger.DiscardAsync(DiscardScope(TenantId, seed), CancellationToken.None);

            await act.Should().ThrowAsync<ServerJobArtifactImportNotOwnedException>();
            (await TableExistsAsync(dataSchema, seed.PhysicalTable)).Should().BeTrue();
        }
        finally
        {
            await fixture.DropSchemaAsync(dataSchema);
            await fixture.DropSchemaAsync(metaSchema);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Import)]
    public async Task DiscardAsync_ReferencedByStorageBinding_RefusesDrop()
    {
        var metaSchema = await fixture.CreateIsolatedSchemaAsync("meta_7632_sb");
        var dataSchema = await fixture.CreateIsolatedSchemaAsync("data_7632_sb");
        try
        {
            var seed = new ReceiptSeed
            {
                MetaSchema = metaSchema,
                DataSchema = dataSchema,
                IntentId = "intent-storage-binding-ref",
                LogicalTable = "staged_7632_bound",
                PhysicalTable = "imported_staged_7632_bound",
            };
            await SeedOwnedStagedImportAsync(seed);
            await fixture.ExecuteAsync(
                $"""
                INSERT INTO "{metaSchema}".metadata_v2_current (environment, revision) VALUES ('default', 1);
                INSERT INTO "{metaSchema}".metadata_v2_storage_bindings_idx (
                    environment, revision, storage_binding_id, resource_id, connection_id, storage_type, locator)
                VALUES ('default', 1, 'sb-1', 'res-1', NULL, 'postgres', '{dataSchema}.{seed.PhysicalTable}');
                """);

            var schemaConfig = new PostgresSchemaConfiguration(metaSchema, dataSchema, [dataSchema]);
            await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
            var ledger = new PostgresServerJobArtifactImportLedger(dataSource, schemaConfig);
            var act = () => ledger.DiscardAsync(DiscardScope(TenantId, seed), CancellationToken.None);

            await act.Should().ThrowAsync<ServerJobArtifactImportReferencedException>();
            (await TableExistsAsync(dataSchema, seed.PhysicalTable)).Should().BeTrue();
        }
        finally
        {
            await fixture.DropSchemaAsync(dataSchema);
            await fixture.DropSchemaAsync(metaSchema);
        }
    }

    private async Task SeedOwnedStagedImportAsync(ReceiptSeed seed)
    {
        await CreateMetadataSchemaCatalogAsync(seed.MetaSchema);
        await CreateOwnedStagedTableAsync(seed.DataSchema, seed.PhysicalTable);
        await InsertCommittedReceiptAsync(seed);
    }

    private async Task AssertDiscardLifecycleAsync(PostgresServerJobArtifactImportLedger ledger, ReceiptSeed seed)
    {
        var wrongTenant = () => ledger.DiscardAsync(DiscardScope("other-tenant", seed), CancellationToken.None);
        await wrongTenant.Should().ThrowAsync<ServerJobArtifactImportNotFoundException>();
        var wrongActor = () => ledger.DiscardAsync(DiscardScope(TenantId, seed) with { ActorId = "other-actor" }, CancellationToken.None);
        await wrongActor.Should().ThrowAsync<ServerJobArtifactImportNotFoundException>();
        (await TableExistsAsync(seed.DataSchema, seed.PhysicalTable)).Should().BeTrue();

        var first = await ledger.DiscardAsync(DiscardScope(TenantId, seed), CancellationToken.None);
        first.AlreadyDiscarded.Should().BeFalse();
        first.PhysicalTable.Should().Be(seed.PhysicalTable);
        (await TableExistsAsync(seed.DataSchema, seed.PhysicalTable)).Should().BeFalse();

        var repeat = await ledger.DiscardAsync(DiscardScope(TenantId, seed), CancellationToken.None);
        repeat.AlreadyDiscarded.Should().BeTrue();

        var retryImport = () => ledger.GetOrImportAsync(
            new ServerJobArtifactImportScope(TenantId, ActorId, seed.IntentId, new string('a', 64), seed.DataSchema, seed.LogicalTable),
            (_, _) => throw new InvalidOperationException("import callback must not run after discard"),
            CancellationToken.None);
        await retryImport.Should().ThrowAsync<ServerJobArtifactImportDiscardedException>();
    }

    private Task<PublishedLayerSummary> PublishStagedTableAsync(PostgresSchemaConfiguration schemaConfig, ReceiptSeed seed)
    {
        var publisher = CreatePublisher(schemaConfig);
        var publishConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            SearchPath = $"{seed.MetaSchema},public",
        }.ConnectionString;
        return publisher.PublishLayerAsync(
            publishConnectionString,
            new LayerPublishRequest
            {
                Schema = seed.DataSchema,
                Table = seed.PhysicalTable,
                LayerName = "Staged Published Layer",
                GeometryColumn = "geom",
                Srid = 4326,
                PrimaryKey = "objectid",
                ServiceName = "svc-7664",
                Enabled = true,
            },
            CancellationToken.None);
    }

    private static ServerJobArtifactImportDiscardScope DiscardScope(string tenantId, ReceiptSeed seed) => new()
    {
        TenantId = tenantId,
        ActorId = ActorId,
        IntentId = seed.IntentId,
        ExpectedTable = seed.LogicalTable,
    };

    private static PostgreSqlLayerPublishingService CreatePublisher(PostgresSchemaConfiguration schemaConfig)
    {
        var graphStore = new Mock<IMetadataV2GraphStore>();
        var emptyGraph = new MetadataV2Graph
        {
            Services = [],
            Resources = [],
            StorageBindings = [],
            Publications = [],
            Connections = [],
        };
        graphStore
            .Setup(store => store.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetadataV2GraphSnapshot(emptyGraph, "\"etag-1\"", DateTimeOffset.UtcNow));
        graphStore
            .Setup(store => store.SaveAsync(It.IsAny<MetadataV2Graph>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MetadataV2Graph graph, string? _, CancellationToken _) =>
                new MetadataV2GraphSnapshot(graph, "\"etag-2\"", DateTimeOffset.UtcNow));

        return new PostgreSqlLayerPublishingService(
            new PostgreSqlTableDiscoveryService(
                NullLogger<PostgreSqlTableDiscoveryService>.Instance,
                schemaContext: null,
                schemaConfiguration: schemaConfig),
            graphStore.Object,
            NullLogger<PostgreSqlLayerPublishingService>.Instance,
            metadataSchema: schemaConfig.MetadataSchema,
            schemaConfiguration: schemaConfig);
    }
}

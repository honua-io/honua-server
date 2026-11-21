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
public sealed class PostgresServerJobArtifactImportLedgerDiscardTests(PostgresFixture fixture)
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

    private async Task CreateMetadataSchemaCatalogAsync(string metaSchema)
    {
        await CreateLayerCatalogTablesAsync(metaSchema);
        await CreateServiceCatalogTablesAsync(metaSchema);
        await CreateMetadataV2AndFeatureTablesAsync(metaSchema);
        await ApplyReceiptMigrationsAsync(metaSchema);
    }

    private Task CreateLayerCatalogTablesAsync(string metaSchema)
    {
        var q = $"\"{metaSchema}\"";
        return fixture.ExecuteAsync(
            $$"""
            CREATE TABLE {{q}}.layers (
                layer_id SERIAL PRIMARY KEY,
                layer_name VARCHAR(255) NOT NULL,
                description TEXT,
                table_schema VARCHAR(255) NOT NULL,
                table_name VARCHAR(255) NOT NULL,
                primary_key_column VARCHAR(255),
                geometry_column VARCHAR(255),
                storage_options JSONB NOT NULL DEFAULT '{}'::jsonb,
                storage_srid INTEGER NOT NULL DEFAULT 4326,
                geometry_type VARCHAR(50) NOT NULL,
                srid INTEGER NOT NULL DEFAULT 4326,
                extent geometry(Polygon, 4326),
                default_visibility BOOLEAN NOT NULL DEFAULT TRUE,
                enabled BOOLEAN NOT NULL DEFAULT TRUE,
                metadata JSONB NOT NULL DEFAULT '{}'::jsonb
            );
            CREATE TABLE {{q}}.layer_fields (
                layer_id INTEGER NOT NULL REFERENCES {{q}}.layers(layer_id) ON DELETE CASCADE,
                field_name VARCHAR(255) NOT NULL,
                field_type VARCHAR(100) NOT NULL,
                field_order INTEGER NOT NULL,
                max_length INTEGER,
                nullable BOOLEAN NOT NULL DEFAULT TRUE,
                default_value TEXT,
                description TEXT
            );
            """);
    }

    private Task CreateServiceCatalogTablesAsync(string metaSchema)
    {
        var q = $"\"{metaSchema}\"";
        return fixture.ExecuteAsync(
            $$"""
            CREATE TABLE {{q}}.services (
                service_name VARCHAR(255) PRIMARY KEY,
                description TEXT,
                srid INTEGER NOT NULL DEFAULT 4326,
                supported_formats TEXT[] NOT NULL DEFAULT ARRAY['JSON', 'GeoJSON'],
                capabilities TEXT[] NOT NULL DEFAULT ARRAY['Query', 'Extract'],
                service_extent geometry(Polygon, 4326),
                connection_id UUID,
                updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            CREATE TABLE {{q}}.service_layers (
                service_name VARCHAR(255) NOT NULL REFERENCES {{q}}.services(service_name) ON DELETE CASCADE,
                layer_id INTEGER NOT NULL REFERENCES {{q}}.layers(layer_id) ON DELETE CASCADE,
                layer_order INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (service_name, layer_id)
            );
            """);
    }

    private Task CreateMetadataV2AndFeatureTablesAsync(string metaSchema)
    {
        var q = $"\"{metaSchema}\"";
        return fixture.ExecuteAsync(
            $$"""
            CREATE TABLE {{q}}.features (
                objectid BIGSERIAL PRIMARY KEY,
                layer_id INTEGER NOT NULL REFERENCES {{q}}.layers(layer_id) ON DELETE CASCADE,
                global_id UUID DEFAULT gen_random_uuid(),
                attributes JSONB NOT NULL DEFAULT '{}'::jsonb,
                geometry geometry,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            CREATE TABLE {{q}}.metadata_v2_current (
                environment TEXT PRIMARY KEY,
                revision BIGINT NOT NULL
            );
            CREATE TABLE {{q}}.metadata_v2_storage_bindings_idx (
                environment TEXT NOT NULL,
                revision BIGINT NOT NULL,
                storage_binding_id TEXT NOT NULL,
                resource_id TEXT NOT NULL,
                connection_id TEXT,
                storage_type TEXT NOT NULL,
                locator TEXT NOT NULL,
                PRIMARY KEY (environment, revision, storage_binding_id)
            );
            """);
    }

    private async Task ApplyReceiptMigrationsAsync(string metaSchema)
    {
        var q = $"\"{metaSchema}\"";
        var receiptSql = await File.ReadAllTextAsync(RepositoryPaths.Resolve(
            "src", "Honua.Server", "Migrations", "138_CreateJobArtifactImportReceipt.sql"));
        var discardSql = await File.ReadAllTextAsync(RepositoryPaths.Resolve(
            "src", "Honua.Server", "Migrations", "224_AddDiscardedPhaseToJobArtifactImportReceipt.sql"));
        await fixture.ExecuteAsync(receiptSql.Replace("$HonuaSchema$", q, StringComparison.Ordinal));
        await fixture.ExecuteAsync(discardSql.Replace("$HonuaSchema$", q, StringComparison.Ordinal));
    }

    private async Task CreateOwnedStagedTableAsync(string dataSchema, string physicalTable)
    {
        await CreateUnmarkedStagedTableAsync(dataSchema, physicalTable);
        await fixture.ExecuteAsync(ImportTableOwnership.MarkSql(dataSchema, physicalTable));
    }

    private async Task CreateUnmarkedStagedTableAsync(string dataSchema, string physicalTable)
    {
        var qTable = $"\"{dataSchema}\".\"{physicalTable}\"";
        await fixture.ExecuteAsync(
            $"""
            CREATE TABLE {qTable} (
                objectid SERIAL PRIMARY KEY,
                name TEXT,
                geom geometry(Point, 4326)
            );
            INSERT INTO {qTable} (name, geom)
            VALUES ('parcel-1', ST_SetSRID(ST_MakePoint(-157.8583, 21.3069), 4326));
            """);
    }

    private Task InsertCommittedReceiptAsync(ReceiptSeed seed)
    {
        var scopeText = TenantId + "\n" + ActorId + "\n" + seed.IntentId;
        var scopeKey = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(scopeText)));
        return fixture.ExecuteAsync(
            $"""
            INSERT INTO "{seed.MetaSchema}".job_artifact_import_receipt (
                scope_key, tenant_id, actor_id, intent_id, fingerprint, phase,
                schema_name, table_name, physical_table, feature_count,
                source_srid, target_srid, content_sha256, crs_state)
            VALUES (
                '{scopeKey}', '{TenantId}', '{ActorId}', '{seed.IntentId}', '{new string('a', 64)}', 'committed',
                '{seed.DataSchema}', '{seed.LogicalTable}', '{seed.PhysicalTable}', 1,
                4326, 4326, '{new string('b', 64)}', 'known');
            """);
    }

    private async Task<long> CountLayersInSchemaAsync(string metaSchema, string dataSchema, string physicalTable)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM \"{metaSchema}\".layers WHERE table_schema = @schema AND table_name = @table",
            connection);
        command.Parameters.AddWithValue("schema", dataSchema);
        command.Parameters.AddWithValue("table", physicalTable);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<bool> TableExistsAsync(string dataSchema, string physicalTable)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT to_regclass(@relation) IS NOT NULL", connection);
        command.Parameters.AddWithValue("relation", $"\"{dataSchema}\".\"{physicalTable}\"");
        return await command.ExecuteScalarAsync() is true;
    }

    private sealed record ReceiptSeed
    {
        public required string MetaSchema { get; init; }
        public required string DataSchema { get; init; }
        public required string IntentId { get; init; }
        public required string LogicalTable { get; init; }
        public required string PhysicalTable { get; init; }
    }
}

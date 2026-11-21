// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Db.Postgres.Features.FileImport;
using Honua.TestKit;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.FileImport;

public sealed partial class PostgresServerJobArtifactImportLedgerDiscardTests
{
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
            "src", "Honua.Server", "Migrations", "225_AddDiscardedAtToJobArtifactImportReceipt.sql"));
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

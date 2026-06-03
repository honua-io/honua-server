// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Postgres.Features.Metadata;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Metadata;

/// <summary>
/// Regression tests for honua-server#1412 — the Metadata v2 cutover (~2026-05-18) made
/// every protocol that resolves through <see cref="PostgresMetadataV2GraphStore"/> require
/// an activated v2 snapshot, which regressed OGC API Features <c>/collections</c> (and the
/// collection-detail/items paths) to HTTP 500 on any deployment whose only catalog data is
/// the legacy V1 catalog (<c>honua.services</c> / <c>honua.layers</c>). The store now
/// synthesizes a compat snapshot from the V1 catalog when no v2 snapshot is activated, and
/// still serves the activated snapshot when one exists.
/// </summary>
[Collection("Database")]
public sealed class PostgresMetadataV2GraphStoreCompatFallbackTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task GetCurrentAsync_NoV2SnapshotButV1CatalogPresent_SynthesizesCompatSnapshotFromV1()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresMetadataV2GraphStoreCompatFallbackTests));
        try
        {
            await SeedV1CatalogAsync(fixture.DataSource, schema, serviceName: "cite_features", layerId: 700, layerName: "CITE Features");

            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var store = new PostgresMetadataV2GraphStore(provider, environment: "Test", schemaName: schema);

            // No metadata_v2_current row exists for this schema, so before the fix this threw
            // InvalidOperationException ("No Metadata v2 snapshot has been activated ...").
            var snapshot = await store.GetCurrentAsync();

            snapshot.Should().NotBeNull();
            snapshot.Graph.Environment.Should().Be("Test");

            // The OGC API Features collections path walks IsPrimary OgcFeatures publications and
            // resolves them to resources + storage layer ids. Assert the synthesized graph carries
            // an OGC-API-Features service, a feature resource, and an ogc-collection publication for
            // the seeded V1 layer so /collections, the collection detail, and items all resolve.
            snapshot.Graph.Services.Should().Contain(s => s.ServiceType == MetadataV2ServiceType.OgcApiFeatures,
                "the compat snapshot must expose an OGC API Features service so /collections resolves");

            var resource = snapshot.Graph.Resources.Should()
                .ContainSingle(r => r.Metadata.Id == "res-layer-700").Which;
            resource.Metadata.Name.Should().Be("CITE Features");

            var ogcPublication = snapshot.Graph.Publications.Should()
                .ContainSingle(p => p.Metadata.Id == "pub-cite-features-ogc-700").Which;
            ogcPublication.ResourceId.Should().Be("res-layer-700");
            ogcPublication.LayerIndex.Should().Be(700, "the items/query path resolves the storage layer id from LayerIndex");

            // The shared-`features` storage binding must carry the discriminator/geometry/attributes
            // columns so the storage-mapped reader projects geometry and constrains reads to the layer.
            var binding = snapshot.Graph.StorageBindings.Should()
                .ContainSingle(b => b.Metadata.Id == "storage-layer-700").Which;
            binding.StorageLayerId.Should().Be(700);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task GetCurrentAsync_WhenV2SnapshotActivated_ServesActivatedSnapshotNotCompat()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresMetadataV2GraphStoreCompatFallbackTests));
        try
        {
            // Seed a V1 catalog that WOULD compile to a compat snapshot, so the test proves the
            // activated v2 snapshot takes precedence rather than merely that one path works.
            await SeedV1CatalogAsync(fixture.DataSource, schema, serviceName: "legacy_v1", layerId: 800, layerName: "Legacy V1 Layer");

            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var store = new PostgresMetadataV2GraphStore(provider, environment: "Test", schemaName: schema);

            // Activate a real v2 snapshot (SaveAsync self-heals the v2 schema + activates the
            // revision). Its single resource id is intentionally NOT one the compat compiler
            // would emit (res-layer-800), so we can tell the two paths apart.
            var graph = new MetadataV2Graph
            {
                Environment = "Test",
                Revision = 1,
                GeneratedAt = DateTimeOffset.UtcNow,
                Resources =
                [
                    new MetadataV2Resource
                    {
                        Metadata = new MetadataV2ObjectMetadata { Id = "res-activated-v2", Name = "activated-v2-layer" },
                        Type = MetadataV2ResourceType.FeatureDataset,
                    },
                ],
            };
            await store.SaveAsync(graph, expectedEtag: null);

            // A fresh store instance avoids any in-process cache so the read goes back to Postgres.
            var readStore = new PostgresMetadataV2GraphStore(provider, environment: "Test", schemaName: schema);
            var snapshot = await readStore.GetCurrentAsync();

            snapshot.Graph.Resources.Should().ContainSingle()
                .Which.Metadata.Id.Should().Be("res-activated-v2",
                    "the activated v2 snapshot must take precedence over the V1 compat fallback");
            snapshot.Graph.Resources.Should().NotContain(r => r.Metadata.Id == "res-layer-800",
                "the compat fallback must not run when a v2 snapshot is activated");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task GetCurrentAsync_BareLayerWithNoServicePublication_StillSynthesizesCollection()
    {
        // The CITE OGC API Features seed (docker/cite/ogc-api-features/seed.sql) inserts ONLY
        // honua.layers — no honua.services / honua.service_layers rows. Pre-cutover the
        // collections path listed that bare layer as a collection; the compat synthesis must
        // do the same (attaching a synthetic per-layer OGC service) so /collections is 200.
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresMetadataV2GraphStoreCompatFallbackTests));
        try
        {
            await CreateV1CatalogTablesAsync(fixture.DataSource, schema);
            await ExecuteAsync(fixture.DataSource, schema, $"""
                INSERT INTO "{schema}".layers (
                    layer_id, layer_name, description, table_schema, table_name,
                    geometry_type, srid, extent, default_visibility, metadata
                )
                VALUES (
                    0, 'CITE Features', 'Bare layer with no service publication', '{schema}', 'features',
                    'Point', 4326, ST_MakeEnvelope(-180, -90, 180, 90, 4326), TRUE,
                    jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', true))
                );
                """);

            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var store = new PostgresMetadataV2GraphStore(provider, environment: "Test", schemaName: schema);

            var snapshot = await store.GetCurrentAsync();

            snapshot.Graph.Services.Should().Contain(s => s.ServiceType == MetadataV2ServiceType.OgcApiFeatures,
                "a bare honua.layers row must still surface an OGC API Features service");
            snapshot.Graph.Resources.Should().ContainSingle(r => r.Metadata.Id == "res-layer-0")
                .Which.Metadata.Name.Should().Be("CITE Features");
            snapshot.Graph.Publications.Should().Contain(
                p => p.ResourceId == "res-layer-0" && p.PublicationType == MetadataV2PublicationType.OgcCollection,
                "the bare layer must be published as an OGC collection so /collections lists it");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task GetCurrentAsync_NoV2SnapshotAndEmptyV1Catalog_StillThrows()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresMetadataV2GraphStoreCompatFallbackTests));
        try
        {
            // V1 catalog tables exist but contain no published service layers — there is genuinely
            // nothing to serve, so the store keeps the original not-found contract.
            await CreateV1CatalogTablesAsync(fixture.DataSource, schema);

            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var store = new PostgresMetadataV2GraphStore(provider, environment: "Test", schemaName: schema);

            var act = () => store.GetCurrentAsync().AsTask();

            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static async Task SeedV1CatalogAsync(
        NpgsqlDataSource dataSource,
        string schema,
        string serviceName,
        int layerId,
        string layerName)
    {
        await CreateV1CatalogTablesAsync(dataSource, schema);

        await ExecuteAsync(dataSource, schema, $"""
            INSERT INTO "{schema}".services (
                service_name, description, srid, max_record_count,
                supported_formats, capabilities, service_extent, metadata
            )
            VALUES (
                '{serviceName}', 'Compat fallback service', 4326, 1000,
                ARRAY['JSON', 'GeoJSON'], ARRAY['Query'],
                ST_MakeEnvelope(-180, -90, 180, 90, 4326),
                jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', true))
            );
            """);

        await ExecuteAsync(dataSource, schema, $"""
            INSERT INTO "{schema}".layers (
                layer_id, layer_name, description, table_schema, table_name,
                geometry_type, srid, extent, default_visibility, metadata
            )
            VALUES (
                {layerId}, '{layerName}', 'Compat fallback layer', '{schema}', 'features',
                'Point', 4326, ST_MakeEnvelope(-180, -90, 180, 90, 4326), TRUE,
                jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', true))
            );
            """);

        await ExecuteAsync(dataSource, schema, $"""
            INSERT INTO "{schema}".service_layers (service_name, layer_id, layer_order)
            VALUES ('{serviceName}', {layerId}, 0);
            """);

        await ExecuteAsync(dataSource, schema, $"""
            INSERT INTO "{schema}".layer_fields (layer_id, field_name, field_type, field_order, nullable, description)
            VALUES
                ({layerId}, 'objectid', 'Integer', 0, false, 'Object ID'),
                ({layerId}, 'name', 'String', 1, true, 'Name'),
                ({layerId}, 'geometry', 'Geometry', 2, true, 'Geometry');
            """);
    }

    private static async Task CreateV1CatalogTablesAsync(NpgsqlDataSource dataSource, string schema)
    {
        await ExecuteAsync(dataSource, schema, "CREATE EXTENSION IF NOT EXISTS postgis;");

        await ExecuteAsync(dataSource, schema, $$"""
            CREATE TABLE IF NOT EXISTS "{{schema}}".services (
                service_name VARCHAR(64) PRIMARY KEY,
                description TEXT NOT NULL DEFAULT '',
                srid INT NOT NULL DEFAULT 4326,
                max_record_count INT NOT NULL DEFAULT 1000,
                supported_formats TEXT[] NOT NULL DEFAULT '{JSON,GeoJSON}',
                capabilities TEXT[] NOT NULL DEFAULT '{Query,Extract}',
                service_extent GEOMETRY,
                metadata JSONB
            );
            """);

        await ExecuteAsync(dataSource, schema, $$"""
            CREATE TABLE IF NOT EXISTS "{{schema}}".layers (
                layer_id INT PRIMARY KEY,
                layer_name TEXT NOT NULL,
                description TEXT,
                table_schema TEXT NOT NULL DEFAULT current_schema(),
                table_name TEXT NOT NULL,
                primary_key_column TEXT NOT NULL DEFAULT 'objectid',
                geometry_column TEXT DEFAULT 'geometry',
                storage_srid INT,
                storage_options JSONB NOT NULL DEFAULT '{}'::jsonb,
                geometry_type TEXT NOT NULL,
                srid INT NOT NULL DEFAULT 4326,
                extent GEOMETRY,
                default_visibility BOOLEAN NOT NULL DEFAULT TRUE,
                metadata JSONB
            );
            """);

        await ExecuteAsync(dataSource, schema, $"""
            CREATE TABLE IF NOT EXISTS "{schema}".service_layers (
                service_name VARCHAR(64) NOT NULL REFERENCES "{schema}".services(service_name) ON DELETE CASCADE,
                layer_id INT NOT NULL REFERENCES "{schema}".layers(layer_id) ON DELETE CASCADE,
                layer_order INT NOT NULL,
                PRIMARY KEY (service_name, layer_id)
            );
            """);

        await ExecuteAsync(dataSource, schema, $"""
            CREATE TABLE IF NOT EXISTS "{schema}".layer_fields (
                layer_id INT NOT NULL REFERENCES "{schema}".layers(layer_id) ON DELETE CASCADE,
                field_name VARCHAR(64) NOT NULL,
                field_type VARCHAR(32) NOT NULL,
                field_order INT NOT NULL,
                max_length INT,
                nullable BOOLEAN NOT NULL DEFAULT TRUE,
                default_value TEXT,
                description TEXT,
                PRIMARY KEY (layer_id, field_name)
            );
            """);
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string schema, string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var setPath = connection.CreateCommand();
        setPath.CommandText = $"SET search_path TO \"{schema}\", public;";
        await setPath.ExecuteNonQueryAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class TestConnectionProvider(NpgsqlDataSource dataSource, string schemaName) : IDatabaseConnectionProvider
    {
        public string GetConnectionString() => dataSource.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var conn = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SET search_path TO \"{schemaName}\", public;";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            return conn;
        }

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var conn = await OpenConnectionAsync(cancellationToken);
            try
            {
                var tx = await conn.BeginTransactionAsync(isolationLevel, cancellationToken);
                return (conn, tx);
            }
            catch
            {
                await conn.DisposeAsync();
                throw;
            }
        }

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default)
            => operation();

        public Task ExecuteWithDeadlockRetryAsync(
            Func<Task> operation,
            CancellationToken cancellationToken = default)
            => operation();
    }
}

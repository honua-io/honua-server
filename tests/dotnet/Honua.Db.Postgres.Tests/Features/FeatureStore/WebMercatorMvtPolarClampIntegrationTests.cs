// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Tiles;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Formats;
using Microsoft.Extensions.ObjectPool;
using Npgsql;
using CoreGeometryStorageType = Honua.Core.Features.FeatureStore.Abstractions.GeometryStorageType;
using FeatureStoreStringBuilderPooledObjectPolicy = Honua.Db.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

/// <summary>
/// Executes the Web Mercator MVT SQL against PostGIS for a geographic layer with a vertex at
/// latitude -90 (#6109). PROJ rejects ±90 for Mercator, so an unclamped
/// <c>ST_Transform(geom, 3857)</c> aborts the whole tile statement; the managed, source-backed
/// and custom-gridset tile builders must clip through <see cref="DatumTransformSql"/> first.
/// </summary>
[Collection("Database")]
public sealed class WebMercatorMvtPolarClampIntegrationTests : IAsyncLifetime
{
    private const string PolarPolygonWkt = "POLYGON((-180 -90, 180 -90, 180 -60, -180 -60, -180 -90))";

    private static readonly ObjectPool<Dictionary<string, object?>> DictionaryPool =
        new DefaultObjectPoolProvider().Create(
            new Honua.Core.Features.Infrastructure.ServiceRegistration.DictionaryPooledObjectPolicy());

    private static readonly TileOptions Options = new() { TileBuffer = 64, TileExtent = 4096 };

    private static readonly GridGeometry WebMercatorGridset = new()
    {
        Id = "custom-web-mercator",
        Srid = 3857,
        IsGeographic = false,
        TopLeftX = -20037508.342789244,
        TopLeftY = 20037508.342789244,
        TileWidth = 256,
        TileHeight = 256,
        Levels = [new GridLevel(0, 559082264.0287178, 156543.03392804097, 1, 1)],
    };

    private readonly PostgresFixture _fixture;
    private string _schema = null!;

    public WebMercatorMvtPolarClampIntegrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _schema = await _fixture.CreateIsolatedSchemaAsync(nameof(WebMercatorMvtPolarClampIntegrationTests));
        await _fixture.ExecuteAsync($$"""
            CREATE TABLE {{_schema}}.features (
                layer_id integer NOT NULL,
                objectid bigint NOT NULL,
                geometry geometry,
                attributes jsonb
            );
            INSERT INTO {{_schema}}.features (layer_id, objectid, geometry, attributes)
            VALUES (1, 1, ST_GeomFromText('{{PolarPolygonWkt}}', 4326), '{"name":"Antarctica"}'::jsonb);

            CREATE TABLE {{_schema}}.continents (
                objectid bigint PRIMARY KEY,
                geom geometry(Polygon, 4326),
                name text
            );
            INSERT INTO {{_schema}}.continents (objectid, geom, name)
            VALUES (1, ST_GeomFromText('{{PolarPolygonWkt}}', 4326), 'Antarctica');
            """);
    }

    public Task DisposeAsync() => _fixture.DropSchemaAsync(_schema);

    [IntegrationTheory]
    [InlineData(4326)]
    [InlineData(null)]
    public async Task ManagedMvtTile_PolarVertex_ReturnsTile(int? layerSrid)
    {
        var query = CreateQueryBuilder().BuildMvtTileQuery(
            1, 0, 0, 0,
            new FeatureQuery { SpatialReferenceSrid = layerSrid },
            Options,
            new TileLimits(),
            CoreGeometryStorageType.Geometry);

        var payload = await ExecuteManagedTileAsync(query);

        AssertSinglePolarFeature(payload);
    }

    [IntegrationTheory]
    [InlineData(4326)]
    [InlineData(null)]
    public async Task ManagedCustomGridsetMvtTile_PolarVertex_ReturnsTile(int? layerSrid)
    {
        var query = CreateQueryBuilder().BuildMvtTileQuery(
            1, 0, 0, 0,
            new FeatureQuery { SpatialReferenceSrid = layerSrid },
            Options,
            new TileLimits(),
            CoreGeometryStorageType.Geometry,
            WebMercatorGridset);

        var payload = await ExecuteManagedTileAsync(query);

        AssertSinglePolarFeature(payload);
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceBackedMvtTile_PolarVertex_ReturnsTile(bool customGridset)
    {
        var payload = await CreateReader().GetMvtTileAsync(
            1, 0, 0, 0,
            new FeatureQuery(),
            Options,
            new TileLimits(),
            customGridset ? WebMercatorGridset : null);

        AssertSinglePolarFeature(payload);
    }

    private static void AssertSinglePolarFeature(byte[]? payload)
    {
        payload.Should().NotBeNullOrEmpty();
        MvtTileDecoder.Decode(payload!).Layer("layer").Features.Should().ContainSingle();
    }

    private async Task<byte[]?> ExecuteManagedTileAsync(ParameterizedQuery query)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(query.Sql, connection);
        foreach (var parameter in query.WhereParameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        var result = await command.ExecuteScalarAsync();
        return result as byte[];
    }

    private FeatureQueryBuilder CreateQueryBuilder()
    {
        var pool = new DefaultObjectPoolProvider().Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        return new FeatureQueryBuilder(pool, new GeometryProcessor(), _schema);
    }

    private PostgresStorageMappedFeatureReader CreateReader()
    {
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-continents", Name = "Continents" },
            Type = MetadataV2ResourceType.FeatureDataset,
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = MetadataV2GeometryType.Polygon,
                PrimaryGeometryField = "geom",
            },
            SchemaFields =
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.BigInteger, Nullable = false, SemanticRoles = ["id.primary"] },
                new MetadataV2Field { Name = "geom", Type = MetadataV2FieldType.Geometry, Nullable = true, SemanticRoles = ["geometry.primary"] },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String },
            ],
        };

        var mapping = new FeatureStorageMapping(
            TableName: "continents",
            SchemaName: _schema,
            PrimaryKeyColumn: "objectid",
            GeometryColumn: "geom",
            StorageSrid: 4326,
            AttributesColumn: null,
            ProviderOptions: null);

        return new PostgresStorageMappedFeatureReader(
            new FixtureConnectionProvider(_fixture.ConnectionString),
            DictionaryPool,
            resource,
            mapping,
            connection: null,
            connectionEncryptionService: null);
    }

    private sealed class FixtureConnectionProvider : IAdoNetDatabaseConnectionProvider
    {
        private readonly string _connectionString;

        public FixtureConnectionProvider(string connectionString) => _connectionString = connectionString;

        public string GetConnectionString() => _connectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var connection = (NpgsqlConnection)await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
            return (connection, transaction);
        }

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
            => operation();

        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default)
            => operation();
    }
}

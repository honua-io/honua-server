// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using Honua.Core.Configuration;
using Honua.Core.Exceptions;
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
/// A dense zoom-0 tile whose full <c>ST_AsMVT</c> encode exceeds the byte budget must come
/// back within that budget, still decodable, with valid geometry. One byte is still a refusal.
/// </summary>
[Collection("Database")]
public sealed class DenseLowZoomMvtBudgetIntegrationTests : IAsyncLifetime
{
    private const int Budget = 32_768;
    private const int WideBudget = 2_000_000;

    private static readonly ObjectPool<Dictionary<string, object?>> DictionaryPool =
        new DefaultObjectPoolProvider().Create(
            new Honua.Core.Features.Infrastructure.ServiceRegistration.DictionaryPooledObjectPolicy());

    private static readonly TileOptions Options = new() { TileBuffer = 0, TileExtent = 4096 };

    private readonly PostgresFixture _fixture;
    private string _schema = null!;

    public DenseLowZoomMvtBudgetIntegrationTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _schema = await _fixture.CreateIsolatedSchemaAsync(nameof(DenseLowZoomMvtBudgetIntegrationTests));
        await _fixture.ExecuteAsync($"""
            CREATE TABLE {_schema}.features (
                layer_id integer NOT NULL,
                objectid bigint NOT NULL,
                geometry geometry,
                attributes jsonb
            );
            INSERT INTO {_schema}.features (layer_id, objectid, geometry, attributes)
            SELECT 1,
                   g,
                   ST_SetSRID(ST_MakePoint(-120 + (g * 0.02), 35), 4326),
                   jsonb_build_object('name', 'dense-' || g::text, 'payload', repeat('x', 4096) || g::text)
            FROM generate_series(1, 16) AS g;
            INSERT INTO {_schema}.features (layer_id, objectid, geometry, attributes) VALUES
                (1, 101, ST_SetSRID(ST_GeomFromText('POLYGON((-140 20, -135 20, -135 25, -140 25, -140 20))'), 4326),
                    jsonb_build_object('name', 'dense-box-a', 'payload', repeat('y', 4096))),
                (1, 102, ST_SetSRID(ST_GeomFromText('POLYGON((-80 10, -75 10, -75 15, -80 15, -80 10))'), 4326),
                    jsonb_build_object('name', 'dense-box-b', 'payload', repeat('z', 4096)));

            CREATE TABLE {_schema}.dense_features (
                objectid bigint PRIMARY KEY,
                geom geometry(Geometry, 4326),
                name text,
                payload text
            );
            INSERT INTO {_schema}.dense_features (objectid, geom, name, payload)
            SELECT g,
                   ST_SetSRID(ST_MakePoint(-120 + (g * 0.02), 35), 4326),
                   'dense-' || g::text,
                   repeat('x', 4096) || g::text
            FROM generate_series(1, 16) AS g;
            INSERT INTO {_schema}.dense_features (objectid, geom, name, payload) VALUES
                (101, ST_SetSRID(ST_GeomFromText('POLYGON((-140 20, -135 20, -135 25, -140 25, -140 20))'), 4326),
                    'dense-box-a', repeat('y', 4096)),
                (102, ST_SetSRID(ST_GeomFromText('POLYGON((-80 10, -75 10, -75 15, -80 15, -80 10))'), 4326),
                    'dense-box-b', repeat('z', 4096));
            """);
    }

    public Task DisposeAsync() => _fixture.DropSchemaAsync(_schema);

    [IntegrationTheory]
    [InlineData(null)]
    [InlineData(4326)]
    public async Task GetMvtTile_DenseZoom0_ReturnsTileWithinBudget(int? outputSrid)
    {
        var query = new FeatureQuery { SpatialReferenceSrid = 4326, OutputSrid = outputSrid };
        var wide = new TileLimits { MaxTileSize = WideBudget, MaxFeaturesPerTile = 0 };
        var budget = new TileLimits { MaxTileSize = Budget, MaxFeaturesPerTile = 0 };
        var builder = CreateQueryBuilder();

        var full = builder.BuildMvtTileQuery(1, 0, 0, 0, query, Options, wide);
        full.Sql.Should().NotContain("ST_SnapToGrid");
        var unthinned = await ExecuteScalarAsync(full);
        unthinned.Should().NotBeNull();
        unthinned!.Length.Should().BeGreaterThan(Budget, "the historical encode is the tile the budget used to refuse");

        var fitted = await MvtTileBudgetFitter.ReadAsync(
            budget.MaxTileSize,
            step => builder.BuildMvtTileQuery(
                1, 0, 0, 0, query, Options, budget, CoreGeometryStorageType.Geometry, null, step),
            ReadManagedAsync,
            CancellationToken.None);
        AssertWithinBudget(fitted);

        var reader = CreateReader();
        var sourceFull = await reader.GetMvtTileAsync(1, 0, 0, 0, query, Options, wide);
        sourceFull.Should().NotBeNull();
        sourceFull!.Length.Should().BeGreaterThan(Budget);
        var sourceFitted = await reader.GetMvtTileAsync(1, 0, 0, 0, query, Options, budget);
        AssertWithinBudget(sourceFitted);
    }

    [IntegrationTest]
    public async Task GetMvtTile_OneByteBudget_StillRefuses()
    {
        var query = new FeatureQuery { SpatialReferenceSrid = 4326 };
        var options = Options;
        var limits = new TileLimits { MaxTileSize = 1, MaxFeaturesPerTile = 0 };
        var builder = CreateQueryBuilder();

        await Assert.ThrowsAsync<TileSizeLimitExceededException>(() =>
            MvtTileBudgetFitter.ReadAsync(
                limits.MaxTileSize,
                step => builder.BuildMvtTileQuery(
                    1, 0, 0, 0, query, options, limits, CoreGeometryStorageType.Geometry, null, step),
                ReadManagedAsync,
                CancellationToken.None));

        await Assert.ThrowsAsync<TileSizeLimitExceededException>(() =>
            CreateReader().GetMvtTileAsync(1, 0, 0, 0, query, options, limits));
    }

    private static void AssertWithinBudget(byte[]? payload)
    {
        payload.Should().NotBeNull();
        payload!.Length.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(Budget);
        var tile = MvtTileDecoder.Decode(payload);
        tile.FeatureCount.Should().BeGreaterThan(0);
        tile.Layer("layer").Features.Should().Contain(
            feature => feature.GeometryType == MvtGeometryType.Polygon,
            "a polygon that survived generalization is what proves the ring stays valid");
        foreach (var feature in tile.Layer("layer").Features)
        {
            switch (feature.GeometryType)
            {
                case MvtGeometryType.Point:
                    feature.Points.Should().NotBeEmpty();
                    break;
                case MvtGeometryType.LineString:
                    feature.Points.Count().Should().BeGreaterOrEqualTo(2);
                    break;
                case MvtGeometryType.Polygon:
                    feature.Rings.Should().NotBeEmpty();
                    foreach (var ring in feature.Rings)
                    {
                        ring.Count.Should().BeGreaterOrEqualTo(4);
                        ring[0].Should().Be(ring[^1]);
                        TwiceArea(ring).Should().NotBe(0);
                    }

                    break;
            }
        }
    }

    private static long TwiceArea(IReadOnlyList<MvtPoint> ring)
    {
        long area = 0;
        for (var i = 0; i < ring.Count - 1; i++)
        {
            area += (ring[i].X * ring[i + 1].Y) - (ring[i + 1].X * ring[i].Y);
        }

        return area;
    }

    private async Task<byte[]?> ExecuteScalarAsync(ParameterizedQuery query)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(query.Sql, connection);
        foreach (var parameter in query.WhereParameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        return await command.ExecuteScalarAsync() as byte[];
    }

    private async Task<byte[]?> ReadManagedAsync(ParameterizedQuery query, long maxTileSize, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(query.Sql, connection);
        foreach (var parameter in query.WhereParameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        return await PostgresMvtReader.ReadAsync(command, maxTileSize, cancellationToken);
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
            Metadata = new MetadataV2ObjectMetadata { Id = "res-dense", Name = "Dense" },
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
                new MetadataV2Field { Name = "payload", Type = MetadataV2FieldType.String },
            ],
        };

        var mapping = new FeatureStorageMapping(
            TableName: "dense_features",
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

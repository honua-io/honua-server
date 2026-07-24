// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Tiles;
using Honua.Postgres.Features.FeatureStore.Services;
using Honua.TestKit;
using Microsoft.Extensions.ObjectPool;
using Npgsql;

namespace Honua.Postgres.Tests.Features.FeatureStore;

/// <summary>
/// Integration coverage for FlatGeobuf / Geobuf output on source-backed (provider-routed)
/// PostGIS layers via <see cref="PostgresStorageMappedFeatureReader"/> (honua-server#1938).
/// Before the fix the storage-mapped reader threw <see cref="NotSupportedException"/> for
/// <c>f=fgb</c> and lacked <see cref="IGeobufFeatureStore"/> entirely, so the FeatureServer
/// emitted a 400 for every compat-seeded layer. These tests run the encoders against a real
/// PostGIS table to prove both formats now produce valid payloads over the storage mapping.
/// </summary>
[Collection("Database")]
public sealed class PostgresStorageMappedFeatureReaderEncodedFormatsIntegrationTests : IAsyncLifetime
{
    private static readonly ObjectPool<Dictionary<string, object?>> DictionaryPool =
        new DefaultObjectPoolProvider().Create(
            new Honua.Core.Features.Infrastructure.ServiceRegistration.DictionaryPooledObjectPolicy());

    private readonly PostgresFixture _fixture;
    private string _schema = null!;

    public PostgresStorageMappedFeatureReaderEncodedFormatsIntegrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _schema = await _fixture.CreateIsolatedSchemaAsync(nameof(PostgresStorageMappedFeatureReaderEncodedFormatsIntegrationTests));

        // A source-backed table is the standard provider-routed shape: an arbitrary physical
        // table with a primary key, a geometry column, and column-per-field attributes (no
        // shared 'layer_id'/'attributes' jsonb columns). The reader maps reads onto it.
        await _fixture.ExecuteAsync($"""
            CREATE TABLE {_schema}.cities (
                id bigint PRIMARY KEY,
                geom geometry(Point, 4326),
                name text,
                population integer
            );

            INSERT INTO {_schema}.cities (id, geom, name, population) VALUES
                (1, ST_SetSRID(ST_MakePoint(-157.8583, 21.3069), 4326), 'Honolulu', 350000),
                (2, ST_SetSRID(ST_MakePoint(-156.3319, 20.7984), 4326), 'Kahului', 26000);
            """);
    }

    public Task DisposeAsync() => _fixture.DropSchemaAsync(_schema);

    [Fact]
    public async Task QueryFlatGeobufAsync_SourceBackedLayer_ReturnsFlatGeobufPayload()
    {
        var reader = CreateReader();

        reader.Should().BeAssignableTo<IFlatGeobufFeatureStore>(
            "the source-backed reader must advertise the FlatGeobuf marker so the FeatureServer gates f=fgb on the underlying table support");

        var payload = await reader.QueryFlatGeobufAsync(layerId: 1, new FeatureQuery(), CancellationToken.None);

        payload.Should().NotBeNull();
        payload!.Length.Should().BeGreaterThan(0);
        // FlatGeobuf magic bytes begin with ASCII "fgb" (0x66 0x67 0x62).
        payload[0].Should().Be(0x66);
        payload[1].Should().Be(0x67);
        payload[2].Should().Be(0x62);
    }

    [Fact]
    public async Task QueryGeobufAsync_SourceBackedLayer_ReturnsGeobufPayload()
    {
        var reader = CreateReader();

        var geobufStore = reader.Should().BeAssignableTo<IGeobufFeatureStore>().Subject;

        var payload = await geobufStore.QueryGeobufAsync(layerId: 1, new FeatureQuery(), CancellationToken.None);

        payload.Should().NotBeNull();
        payload!.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task QueryFlatGeobufAsync_NoMatchingRows_ReturnsNull()
    {
        var reader = CreateReader();

        var payload = await reader.QueryFlatGeobufAsync(
            layerId: 1,
            new FeatureQuery { Where = "population > 100000000" },
            CancellationToken.None);

        payload.Should().BeNull();
    }

    [Fact]
    public async Task GetMvtTileAsync_SourceBackedLayer_UsesMappedTable()
    {
        var tileProvider = CreateReader().Should().BeAssignableTo<ITileProvider>().Subject;

        var payload = await tileProvider.GetMvtTileAsync(
            layerId: 1,
            x: 0,
            y: 0,
            z: 0,
            query: new FeatureQuery
            {
                SpatialReferenceSrid = 4326,
                OutputSrid = 4326
            },
            tileOptions: new TileOptions { TileBuffer = 0, TileExtent = 4096 },
            tileLimits: new TileLimits { MaxFeaturesPerTile = 100 },
            cancellationToken: CancellationToken.None);

        payload.Should().NotBeNull();
        payload!.Should().NotBeEmpty();
    }

    private PostgresStorageMappedFeatureReader CreateReader()
    {
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-cities", Name = "Cities" },
            Type = MetadataV2ResourceType.FeatureDataset,
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = MetadataV2GeometryType.Point,
                PrimaryGeometryField = "geom",
            },
            SchemaFields =
            [
                new MetadataV2Field { Name = "id", Type = MetadataV2FieldType.BigInteger, Nullable = false, SemanticRoles = ["id.primary"] },
                new MetadataV2Field { Name = "geom", Type = MetadataV2FieldType.Geometry, Nullable = true, SemanticRoles = ["geometry.primary"] },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String },
                new MetadataV2Field { Name = "population", Type = MetadataV2FieldType.Integer },
            ],
        };

        var mapping = new FeatureStorageMapping(
            TableName: "cities",
            SchemaName: _schema,
            PrimaryKeyColumn: "id",
            GeometryColumn: "geom",
            StorageSrid: 4326);

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

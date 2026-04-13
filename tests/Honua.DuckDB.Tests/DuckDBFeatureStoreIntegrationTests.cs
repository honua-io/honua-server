// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using DuckDB.NET.Data;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.DuckDB.Features.FeatureStore;
using Honua.DuckDB.Features.FeatureStore.Services;
using Honua.DuckDB.Features.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.DuckDB.Tests;

/// <summary>
/// Integration tests that exercise the full DuckDB provider stack
/// using a temp-file database with seeded spatial data.
/// </summary>
public class DuckDBFeatureStoreIntegrationTests : IAsyncLifetime
{
    private string _dbPath = null!;
    private DuckDBFeatureStore _store = null!;
    private DuckDBLayerRegistry _registry = null!;
    private string _connectionString = null!;
    private const int LayerId = 0;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"honua_test_{Guid.NewGuid():N}.duckdb");
        _connectionString = $"Data Source={_dbPath}";

        // Seed the database
        await using var seedConnection = new DuckDBConnection(_connectionString);
        await seedConnection.OpenAsync();

        await ExecuteAsync(seedConnection, "INSTALL spatial");
        await ExecuteAsync(seedConnection, "LOAD spatial");

        await ExecuteAsync(seedConnection, """
            CREATE TABLE parcels (
                id BIGINT PRIMARY KEY,
                geom GEOMETRY,
                name VARCHAR,
                area DOUBLE,
                type VARCHAR
            )
            """);

        for (var i = 1; i <= 10; i++)
        {
            var lon = -122.0 + i * 0.01;
            var lat = 37.0 + i * 0.01;
            await ExecuteAsync(seedConnection,
                $"INSERT INTO parcels VALUES ({i}, ST_Point({lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}), 'Parcel {i}', {(i * 100.5).ToString(System.Globalization.CultureInfo.InvariantCulture)}, '{(i % 2 == 0 ? "residential" : "commercial")}')");
        }

        var mapping = new DuckDBLayerMapping
        {
            LayerId = LayerId,
            TableName = "parcels",
            GeometryColumn = "geom",
            ObjectIdColumn = "id",
            Srid = 4326,
            AttributeColumns = ["name", "area", "type"]
        };

        _registry = new DuckDBLayerRegistry([mapping]);
        var connectionProvider = new FileDuckDBConnectionProvider(_connectionString);
        var queryBuilder = new DuckDBFeatureQueryBuilder(_registry);
        var dataAccess = new DuckDBFeatureDataAccess(
            connectionProvider,
            _registry,
            null,
            NullLogger<DuckDBFeatureDataAccess>.Instance);
        var cacheManager = new DuckDBFeatureCacheManager(_registry);

        _store = new DuckDBFeatureStore(queryBuilder, dataAccess, cacheManager);
    }

    public Task DisposeAsync()
    {
        try { File.Delete(_dbPath); } catch { /* best effort cleanup */ }
        try { File.Delete(_dbPath + ".wal"); } catch { /* best effort cleanup */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task QueryAsync_ReturnsAllFeatures()
    {
        var result = await _store.QueryAsync(LayerId, new FeatureQuery());

        Assert.Equal(10, result.TotalCount);
        Assert.Equal(10, result.Items.Length);
    }

    [Fact]
    public async Task QueryAsync_WithLimit_RespectsLimit()
    {
        var query = new FeatureQuery { Limit = 3 };

        var result = await _store.QueryAsync(LayerId, query);

        Assert.Equal(3, result.Items.Length);
        Assert.True(result.HasMoreResults);
    }

    [Fact]
    public async Task QueryAsync_WithObjectIds_FiltersCorrectly()
    {
        var query = new FeatureQuery
        {
            ObjectIds = ImmutableArray.Create(1L, 3L, 5L)
        };

        var result = await _store.QueryAsync(LayerId, query);

        Assert.Equal(3, result.Items.Length);
        Assert.All(result.Items, f => Assert.Contains(f.Id, new long[] { 1, 3, 5 }));
    }

    [Fact]
    public async Task CountAsync_ReturnsCorrectCount()
    {
        var count = await _store.CountAsync(LayerId, new FeatureQuery());

        Assert.Equal(10, count);
    }

    [Fact]
    public async Task GetExtentAsync_ReturnsValidExtent()
    {
        var extent = await _store.GetExtentAsync(LayerId);

        Assert.NotNull(extent);
        Assert.True(extent.Value.MinX < extent.Value.MaxX);
        Assert.True(extent.Value.MinY < extent.Value.MaxY);
    }

    [Fact]
    public async Task GetAsync_ExistingFeature_ReturnsFeature()
    {
        var feature = await _store.GetAsync(LayerId, 1);

        Assert.NotNull(feature);
        Assert.Equal(1, feature.Value.Id);
        Assert.NotNull(feature.Value.Geometry);
        Assert.Equal("Parcel 1", feature.Value.Attributes["name"]);
    }

    [Fact]
    public async Task GetAsync_NonExistentFeature_ReturnsNull()
    {
        var feature = await _store.GetAsync(LayerId, 999);

        Assert.Null(feature);
    }

    [Fact]
    public async Task QueryObjectIdsAsync_ReturnsAllIds()
    {
        var ids = await _store.QueryObjectIdsAsync(LayerId, new FeatureQuery());

        Assert.Equal(10, ids.Length);
        Assert.Contains(1L, ids);
        Assert.Contains(10L, ids);
    }

    [Fact]
    public async Task QueryStatisticsAsync_ComputesAggregates()
    {
        var query = new FeatureQuery
        {
            OutStatistics = ImmutableArray.Create(
                new StatisticDefinition
                {
                    StatisticType = StatisticType.Sum,
                    OnStatisticField = "area",
                    OutStatisticFieldName = "total_area"
                },
                new StatisticDefinition
                {
                    StatisticType = StatisticType.Avg,
                    OnStatisticField = "area",
                    OutStatisticFieldName = "avg_area"
                }),
            GroupByFields = ImmutableArray.Create("type")
        };

        var result = await _store.QueryStatisticsAsync(LayerId, query);

        Assert.Equal(2, result.Length); // residential + commercial
        Assert.All(result, row =>
        {
            Assert.True(row.ContainsKey("total_area"));
            Assert.True(row.ContainsKey("avg_area"));
            Assert.True(row.ContainsKey("type"));
        });
    }

    [Fact]
    public async Task QueryGeoJsonAsync_ReturnsGeoJsonGeometries()
    {
        var result = await _store.QueryGeoJsonAsync(LayerId, new FeatureQuery());

        Assert.Equal(10, result.Items.Length);
        Assert.All(result.Items, f =>
        {
            Assert.NotNull(f.GeometryGeoJson);
            Assert.Contains("Point", f.GeometryGeoJson);
        });
    }

    [Fact]
    public async Task StreamFeaturesAsync_StreamsAllFeatures()
    {
        var count = 0;
        await foreach (var _ in _store.StreamFeaturesAsync(LayerId, new FeatureQuery()))
        {
            count++;
        }

        Assert.Equal(10, count);
    }

    [Fact]
    public async Task QueryFlatGeobufAsync_ReturnsNull()
    {
        var result = await _store.QueryFlatGeobufAsync(LayerId, new FeatureQuery());

        Assert.Null(result);
    }

    [Fact]
    public async Task GetEstimatesAsync_ReturnsCountAndExtent()
    {
        var estimates = await _store.GetEstimatesAsync(LayerId);

        Assert.Equal(10, estimates.EstimatedCount);
        Assert.NotNull(estimates.Extent);
    }

    private static async Task ExecuteAsync(DuckDBConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Opens a fresh DuckDB connection to the temp-file database for each call,
    /// ensuring the spatial extension is loaded on each connection.
    /// </summary>
    private sealed class FileDuckDBConnectionProvider : Core.Features.Infrastructure.Abstractions.IDatabaseConnectionProvider
    {
        private readonly string _connectionString;

        public FileDuckDBConnectionProvider(string connectionString)
        {
            _connectionString = connectionString;
        }

        public string GetConnectionString() => _connectionString;

        public async Task<System.Data.Common.DbConnection> OpenConnectionAsync(CancellationToken ct = default)
        {
            var conn = new DuckDBConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "LOAD spatial";
            await cmd.ExecuteNonQueryAsync(ct);
            return conn;
        }

        public Task<(System.Data.Common.DbConnection, System.Data.Common.DbTransaction)> OpenTransactionAsync(
            System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.RepeatableRead,
            CancellationToken ct = default)
        {
            throw new NotSupportedException("Transactions not supported in test provider.");
        }

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken ct = default)
            => operation();

        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken ct = default)
            => operation();
    }
}

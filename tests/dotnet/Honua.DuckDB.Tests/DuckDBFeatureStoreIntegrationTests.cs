// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using DuckDB.NET.Data;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.DuckDB.Features.FeatureStore;
using Honua.DuckDB.Features.FeatureStore.Services;
using Honua.DuckDB.Features.Infrastructure;
using Honua.DuckDB.Queries.Filters;
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
    private DuckDBSpatialBootstrap _spatialBootstrap = null!;
    private DuckDbSqlFilterTranslator _filterTranslator = null!;
    private MetadataV2Resource _resource = null!;
    private const int LayerId = 0;

    public async Task InitializeAsync()
    {
        // Second argument is always a generated relative filename (hex GUID + extension), never rooted,
        // so Path.Combine cannot silently drop the temp-path prefix here.
        _dbPath = Path.Combine(Path.GetTempPath(), $"honua_test_{Guid.NewGuid():N}.duckdb");
        _connectionString = $"Data Source={_dbPath}";

        // Seed the database
        await using var seedConnection = new DuckDBConnection(_connectionString);
        await seedConnection.OpenAsync();

        _spatialBootstrap = new DuckDBSpatialBootstrap(
            extensionPath: null,
            logger: NullLogger<DuckDBSpatialBootstrap>.Instance);
        await _spatialBootstrap.EnsureSpatialExtensionAsync(seedConnection, CancellationToken.None);

        await ExecuteAsync(seedConnection, """
            CREATE TABLE parcels (
                id BIGINT PRIMARY KEY,
                geom GEOMETRY,
                name VARCHAR,
                area DOUBLE,
                type VARCHAR,
                start_time TIMESTAMPTZ,
                end_time TIMESTAMPTZ
            )
            """);

        for (var i = 1; i <= 10; i++)
        {
            var lon = -122.0 + i * 0.01;
            var lat = 37.0 + i * 0.01;
            var area = i * 100.5;
            var landUse = i % 2 == 0 ? "residential" : "commercial";
            // start_time spans 2024-01-{i} (one day per parcel); end_time
            // overlaps the next parcel so the interval-intersection path
            // can be exercised. Parcel 5 has a NULL end_time so the
            // COALESCE fallback in the temporal filter is also covered.
            var startDay = i.ToString("D2", CultureInfo.InvariantCulture);
            var endDay = (i + 2).ToString("D2", CultureInfo.InvariantCulture);
            var endLiteral = i == 5
                ? "NULL"
                : FormattableString.Invariant($"TIMESTAMPTZ '2024-01-{endDay} 12:00:00+00'");
            await ExecuteAsync(seedConnection, FormattableString.Invariant(
                $"INSERT INTO parcels VALUES ({i}, ST_Point({lon}, {lat}), 'Parcel {i}', {area}, '{landUse}', TIMESTAMPTZ '2024-01-{startDay} 00:00:00+00', {endLiteral})"));
        }

        var mapping = new DuckDBLayerMapping
        {
            LayerId = LayerId,
            TableName = "parcels",
            GeometryColumn = "geom",
            ObjectIdColumn = "id",
            Srid = 4326,
            AttributeColumns = ["name", "area", "type", "start_time", "end_time"]
        };

        _registry = new DuckDBLayerRegistry([mapping]);
        var connectionProvider = new FileDuckDBConnectionProvider(_connectionString, _spatialBootstrap);
        var queryBuilder = new DuckDBFeatureQueryBuilder(_registry);
        var dataAccess = new DuckDBFeatureDataAccess(
            connectionProvider,
            _registry,
            null,
            NullLogger<DuckDBFeatureDataAccess>.Instance);
        var cacheManager = new DuckDBFeatureCacheManager(_registry);

        _store = new DuckDBFeatureStore(queryBuilder, dataAccess, cacheManager);
        _filterTranslator = new DuckDbSqlFilterTranslator();
        _resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "duckdb.parcels", Name = "parcels" },
            Type = MetadataV2ResourceType.FeatureDataset,
            Spatial = new MetadataV2ResourceSpatial
            {
                GeometryType = MetadataV2GeometryType.Point,
                PrimaryGeometryField = "geom",
                SpatialReference = MetadataV2SpatialReference.Wgs84
            },
            SchemaFields =
            [
                new MetadataV2Field
                {
                    Name = "id",
                    Type = MetadataV2FieldType.BigInteger,
                    Nullable = false,
                    SemanticRoles = ["id.primary"]
                },
                new MetadataV2Field { Name = "geom", Type = MetadataV2FieldType.Geometry, Nullable = false },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String },
                new MetadataV2Field { Name = "area", Type = MetadataV2FieldType.Double },
                new MetadataV2Field { Name = "type", Type = MetadataV2FieldType.String }
            ]
        };
    }

    public Task DisposeAsync()
    {
        _spatialBootstrap?.Dispose();

        // Intentional catch-all: best-effort deletion of the per-test scratch DuckDB
        // files; a failed cleanup (e.g. the file is still locked) must not fail teardown.
        try { File.Delete(_dbPath); } catch { }

        try { File.Delete(_dbPath + ".wal"); } catch { }
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
    public async Task QueryAsync_WithCql2Intersects_ReturnsCorrectFeatureSet()
    {
        var polygon = GeometryLiteral(CreatePolygonWkb(-121.972, 37.028, -121.948, 37.052), 4326);
        var filter = new SpatialPredicate(
            SpatialOperator.Intersects,
            new PropertyReference("geom"),
            polygon);
        var ids = await QueryIdsThroughInterfaceAsync(_store, QueryWithFilter(filter));

        Assert.Equal([3L, 4L, 5L], ids);

        // Exercise positional rebasing: the spatial WKB is $1 and the later object-id
        // predicate must become $2 rather than aliasing the geometry parameter.
        var narrowed = await QueryIdsThroughInterfaceAsync(_store, QueryWithFilter(filter) with
        {
            ObjectIds = ImmutableArray.Create(4L, 8L)
        });
        Assert.Equal([4L], narrowed);
    }

    [Fact]
    public async Task QueryAsync_WithCql2WithinAndContains_PreservesAsymmetricOperandOrder()
    {
        var polygon = GeometryLiteral(CreatePolygonWkb(-121.972, 37.028, -121.948, 37.052), 4326);
        var within = await QueryIdsThroughInterfaceAsync(_store, QueryWithFilter(new SpatialPredicate(
            SpatialOperator.Within,
            new PropertyReference("geom"),
            polygon)));
        var containsWithReversedOperands = await QueryIdsThroughInterfaceAsync(_store, QueryWithFilter(new SpatialPredicate(
            SpatialOperator.Contains,
            polygon,
            new PropertyReference("geom"))));

        var expected = new long[] { 3, 4, 5 };
        Assert.Equal(expected, within);
        Assert.Equal(expected, containsWithReversedOperands);
    }

    [Fact]
    public async Task QueryAsync_WithCql2DWithin_UsesMetersAndAlwaysXyAxisOrder()
    {
        var filter = new SpatialDistancePredicate(
            SpatialOperator.DWithin,
            new PropertyReference("geom"),
            GeometryLiteral(CreatePointWkb(-121.95, 37.05), 4326),
            new Literal(1_600d, LiteralType.Number));
        var ids = await QueryIdsThroughInterfaceAsync(_store, QueryWithFilter(filter));

        Assert.Equal([4L, 5L, 6L], ids);
    }

    [Fact]
    public async Task QueryAsync_WithCombinedAttributeAndSpatialFragments_PreservesParameterBindings()
    {
        var attribute = _filterTranslator.Translate(
            new BinaryExpression(
                new PropertyReference("area"),
                BinaryOperator.GreaterThan,
                new Literal(0d, LiteralType.Number)),
            _resource);
        var spatial = _filterTranslator.Translate(
            new SpatialPredicate(
                SpatialOperator.Intersects,
                new PropertyReference("geom"),
                GeometryLiteral(CreatePolygonWkb(-121.972, 37.028, -121.948, 37.052), 4326)),
            _resource);
        var combined = SqlFragmentHelpers.CombineSqlFilters(attribute, spatial);

        var ids = await QueryIdsThroughInterfaceAsync(_store, new FeatureQuery { SqlFilter = combined });

        Assert.Equal([3L, 4L, 5L], ids);
    }

    [Fact]
    public void Translate_CrossSridGeometryLiteral_RejectsWithAxisGuidance()
    {
        var filter = new SpatialPredicate(
            SpatialOperator.Intersects,
            new PropertyReference("geom"),
            GeometryLiteral(CreatePointWkb(-121.95, 37.05), 3857));

        var exception = Assert.Throws<NotSupportedException>(() => _filterTranslator.Translate(filter, _resource));

        Assert.Contains("Cross-SRID", exception.Message, StringComparison.Ordinal);
        Assert.Contains("always_xy", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_GeographicDistanceOnPolygonResource_RejectsPointOnlyFunction()
    {
        var polygonResource = _resource with
        {
            Spatial = _resource.Spatial! with { GeometryType = MetadataV2GeometryType.Polygon }
        };
        var filter = new SpatialDistancePredicate(
            SpatialOperator.DWithin,
            new PropertyReference("geom"),
            GeometryLiteral(CreatePointWkb(-121.95, 37.05), 4326),
            new Literal(1_600d, LiteralType.Number));

        var exception = Assert.Throws<NotSupportedException>(
            () => _filterTranslator.Translate(filter, polygonResource));

        Assert.Contains("point geometries only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_GeographicDistanceOnMultiPointResource_RejectsPointOnlyFunction()
    {
        var multiPointResource = _resource with
        {
            Spatial = _resource.Spatial! with { GeometryType = MetadataV2GeometryType.MultiPoint }
        };
        var filter = new SpatialDistancePredicate(
            SpatialOperator.DWithin,
            new PropertyReference("geom"),
            GeometryLiteral(CreatePointWkb(-121.95, 37.05), 4326),
            new Literal(1_600d, LiteralType.Number));

        var exception = Assert.Throws<NotSupportedException>(
            () => _filterTranslator.Translate(filter, multiPointResource));

        Assert.Contains("point geometries only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Translate_GeographicDistanceWithPolygonLiteral_RejectsPointOnlyFunction()
    {
        var filter = new SpatialDistancePredicate(
            SpatialOperator.DWithin,
            new PropertyReference("geom"),
            GeometryLiteral(CreatePolygonWkb(-121.96, 37.04, -121.94, 37.06), 4326),
            new Literal(1_600d, LiteralType.Number));

        var exception = Assert.Throws<NotSupportedException>(
            () => _filterTranslator.Translate(filter, _resource));

        Assert.Contains("point geometries only", exception.Message, StringComparison.Ordinal);
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
        var extentValue = extent!.Value;
        Assert.True(extentValue.MinX < extentValue.MaxX);
        Assert.True(extentValue.MinY < extentValue.MaxY);
    }

    [Fact]
    public async Task GetAsync_ExistingFeature_ReturnsFeature()
    {
        var feature = await _store.GetAsync(LayerId, 1);

        Assert.NotNull(feature);
        var featureValue = feature!.Value;
        Assert.Equal(1, featureValue.Id);
        Assert.NotNull(featureValue.Geometry);
        Assert.Equal("Parcel 1", featureValue.Attributes["name"]);
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

    [Fact]
    public async Task QueryAsync_WithTemporalFilterInstantOnly_FiltersCorrectly()
    {
        // Instant-only mode (no EndPropertyName): the predicate is
        // start_time >= queryStart AND start_time <= queryEnd.
        // start_time spans 2024-01-01..2024-01-10 (one per parcel).
        // Window 2024-01-03..2024-01-05 should match parcels 3, 4, 5 (#379).
        var query = new FeatureQuery
        {
            TemporalFilter = new TemporalFilter
            {
                PropertyName = "start_time",
                PropertyType = TemporalPropertyType.DateTime,
                Start = new DateTimeOffset(2024, 1, 3, 0, 0, 0, TimeSpan.Zero),
                End = new DateTimeOffset(2024, 1, 5, 0, 0, 0, TimeSpan.Zero)
            }
        };

        var result = await _store.QueryAsync(LayerId, query);

        Assert.Equal(3, result.Items.Length);
        Assert.All(result.Items, f => Assert.Contains(f.Id, new long[] { 3, 4, 5 }));
    }

    [Fact]
    public async Task QueryAsync_WithTemporalFilterIntervalIntersection_FiltersCorrectly()
    {
        // Interval mode (EndPropertyName=end_time): the predicate is
        // COALESCE(end_time, start_time) >= queryStart AND start_time <= queryEnd.
        // Window 2024-01-08..2024-01-08 (single instant): parcels whose
        // [start, end] interval intersects 2024-01-08:
        // - P1: [01-01, 01-03] → no
        // - P2: [01-02, 01-04] → no
        // - P3: [01-03, 01-05] → no
        // - P4: [01-04, 01-06] → no
        // - P5: [01-05, NULL→01-05] → no (collapsed to instant)
        // - P6: [01-06, 01-08] → yes (ends exactly at 01-08)
        // - P7: [01-07, 01-09] → yes
        // - P8: [01-08, 01-10] → yes (starts exactly at 01-08)
        // - P9: [01-09, 01-11] → no
        // - P10: [01-10, 01-12] → no
        var query = new FeatureQuery
        {
            TemporalFilter = new TemporalFilter
            {
                PropertyName = "start_time",
                PropertyType = TemporalPropertyType.DateTime,
                EndPropertyName = "end_time",
                Start = new DateTimeOffset(2024, 1, 8, 0, 0, 0, TimeSpan.Zero),
                End = new DateTimeOffset(2024, 1, 8, 0, 0, 0, TimeSpan.Zero)
            }
        };

        var result = await _store.QueryAsync(LayerId, query);

        Assert.Equal(3, result.Items.Length);
        Assert.All(result.Items, f => Assert.Contains(f.Id, new long[] { 6, 7, 8 }));
    }

    [Fact]
    public async Task QueryAsync_WithTemporalFilterCoalescingNullEnd_TreatsRowAsInstant()
    {
        // Parcel 5 has end_time = NULL. With the COALESCE(end_time, start_time)
        // path, the row's effective interval collapses to start_time only,
        // so a window covering 2024-01-05 should match parcel 5 even though
        // end_time is null. Window 2024-01-05..2024-01-05.
        var query = new FeatureQuery
        {
            TemporalFilter = new TemporalFilter
            {
                PropertyName = "start_time",
                PropertyType = TemporalPropertyType.DateTime,
                EndPropertyName = "end_time",
                Start = new DateTimeOffset(2024, 1, 5, 0, 0, 0, TimeSpan.Zero),
                End = new DateTimeOffset(2024, 1, 5, 0, 0, 0, TimeSpan.Zero)
            }
        };

        var result = await _store.QueryAsync(LayerId, query);

        Assert.Contains(result.Items, f => f.Id == 5);
    }

    private static async Task ExecuteAsync(DuckDBConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private FeatureQuery QueryWithFilter(FilterExpression filter)
        => new() { SqlFilter = _filterTranslator.Translate(filter, _resource) };

#pragma warning disable CA1859 // Deliberately exercise the provider through its public interface.
    private static async Task<long[]> QueryIdsThroughInterfaceAsync(
        IFeatureReader reader,
        FeatureQuery query)
    {
        var result = await reader.QueryAsync(LayerId, query);
        return result.Items.Select(feature => feature.Id).Order().ToArray();
    }
#pragma warning restore CA1859

    private static GeometryLiteral GeometryLiteral(byte[] wkb, int srid)
        => new(wkb, srid, "test-wkb");

    private static byte[] CreatePointWkb(double x, double y)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1); // little-endian
        writer.Write(1u); // Point
        writer.Write(x);
        writer.Write(y);
        return stream.ToArray();
    }

    private static byte[] CreatePolygonWkb(double minX, double minY, double maxX, double maxY)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1); // little-endian
        writer.Write(3u); // Polygon
        writer.Write(1u); // one ring
        writer.Write(5u); // closed shell
        (double X, double Y)[] coordinates =
        [
            (minX, minY),
            (maxX, minY),
            (maxX, maxY),
            (minX, maxY),
            (minX, minY)
        ];
        foreach (var coordinate in coordinates)
        {
            writer.Write(coordinate.X);
            writer.Write(coordinate.Y);
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Opens a fresh DuckDB connection to the temp-file database for each call,
    /// ensuring the spatial extension is loaded on each connection.
    /// </summary>
    private sealed class FileDuckDBConnectionProvider : Core.Features.Infrastructure.Abstractions.IAdoNetDatabaseConnectionProvider
    {
        private readonly string _connectionString;
        private readonly DuckDBSpatialBootstrap _spatialBootstrap;

        public FileDuckDBConnectionProvider(string connectionString, DuckDBSpatialBootstrap spatialBootstrap)
        {
            _connectionString = connectionString;
            _spatialBootstrap = spatialBootstrap;
        }

        public string GetConnectionString() => _connectionString;

        public async Task<System.Data.Common.DbConnection> OpenConnectionAsync(CancellationToken ct = default)
        {
            var conn = new DuckDBConnection(_connectionString);
            await conn.OpenAsync(ct);
            await _spatialBootstrap.EnsureSpatialExtensionAsync(conn, ct);
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

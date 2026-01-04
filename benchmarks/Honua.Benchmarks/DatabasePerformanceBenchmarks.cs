// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.FeatureStore;
using Microsoft.Extensions.ObjectPool;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Npgsql;

namespace Honua.Benchmarks;

/// <summary>
/// Comprehensive database performance benchmarks covering spatial queries, connection management,
/// transaction performance, and bulk operations.
///
/// Performance targets for enterprise-scale geospatial workloads:
/// - Simple spatial queries: &lt;50ms p95
/// - Complex spatial operations: &lt;200ms p95
/// - Bulk inserts: &gt;1000 features/second
/// - Connection pool efficiency: &gt;95% utilization under load
/// - Transaction throughput: &gt;500 ops/second
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net90)]
public class DatabasePerformanceBenchmarks
{
    private const int DefaultFeatureCount = 50000;
    private const int LayerId = 1;
    private const int Srid = 4326;
    private const int BulkInsertBatchSize = 1000;

    private NpgsqlDataSource _dataSource = null!;
    private PostgresFeatureStore _featureStore = null!;
    private string _schemaName = string.Empty;

    // Test geometries for different spatial operations
    private byte[] _pointGeometry = null!;
    private byte[] _smallBboxGeometry = null!;
    private byte[] _largeBboxGeometry = null!;
    private byte[] _complexPolygonGeometry = null!;
    private byte[] _multiPolygonGeometry = null!;

    // Pre-built queries for different scenarios
    private FeatureQuery _simpleAttributeQuery = null!;
    private FeatureQuery _spatialIntersectsQuery = null!;
    private FeatureQuery _spatialWithinQuery = null!;
    private FeatureQuery _spatialContainsQuery = null!;
    private FeatureQuery _complexSpatialQuery = null!;
    private FeatureQuery _combinedAttributeSpatialQuery = null!;
    private FeatureQuery _nearbyQuery = null!;
    private FeatureQuery _largePaginatedQuery = null!;

    // Connection pool stress testing
    private readonly List&lt;Task&lt;DbConnection&gt;&gt; _connectionTasks = new ();

    [Params(1, 10, 50, 100)]
    public int ConcurrentConnections { get; set; }

    [Params(100, 500, 1000, 2000)]
    public int BulkOperationSize { get; set; }

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        var connectionString = ResolveConnectionString();

        // Optimized connection string for benchmarking
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MinPoolSize = 5,
            MaxPoolSize = 100,
            ConnectionIdleLifetime = 300,
            ConnectionPruningInterval = 10,
            Pooling = true,
            Multiplexing = true,
            NoResetOnClose = true,
            ReadBufferSize = 16384,
            WriteBufferSize = 16384,
            TcpKeepAlive = true,
            TcpKeepAliveTime = 30,
            TcpKeepAliveInterval = 2
        };

        _dataSource = NpgsqlDataSource.Create(builder.ToString());
        _schemaName = $"bench_db_{Guid.NewGuid():N}";

        await using var connection = await _dataSource.OpenConnectionAsync();
        await EnsurePostgisAsync(connection);
        await CreateSchemaAsync(connection, _schemaName);
        await CreateOptimizedFeatureTableAsync(connection, _schemaName);
        await SeedDiverseGeospatialDataAsync(connection, _schemaName, DefaultFeatureCount);
        await CreateSpatialIndexesAsync(connection, _schemaName);
        await AnalyzeTableAsync(connection, _schemaName);

        // Warm up connection pool
        await WarmupConnectionPoolAsync();

        var pool = new DefaultObjectPoolProvider().CreateStringBuilderPool();
        var connectionProvider = new BenchmarkConnectionProvider(_dataSource);
        _featureStore = new PostgresFeatureStore(connectionProvider, pool, _schemaName);

        SetupTestGeometries();
        SetupTestQueries();
    }

    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        // Clean up any open connections from stress tests
        foreach (var task in _connectionTasks.Where(t = &gt;
        t.IsCompletedSuccessfully))
        {
            var connection = await task;
            await connection.DisposeAsync();
        }
        _connectionTasks.Clear();

        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS {_schemaName} CASCADE;";
        await command.ExecuteNonQueryAsync();
        await _dataSource.DisposeAsync();
    }

    #region Spatial Query Performance Benchmarks

    [Benchmark(Description = "Simple attribute filter query")]
    public Task&lt;QueryResult&lt;Feature&gt;&gt; private SimpleAttributeQuery()
        =&gt; _featureStore.QueryAsync(LayerId, _simpleAttributeQuery);

    [Benchmark(Description = "Point-in-polygon spatial intersects")]
    public Task&lt;QueryResult&lt;Feature&gt;&gt; private SpatialIntersectsQuery()
        =&gt; _featureStore.QueryAsync(LayerId, _spatialIntersectsQuery);

    [Benchmark(Description = "Polygon within larger polygon")]
    public Task&lt;QueryResult&lt;Feature&gt;&gt; private SpatialWithinQuery()
        =&gt; _featureStore.QueryAsync(LayerId, _spatialWithinQuery);

    [Benchmark(Description = "Complex polygon contains geometries")]
    public Task&lt;QueryResult&lt;Feature&gt;&gt; private SpatialContainsQuery()
        =&gt; _featureStore.QueryAsync(LayerId, _spatialContainsQuery);

    [Benchmark(Description = "Multi-polygon complex spatial query")]
    public Task&lt;QueryResult&lt;Feature&gt;&gt; private ComplexSpatialQuery()
        =&gt; _featureStore.QueryAsync(LayerId, _complexSpatialQuery);

    [Benchmark(Description = "Combined attribute and spatial filter")]
    public Task&lt;QueryResult&lt;Feature&gt;&gt; private CombinedAttributeSpatialQuery()
        =&gt; _featureStore.QueryAsync(LayerId, _combinedAttributeSpatialQuery);

    [Benchmark(Description = "Distance-based nearby query")]
    public Task&lt;QueryResult&lt;Feature&gt;&gt; private NearbyQuery()
        =&gt; _featureStore.QueryAsync(LayerId, _nearbyQuery);

    [Benchmark(Description = "Large paginated result set")]
    public Task&lt;QueryResult&lt;Feature&gt;&gt; private LargePaginatedQuery()
        =&gt; _featureStore.QueryAsync(LayerId, _largePaginatedQuery);

    #endregion

    #region Index Performance Benchmarks

    [Benchmark(Description = "Spatial index utilization test")]
    public async Task&lt;TimeSpan&gt; private SpatialIndexPerformanceTest()
    {
        var start = DateTime.UtcNow;

        // Execute multiple spatial queries that should hit spatial index
        var tasks = new List& lt;
        Task & lt;
        QueryResult & lt;
        Feature & gt;
        &gt;
        &gt;
        ();
        for (int i = 0; i & lt; 10; i++)
        {
            tasks.Add(_featureStore.QueryAsync(LayerId, _spatialIntersectsQuery));
        }

        await Task.WhenAll(tasks);
        return DateTime.UtcNow - start;
    }

    [Benchmark(Description = "B-tree index on attributes")]
    public async Task&lt;TimeSpan&gt; private AttributeIndexPerformanceTest()
    {
        var start = DateTime.UtcNow;

        // Execute multiple attribute queries that should hit B-tree indexes
        var tasks = new List& lt;
        Task & lt;
        QueryResult & lt;
        Feature & gt;
        &gt;
        &gt;
        ();
        for (int i = 0; i & lt; 20; i++)
        {
            tasks.Add(_featureStore.QueryAsync(LayerId, _simpleAttributeQuery));
        }

        await Task.WhenAll(tasks);
        return DateTime.UtcNow - start;
    }

    #endregion

    #region Connection Pool Performance Benchmarks

    [Benchmark(Description = "Connection pool stress test")]
    public async Task&lt;int&gt; private ConnectionPoolStressTest()
    {
        _connectionTasks.Clear();
        var successCount = 0;

        // Create concurrent connection requests
        for (int i = 0; i & lt; ConcurrentConnections; i++)
        {
            _connectionTasks.Add(OpenAndTestConnectionAsync());
        }

        var results = await Task.WhenAll(_connectionTasks);
        successCount = results.Count(conn = &gt;
        conn != null);

        return successCount;
    }

    [Benchmark(Description = "Connection pool warmup efficiency")]
    public async Task&lt;TimeSpan&gt; private ConnectionPoolWarmupTest()
    {
        var start = DateTime.UtcNow;
        await WarmupConnectionPoolAsync();
        return DateTime.UtcNow - start;
    }

    private async Task&lt;DbConnection?&gt; private OpenAndTestConnectionAsync()
    {
        try
        {
            var connection = await _dataSource.OpenConnectionAsync();

            // Quick validation query
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            await command.ExecuteScalarAsync();

            return connection;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region Transaction Performance Benchmarks

    [Benchmark(Description = "Bulk feature creation performance")]
    public async Task&lt;int&gt; private BulkFeatureCreationTest()
    {
        var features = GenerateTestFeatures(BulkOperationSize);
        var result = await _featureStore.ProcessCreatesWithResultsAsync(LayerId, features);
        return result.CreatedFeatures.Count();
    }

    [Benchmark(Description = "Bulk feature update performance")]
    public async Task&lt;int&gt; private BulkFeatureUpdateTest()
    {
        // First create features to update
        var originalFeatures = GenerateTestFeatures(Math.Min(BulkOperationSize, 100));
        var createResult = await _featureStore.ProcessCreatesWithResultsAsync(LayerId, originalFeatures);

        // Now update them
        var updates = createResult.CreatedFeatures.Select(f = &gt;
        new FeatureUpdate
        {
            ObjectId = f.ObjectId,
            Geometry = f.Geometry,
            Attributes = new Dictionary& lt; string,
            object ? &gt; (f.Attributes!)
            {
            ["updated_at"] = DateTime.UtcNow,
            ["update_count"] = 1
            }
        }).ToArray();

        var updateResult = await _featureStore.ProcessUpdatesAsync(LayerId, updates);
        return updateResult.UpdatedCount;
    }

    [Benchmark(Description = "Concurrent transaction throughput")]
    public async Task&lt;int&gt; private ConcurrentTransactionThroughputTest()
    {
        var tasks = new List& lt;
        Task & lt;
        int&gt;
        &gt;
        ();

        // Execute concurrent smaller bulk operations
        for (int i = 0; i & lt; ConcurrentConnections; i++)
        {
            tasks.Add(ExecuteSingleBulkOperationAsync(BulkOperationSize / ConcurrentConnections));
        }

        var results = await Task.WhenAll(tasks);
        return results.Sum();
    }

    private async Task&lt;int&gt; private ExecuteSingleBulkOperationAsync(int count)
    {
        var features = GenerateTestFeatures(count);
        var result = await _featureStore.ProcessCreatesWithResultsAsync(LayerId, features);
        return result.CreatedFeatures.Count();
    }

    #endregion

    #region Setup and Helper Methods

    private static string ResolveConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("HONUA_BENCH_DB_URL")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Connection string not configured. Set HONUA_BENCH_DB_URL or ConnectionStrings__DefaultConnection.");
        }

        return connectionString;
    }

    private static async Task EnsurePostgisAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE EXTENSION IF NOT EXISTS postgis;";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CreateSchemaAsync(NpgsqlConnection connection, string schemaName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE SCHEMA IF NOT EXISTS {schemaName};";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CreateOptimizedFeatureTableAsync(NpgsqlConnection connection, string schemaName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {schemaName}.features (
                objectid BIGSERIAL PRIMARY KEY,
                layer_id INT NOT NULL,
                geometry GEOMETRY(GEOMETRY, {Srid}),
                attributes JSONB NOT NULL DEFAULT '{{}}',
                category TEXT,
                priority INTEGER,
                area_sqm DECIMAL(15,3),
                created_at TIMESTAMPTZ DEFAULT NOW(),
                updated_at TIMESTAMPTZ DEFAULT NOW()
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CreateSpatialIndexesAsync(NpgsqlConnection connection, string schemaName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            -- Primary spatial index
            CREATE INDEX IF NOT EXISTS idx_{schemaName}_features_geom
                ON {schemaName}.features USING GIST (geometry);

            -- Layer ID index for common filtering
            CREATE INDEX IF NOT EXISTS idx_{schemaName}_features_layer_id
                ON {schemaName}.features (layer_id);

            -- Category index for attribute filtering
            CREATE INDEX IF NOT EXISTS idx_{schemaName}_features_category
                ON {schemaName}.features (category);

            -- Priority index for sorting
            CREATE INDEX IF NOT EXISTS idx_{schemaName}_features_priority
                ON {schemaName}.features (priority DESC);

            -- Composite index for common queries
            CREATE INDEX IF NOT EXISTS idx_{schemaName}_features_layer_category
                ON {schemaName}.features (layer_id, category);

            -- JSONB GIN index for attribute queries
            CREATE INDEX IF NOT EXISTS idx_{schemaName}_features_attributes
                ON {schemaName}.features USING GIN (attributes);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedDiverseGeospatialDataAsync(NpgsqlConnection connection, string schemaName, int featureCount)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {schemaName}.features (layer_id, geometry, attributes, category, priority, area_sqm)
            SELECT
                {LayerId},
                CASE
                    WHEN gs % 4 = 0 THEN
                        ST_SetSRID(ST_MakePoint(-158.0 + (random() * 2), 21.0 + (random() * 2)), {Srid})
                    WHEN gs % 4 = 1 THEN
                        ST_SetSRID(ST_Buffer(ST_MakePoint(-157.0 + (random() * 2), 21.5 + (random() * 2)), 0.01), {Srid})
                    WHEN gs % 4 = 2 THEN
                        ST_SetSRID(ST_MakePolygon(ST_MakeLine(ARRAY[
                            ST_MakePoint(-156.0 + random(), 20.0 + random()),
                            ST_MakePoint(-155.5 + random(), 20.0 + random()),
                            ST_MakePoint(-155.5 + random(), 20.5 + random()),
                            ST_MakePoint(-156.0 + random(), 20.5 + random()),
                            ST_MakePoint(-156.0 + random(), 20.0 + random())
                        ])), {Srid})
                    ELSE
                        ST_SetSRID(ST_MakeEnvelope(
                            -155.0 + random(), 19.0 + random(),
                            -154.5 + random(), 19.5 + random(), {Srid}), {Srid})
                END,
                jsonb_build_object(
                    'name', 'BenchFeature ' || gs,
                    'description', 'Generated feature for benchmarking',
                    'numeric_value', gs * random(),
                    'timestamp', NOW() - (random() * interval '365 days')
                ),
                CASE gs % 5
                    WHEN 0 THEN 'urban'
                    WHEN 1 THEN 'rural'
                    WHEN 2 THEN 'industrial'
                    WHEN 3 THEN 'recreational'
                    ELSE 'mixed'
                END,
                (gs % 10) + 1,
                ST_Area(ST_Transform(
                    CASE WHEN gs % 4 &lt;&gt; 0 THEN geometry ELSE ST_Buffer(geometry, 0.001) END,
                    3857)) -- Calculate area in square meters
            FROM generate_series(1, {featureCount}) AS gs;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AnalyzeTableAsync(NpgsqlConnection connection, string schemaName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            ANALYZE {schemaName}.features;
            -- Update table statistics for query planner
            SELECT pg_stat_reset_single_table_counters('{schemaName}.features'::regclass);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private async Task WarmupConnectionPoolAsync()
    {
        var warmupTasks = new List& lt;
        Task & gt;
        ();

        // Open minimum pool size connections simultaneously
        for (int i = 0; i & lt; 5; i++)
        {
            warmupTasks.Add(Task.Run(async() = &gt;
            {
                await using var connection = await _dataSource.OpenConnectionAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1;";
                await command.ExecuteScalarAsync();
            }));
    }

    await Task.WhenAll(warmupTasks);
    }

    private void SetupTestGeometries()
    {
        var factory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: Srid);
        var writer = new WKBWriter();

        // Point geometry for intersects tests
        _pointGeometry = writer.Write(factory.CreatePoint(new Coordinate(-157.5, 21.3)));

        // Small bounding box (1km x 1km approximate)
        _smallBboxGeometry = writer.Write(factory.ToGeometry(new Envelope(-157.8, -157.7, 21.2, 21.3)));

        // Large bounding box (100km x 100km approximate)
        _largeBboxGeometry = writer.Write(factory.ToGeometry(new Envelope(-158.5, -156.5, 20.5, 22.5)));

        // Complex polygon with holes
        var exterior = factory.CreateLinearRing(new[]
        {
            new Coordinate(-157.0, 21.0),
            new Coordinate(-156.0, 21.0),
            new Coordinate(-156.0, 22.0),
            new Coordinate(-157.0, 22.0),
            new Coordinate(-157.0, 21.0)
        });
        var hole = factory.CreateLinearRing(new[]
        {
            new Coordinate(-156.8, 21.2),
            new Coordinate(-156.2, 21.2),
            new Coordinate(-156.2, 21.8),
            new Coordinate(-156.8, 21.8),
            new Coordinate(-156.8, 21.2)
        });
        _complexPolygonGeometry = writer.Write(factory.CreatePolygon(exterior, new[] { hole }));

        // Multi-polygon geometry
        var poly1 = factory.ToGeometry(new Envelope(-157.5, -157.3, 21.1, 21.3));
        var poly2 = factory.ToGeometry(new Envelope(-157.2, -157.0, 21.4, 21.6));
        _multiPolygonGeometry = writer.Write(factory.CreateMultiPolygon(new[] { (Polygon)poly1, (Polygon)poly2 }));
    }

    private void SetupTestQueries()
    {
        _simpleAttributeQuery = new FeatureQuery
        {
            Where = "category = 'urban'",
            Limit = 100,
            SpatialReferenceSrid = Srid,
            OutputSrid = Srid
        };

        _spatialIntersectsQuery = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create(_pointGeometry, SpatialRelationship.Intersects, Srid),
            Limit = 50,
            SpatialReferenceSrid = Srid,
            OutputSrid = Srid
        };

        _spatialWithinQuery = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create(_largeBboxGeometry, SpatialRelationship.Within, Srid),
            Limit = 200,
            SpatialReferenceSrid = Srid,
            OutputSrid = Srid
        };

        _spatialContainsQuery = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create(_complexPolygonGeometry, SpatialRelationship.Contains, Srid),
            Limit = 150,
            SpatialReferenceSrid = Srid,
            OutputSrid = Srid
        };

        _complexSpatialQuery = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create(_multiPolygonGeometry, SpatialRelationship.Intersects, Srid),
            Limit = 300,
            SpatialReferenceSrid = Srid,
            OutputSrid = Srid
        };

        _combinedAttributeSpatialQuery = new FeatureQuery
        {
            Where = "category IN ('urban', 'industrial') AND priority &gt; 5",
            SpatialFilter = SpatialFilter.Create(_smallBboxGeometry, SpatialRelationship.Intersects, Srid),
            Limit = 75,
            SpatialReferenceSrid = Srid,
            OutputSrid = Srid
        };

        _nearbyQuery = new FeatureQuery
        {
            Where = "ST_DWithin(geometry, ST_GeomFromText('POINT(-157.5 21.3)', 4326), 1000)",
            OrderBy = "ST_Distance(geometry, ST_GeomFromText('POINT(-157.5 21.3)', 4326))",
            Limit = 25,
            SpatialReferenceSrid = Srid,
            OutputSrid = Srid
        };

        _largePaginatedQuery = new FeatureQuery
        {
            Where = "priority &gt;= 1",
            OrderBy = "priority DESC, objectid",
            Limit = 1000,
            Offset = 5000,
            SpatialReferenceSrid = Srid,
            OutputSrid = Srid
        };
    }

    private static IEnumerable&lt;FeatureCreate&gt; private GenerateTestFeatures(int count)
{
    var factory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: Srid);
    var writer = new WKBWriter();
    var random = new Random();

    for (int i = 0; i & lt; count; i++)
    {
        var point = factory.CreatePoint(new Coordinate(
            -158.0 + (random.NextDouble() * 4),
            19.0 + (random.NextDouble() * 4)));

        yield return new FeatureCreate
        {
            Geometry = writer.Write(point),
            Attributes = new Dictionary& lt; string,
            object ? &gt;
            {
            ["name"] = $"BulkFeature_{i}",
            ["category"] = random.Next(0, 2) == 0 ? "urban" : "rural",
            ["priority"] = random.Next(1, 11),
            ["created_batch"] = DateTime.UtcNow,
            ["sequence"] = i
            }
        };
    }
}

#endregion

private sealed class BenchmarkConnectionProvider : IDatabaseConnectionProvider
{
    private readonly NpgsqlDataSource _dataSource;

    public BenchmarkConnectionProvider(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task&lt;DbConnection&gt; private OpenConnectionAsync(CancellationToken cancellationToken = default)
            =&gt; await _dataSource.OpenConnectionAsync(cancellationToken).private ConfigureAwait(false);
}
}

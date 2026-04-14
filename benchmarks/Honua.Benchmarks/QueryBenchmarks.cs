// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using BenchmarkDotNet.Attributes;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.FeatureStore;
using Honua.Postgres.Features.FeatureStore.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Npgsql;

namespace Honua.Benchmarks;

/// <summary>
/// End-to-end query benchmarks against the feature store using a seeded PostGIS dataset.
/// </summary>
[MemoryDiagnoser]
public class QueryBenchmarks
{
    private const int DefaultFeatureCount = 10000;
    private const int LayerId = 1;
    private const int Srid = 4326;

    private NpgsqlDataSource _dataSource = null!;
    private PostgresFeatureStoreRefactored _featureStore = null!;
    private string _schemaName = string.Empty;
    private SpatialFilter _spatialFilter;
    private FeatureQuery _simpleWhereQuery;
    private FeatureQuery _spatialQuery;
    private FeatureQuery _combinedQuery;
    private FeatureQuery _paginatedQuery;
    private FeatureQuery _largeResultSetQuery;

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        var connectionString = ResolveConnectionString();
        _dataSource = NpgsqlDataSource.Create(connectionString);
        _schemaName = $"bench_{Guid.NewGuid():N}";

        await using var connection = await _dataSource.OpenConnectionAsync();
        await EnsurePostgisAsync(connection);
        await CreateSchemaAsync(connection, _schemaName);
        await CreateFeatureTableAsync(connection, _schemaName);
        await SeedFeaturesAsync(connection, _schemaName, DefaultFeatureCount);
        await AnalyzeAsync(connection, _schemaName);

        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.CreateStringBuilderPool();
        var dictionaryPool = poolProvider.Create(new DictionaryPooledObjectPolicy());
        var connectionProvider = new BenchmarkConnectionProvider(_dataSource);
        var cacheLogger = NullLogger<FeatureCacheManager>.Instance;
        var cacheManager = new FeatureCacheManager(connectionProvider, cacheLogger, _schemaName);
        var geometryProcessor = new GeometryProcessor();
        var queryBuilder = new FeatureQueryBuilder(stringBuilderPool, geometryProcessor, _schemaName);
        var dataAccessLogger = NullLogger<FeatureDataAccess>.Instance;
        var dataAccess = new FeatureDataAccess(new FeatureDataAccessDependencies(
            connectionProvider,
            geometryProcessor,
            cacheManager,
            dictionaryPool,
            StatementCache: null,
            Logger: dataAccessLogger,
            PerformanceOptions: null,
            LimitsOptions: null,
            PerformanceMonitor: null,
            SchemaName: _schemaName));
        _featureStore = new PostgresFeatureStoreRefactored(queryBuilder, dataAccess, cacheManager);

        _spatialFilter = SpatialFilter.Create(
            CreateBboxWkb(-157.95, 21.15, -157.45, 21.65),
            SpatialRelationship.Intersects,
            Srid);

        _simpleWhereQuery = new FeatureQuery
        {
            Where = "category = 'A'",
            Limit = 100,
            SpatialReferenceSrid = Srid,
            OutputSrid = Srid
        };

        _spatialQuery = new FeatureQuery
        {
            SpatialFilter = _spatialFilter,
            Limit = 100,
            SpatialReferenceSrid = Srid,
            OutputSrid = Srid
        };

        _combinedQuery = new FeatureQuery
        {
            Where = "category = 'A'",
            SpatialFilter = _spatialFilter,
            Limit = 100,
            SpatialReferenceSrid = Srid,
            OutputSrid = Srid
        };

        _paginatedQuery = new FeatureQuery
        {
            Where = "category = 'B'",
            Limit = 100,
            Offset = 1000,
            SpatialReferenceSrid = Srid,
            OutputSrid = Srid
        };

        _largeResultSetQuery = new FeatureQuery
        {
            Where = "value > 0",
            Limit = 1000,
            SpatialReferenceSrid = Srid,
            OutputSrid = Srid
        };
    }

    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS {_schemaName} CASCADE;";
        await command.ExecuteNonQueryAsync();
        await _dataSource.DisposeAsync();
    }

    [Benchmark]
    public Task<QueryResult<Feature>> SimpleWhereQuery()
        => _featureStore.QueryAsync(LayerId, _simpleWhereQuery);

    [Benchmark]
    public Task<QueryResult<Feature>> SpatialBboxQuery()
        => _featureStore.QueryAsync(LayerId, _spatialQuery);

    [Benchmark]
    public Task<QueryResult<Feature>> CombinedWhereAndSpatialQuery()
        => _featureStore.QueryAsync(LayerId, _combinedQuery);

    [Benchmark]
    public Task<QueryResult<Feature>> PaginatedQuery()
        => _featureStore.QueryAsync(LayerId, _paginatedQuery);

    [Benchmark]
    public Task<QueryResult<Feature>> LargeResultSet()
        => _featureStore.QueryAsync(LayerId, _largeResultSetQuery);

    private static string ResolveConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("HONUA_BENCH_DB_URL");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Connection string not configured. Set HONUA_BENCH_DB_URL or ConnectionStrings__DefaultConnection.");
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

    private static async Task CreateFeatureTableAsync(NpgsqlConnection connection, string schemaName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {schemaName}.features (
                objectid BIGSERIAL PRIMARY KEY,
                layer_id INT NOT NULL,
                geometry GEOMETRY,
                attributes JSONB,
                created_at TIMESTAMPTZ DEFAULT NOW()
            );
            CREATE INDEX IF NOT EXISTS idx_{schemaName}_features_layer_id ON {schemaName}.features (layer_id);
            CREATE INDEX IF NOT EXISTS idx_{schemaName}_features_geom ON {schemaName}.features USING GIST (geometry);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedFeaturesAsync(NpgsqlConnection connection, string schemaName, int featureCount)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {schemaName}.features (layer_id, geometry, attributes)
            SELECT
                {LayerId},
                ST_SetSRID(
                    ST_MakePoint(-158.0 + random(), 21.0 + random()),
                    {Srid}),
                jsonb_build_object(
                    'name', 'Feature ' || gs,
                    'category', CASE WHEN gs % 2 = 0 THEN 'A' ELSE 'B' END,
                    'value', gs)
            FROM generate_series(1, {featureCount}) AS gs;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AnalyzeAsync(NpgsqlConnection connection, string schemaName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"ANALYZE {schemaName}.features;";
        await command.ExecuteNonQueryAsync();
    }

    private static byte[] CreateBboxWkb(double minX, double minY, double maxX, double maxY)
    {
        var factory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: Srid);
        var polygon = factory.ToGeometry(new Envelope(minX, maxX, minY, maxY));
        return new WKBWriter().Write(polygon);
    }

    private sealed class BenchmarkConnectionProvider : IDatabaseConnectionProvider
    {
        private readonly NpgsqlDataSource _dataSource;

        public BenchmarkConnectionProvider(NpgsqlDataSource dataSource)
        {
            _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        }

        public string GetConnectionString()
            => _dataSource.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var transaction = await connection
                .BeginTransactionAsync(isolationLevel, cancellationToken)
                .ConfigureAwait(false);
            return (connection, transaction);
        }

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return operation();
        }

        public Task ExecuteWithDeadlockRetryAsync(
            Func<Task> operation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return operation();
        }
    }

}

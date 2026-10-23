// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using DuckDB.NET.Data;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.DuckDB.Features.FeatureStore;
using Honua.Db.DuckDB.Features.FeatureStore.Services;
using Honua.Db.DuckDB.Features.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Db.DuckDB.Tests;

/// <summary>
/// Covers how attribute column names reach DuckDB SQL: names that come from automatic
/// column discovery are checked against the shared feature-field name contract, and every
/// attribute identifier is emitted through the provider's quoting helper.
/// </summary>
public sealed class DuckDBAttributeColumnQuotingTests : IAsyncLifetime
{
    private const int LayerId = 0;
    private const string UnsupportedColumnName = "odd\"name";

    private string _dbPath = null!;
    private string _connectionString = null!;
    private DuckDBSpatialBootstrap _spatialBootstrap = null!;

    public async Task InitializeAsync()
    {
        // Second argument is always a generated relative filename (hex GUID + extension), never rooted,
        // so Path.Combine cannot silently drop the temp-path prefix here.
        _dbPath = Path.Join(Path.GetTempPath(), $"honua_test_{Guid.NewGuid():N}.duckdb");
        _connectionString = $"Data Source={_dbPath}";

        await using var seedConnection = new DuckDBConnection(_connectionString);
        await seedConnection.OpenAsync();

        _spatialBootstrap = new DuckDBSpatialBootstrap(
            extensionPath: null,
            logger: NullLogger<DuckDBSpatialBootstrap>.Instance);
        await _spatialBootstrap.EnsureSpatialExtensionAsync(seedConnection, CancellationToken.None);

        // The fourth column carries a double quote in its name, which the shared
        // feature-field name contract does not admit.
        await ExecuteAsync(seedConnection, """
            CREATE TABLE parcels (
                id BIGINT PRIMARY KEY,
                geom GEOMETRY,
                name VARCHAR,
                "odd""name" VARCHAR
            )
            """);

        await ExecuteAsync(
            seedConnection,
            "INSERT INTO parcels VALUES (1, ST_Point(-122.0, 37.0), 'Parcel 1', 'carried')");
    }

    public Task DisposeAsync()
    {
        _spatialBootstrap?.Dispose();

        try { File.Delete(_dbPath); }
        catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine(caughtException);
        }

        try { File.Delete(_dbPath + ".wal"); }
        catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine(caughtException);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public void BuildLayerMappings_DiscoveredColumnOutsideFieldNameContract_IsExcluded()
    {
        var mappings = ServiceCollectionExtensions.BuildLayerMappings(
            BuildOptions(attributes: null),
            _connectionString,
            NullLogger.Instance);

        var mapping = Assert.Single(mappings);
        Assert.Equal(["name"], mapping.AttributeColumns);
        Assert.DoesNotContain(UnsupportedColumnName, mapping.AttributeColumnTypes.Keys, StringComparer.Ordinal);
    }

    [Fact]
    public void BuildLayerMappings_ConfiguredAttributeOutsideFieldNameContract_IsExcluded()
    {
        var mappings = ServiceCollectionExtensions.BuildLayerMappings(
            BuildOptions(attributes: ["name", UnsupportedColumnName]),
            _connectionString,
            NullLogger.Instance);

        var mapping = Assert.Single(mappings);
        Assert.Equal(["name"], mapping.AttributeColumns);
    }

    [Fact]
    public void BuildLayerMappings_OrdinaryDiscoveredColumns_AreUnaffected()
    {
        var mappings = ServiceCollectionExtensions.BuildLayerMappings(
            BuildOptions(attributes: null),
            _connectionString,
            NullLogger.Instance);

        var mapping = Assert.Single(mappings);
        Assert.Contains("name", mapping.AttributeColumns, StringComparer.Ordinal);
        Assert.True(mapping.AttributeColumnTypes.ContainsKey("name"));
    }

    [Fact]
    public async Task GetAsync_LayerWithColumnOutsideFieldNameContract_ServesRemainingAttributes()
    {
        var mappings = ServiceCollectionExtensions.BuildLayerMappings(
            BuildOptions(attributes: null),
            _connectionString,
            NullLogger.Instance);
        var store = CreateStore(mappings);

        var feature = await store.GetAsync(LayerId, 1);

        Assert.NotNull(feature);
        var attributes = feature!.Value.Attributes;
        Assert.Equal("Parcel 1", attributes["name"]);
        Assert.DoesNotContain(UnsupportedColumnName, attributes.Keys, StringComparer.Ordinal);
    }

    [Fact]
    public async Task QueryAsync_LayerWithColumnOutsideFieldNameContract_ServesRemainingAttributes()
    {
        var mappings = ServiceCollectionExtensions.BuildLayerMappings(
            BuildOptions(attributes: null),
            _connectionString,
            NullLogger.Instance);
        var store = CreateStore(mappings);

        var result = await store.QueryAsync(LayerId, new FeatureQuery());

        var feature = Assert.Single(result.Items);
        Assert.Equal("Parcel 1", feature.Attributes["name"]);
        Assert.DoesNotContain(UnsupportedColumnName, feature.Attributes.Keys, StringComparer.Ordinal);
    }

    [Fact]
    public void BuildSelectQuery_MappingCarryingColumnOutsideFieldNameContract_IsRefused()
    {
        var registry = new DuckDBLayerRegistry([
            new DuckDBLayerMapping
            {
                LayerId = LayerId,
                TableName = "parcels",
                GeometryColumn = "geom",
                ObjectIdColumn = "id",
                Srid = 4326,
                AttributeColumns = ["name", UnsupportedColumnName]
            }
        ]);
        var builder = new DuckDBFeatureQueryBuilder(registry);

        Assert.Throws<ArgumentException>(() => builder.BuildSelectQuery(LayerId, new FeatureQuery()));
    }

    private DuckDBOptions BuildOptions(string[]? attributes) => new()
    {
        DatabasePath = _dbPath,
        ReadOnly = false,
        Layers =
        [
            new DuckDBLayerOptions
            {
                Id = LayerId,
                Table = "parcels",
                GeometryColumn = "geom",
                ObjectIdColumn = "id",
                Srid = 4326,
                Attributes = attributes
            }
        ]
    };

    private DuckDBFeatureStore CreateStore(IReadOnlyList<DuckDBLayerMapping> mappings)
    {
        var registry = new DuckDBLayerRegistry(mappings);
        var connectionProvider = new FileDuckDBConnectionProvider(_connectionString, _spatialBootstrap);
        return new DuckDBFeatureStore(
            new DuckDBFeatureQueryBuilder(registry),
            new DuckDBFeatureDataAccess(
                connectionProvider,
                registry,
                null,
                NullLogger<DuckDBFeatureDataAccess>.Instance),
            new DuckDBFeatureCacheManager(registry));
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
    private sealed class FileDuckDBConnectionProvider(
        string connectionString,
        DuckDBSpatialBootstrap spatialBootstrap)
        : Core.Features.Infrastructure.Abstractions.IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString() => connectionString;

        public async Task<System.Data.Common.DbConnection> OpenConnectionAsync(CancellationToken ct = default)
        {
            var conn = new DuckDBConnection(connectionString);
            await conn.OpenAsync(ct);
            await spatialBootstrap.EnsureSpatialExtensionAsync(conn, ct);
            return conn;
        }

        public Task<(System.Data.Common.DbConnection, System.Data.Common.DbTransaction)> OpenTransactionAsync(
            System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.RepeatableRead,
            CancellationToken ct = default)
            => throw new NotSupportedException("Transactions not supported in test provider.");

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken ct = default)
            => operation();

        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken ct = default)
            => operation();
    }
}

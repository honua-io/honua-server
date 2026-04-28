// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.MySql.Features.FeatureStore;
using Honua.MySql.Features.FeatureStore.Services;
using Honua.MySql.Features.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Testcontainers.MySql;

namespace Honua.MySql.Tests;

/// <summary>
/// Gated integration tests that exercise the full MySQL provider stack against a
/// MySQL 8 container. These tests opt in via <c>[Trait("Category", "MySql")]</c>;
/// the standard PR test run skips them. Set <c>HONUA_TEST_MYSQL=1</c> or run
/// <c>dotnet test --filter Category=MySql</c> to opt in.
/// </summary>
[Trait("Category", "MySql")]
public class MySqlFeatureStoreIntegrationTests : IAsyncLifetime
{
    private const int LayerId = 1;
    private MySqlContainer _container = null!;
    private MySqlDataSource _dataSource = null!;
    private MySqlFeatureStore _store = null!;
    private MySqlLayerMappingRegistry _registry = null!;
    private bool _skipped;

    public async Task InitializeAsync()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("HONUA_TEST_MYSQL"), "0", StringComparison.Ordinal))
        {
            _skipped = true;
            return;
        }

        try
        {
            _container = new MySqlBuilder()
                .WithImage("mysql:8.0.36")
                .WithDatabase("honua_test")
                .WithUsername("test")
                .WithPassword("test")
                .Build();
            await _container.StartAsync();
        }
        catch (Exception)
        {
            // Docker not available — gracefully skip.
            _skipped = true;
            return;
        }

        var connectionString = _container.GetConnectionString();
        _dataSource = new MySqlDataSourceBuilder(connectionString).Build();

        await using (var conn = await _dataSource.OpenConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE parcels (
                    id BIGINT PRIMARY KEY,
                    geom POINT NOT NULL SRID 4326,
                    name VARCHAR(64),
                    area DOUBLE,
                    type VARCHAR(32),
                    SPATIAL INDEX(geom)
                ) ENGINE=InnoDB
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var conn = await _dataSource.OpenConnectionAsync())
        {
            for (var i = 1; i <= 10; i++)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO parcels (id, geom, name, area, type)
                    VALUES (@id, ST_SRID(POINT(@lon, @lat), 4326), @name, @area, @type)
                    """;
                cmd.Parameters.AddWithValue("@id", i);
                cmd.Parameters.AddWithValue("@lon", -122.0 + i * 0.01);
                cmd.Parameters.AddWithValue("@lat", 37.0 + i * 0.01);
                cmd.Parameters.AddWithValue("@name", $"Parcel {i}");
                cmd.Parameters.AddWithValue("@area", i * 100.5);
                cmd.Parameters.AddWithValue("@type", i % 2 == 0 ? "residential" : "commercial");
                await cmd.ExecuteNonQueryAsync();
            }
        }

        var mapping = new MySqlLayerMapping
        {
            LayerId = LayerId,
            TableName = "parcels",
            GeometryColumn = "geom",
            PrimaryKeyColumn = "id",
            Srid = 4326,
            AttributeColumns = ["name", "area", "type"],
            GeometryType = GeometryType.Point
        };
        _registry = new MySqlLayerMappingRegistry([mapping]);

        var queryBuilder = new MySqlFeatureQueryBuilder(_registry);
        var connectionProvider = new MySqlConnectionProvider(_dataSource);
        var dataAccess = new MySqlFeatureDataAccess(
            connectionProvider, _registry, performanceMonitor: null,
            NullLogger<MySqlFeatureDataAccess>.Instance);
        _store = new MySqlFeatureStore(queryBuilder, dataAccess);
    }

    public async Task DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    [Fact]
    public async Task Query_Count_Extent_RoundTripAgainstMysql8()
    {
        if (_skipped)
        {
            return;
        }

        var query = new FeatureQuery();

        var count = await _store.CountAsync(LayerId, query);
        Assert.Equal(10, count);

        var result = await _store.QueryAsync(LayerId, query);
        Assert.Equal(10, result.Items.Length);
        Assert.NotNull(result.Items[0].Geometry);
        Assert.True(result.Items[0].Attributes.ContainsKey("name"));

        var extent = await _store.GetExtentAsync(LayerId, null);
        Assert.NotNull(extent);
        Assert.Equal(4326, extent!.Value.SpatialReference);
        Assert.True(extent.Value.MinX < extent.Value.MaxX);
        Assert.True(extent.Value.MinY < extent.Value.MaxY);
    }

    [Fact]
    public async Task Query_WithBboxIntersectsFilter_ReturnsSubset()
    {
        if (_skipped)
        {
            return;
        }

        // Bounding-box polygon WKB covering the first ~5 parcels (lon -121.99..-121.95, lat 37.01..37.05).
        // Build via SQL to avoid hand-rolling WKB. EPSG:4326 has lat-lon axis order in MySQL 8.0+
        // ST_GeomFromText, so request lon-lat axis order explicitly to keep the WKT readable.
        byte[] bboxWkb;
        await using (var conn = await _dataSource.OpenConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT ST_AsWKB(ST_GeomFromText(" +
                "'POLYGON((-121.995 37.005, -121.95 37.005, -121.95 37.055, -121.995 37.055, -121.995 37.005))', " +
                "4326, 'axis-order=long-lat'))";
            var raw = await cmd.ExecuteScalarAsync();
            bboxWkb = (byte[])raw!;
        }

        var query = new FeatureQuery
        {
            SpatialFilter = SpatialFilter.Create(
                geometry: bboxWkb,
                spatialRelationship: SpatialRelationship.Intersects,
                srid: 4326)
        };

        var count = await _store.CountAsync(LayerId, query);
        Assert.True(count is > 0 and < 10);
    }

    [Fact]
    public async Task GetAsync_KnownId_ReturnsFeature()
    {
        if (_skipped)
        {
            return;
        }

        var feature = await _store.GetAsync(LayerId, featureId: 1);

        Assert.NotNull(feature);
        Assert.Equal(1, feature!.Value.Id);
    }
}

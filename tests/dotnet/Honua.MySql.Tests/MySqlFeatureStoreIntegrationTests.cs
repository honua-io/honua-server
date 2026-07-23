// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.MySql.Features.FeatureStore;
using Honua.MySql.Features.FeatureStore.Services;
using Honua.MySql.Features.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Testcontainers.MySql;

namespace Honua.MySql.Tests;

/// <summary>
/// Gated integration tests that exercise the full MySQL provider stack against a
/// MySQL 8 container. The standard PR test run skips them via the
/// <c>Category!=MySql</c> filter. The fixture additionally requires
/// <c>HONUA_TEST_MYSQL=1</c> so a stray <c>--filter Category=MySql</c> does not
/// spin up Docker on machines without it. To actually run the suite, both the
/// category filter and the environment variable are required:
/// <c>HONUA_TEST_MYSQL=1 dotnet test --filter Category=MySql</c>.
/// </summary>
[Trait("Category", "MySql")]
public sealed class MySqlFeatureStoreIntegrationTests : IAsyncLifetime
{
    private const int LayerId = 0;
    private MySqlContainer _container = null!;
    private MySqlDataSource _dataSource = null!;
    private MySqlFeatureStore _store = null!;
    private MySqlLayerMappingRegistry _registry = null!;

    private const string TestMySqlEnvVar = "HONUA_TEST_MYSQL";

    public async Task InitializeAsync()
    {
        _container = new MySqlBuilder()
            .WithImage("mysql:8.0.36")
            .WithDatabase("honua_test")
            .WithUsername("test")
            .WithPassword("test")
            .Build();
        await _container.StartAsync();

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

    [RequiredEnvironmentFact(TestMySqlEnvVar, "1", skipReason: "Set HONUA_TEST_MYSQL=1 to run MySQL Testcontainers integration tests.")]
    public async Task Query_Count_Extent_RoundTripAgainstMysql8()
    {
        var query = new FeatureQuery();

        var count = await _store.CountAsync(LayerId, query);
        Assert.Equal(10, count);

        var result = await _store.QueryAsync(LayerId, query);
        Assert.Equal(10, result.Items.Length);
        Assert.NotNull(result.Items[0].Geometry);
        Assert.True(result.Items[0].Attributes.ContainsKey("name"));

        var extent = await _store.GetExtentAsync(LayerId, null);
        Assert.NotNull(extent);
        var extentValue = extent!.Value;
        Assert.Equal(4326, extentValue.SpatialReference);
        Assert.True(extentValue.MinX < extentValue.MaxX);
        Assert.True(extentValue.MinY < extentValue.MaxY);
    }

    [RequiredEnvironmentFact(TestMySqlEnvVar, "1", skipReason: "Set HONUA_TEST_MYSQL=1 to run MySQL Testcontainers integration tests.")]
    public async Task FeatureServer_BboxQuery_ThroughHttpStack_ReturnsMySqlSubset()
    {
        var fixture = new WebAppFixture().ReplaceService<IFeatureReader>(_store);
        try
        {
            await fixture.InitializeAsync();
            var geometry = Uri.EscapeDataString("-121.995,37.005,-121.95,37.055");

            var response = await fixture.Client.GetAsync(
                $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{LayerId}/query" +
                $"?geometry={geometry}&geometryType=esriGeometryEnvelope&inSR=4326" +
                "&spatialRel=esriSpatialRelIntersects&returnCountOnly=true&f=json");

            response.EnsureSuccessStatusCode();
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(5, payload.RootElement.GetProperty("count").GetInt64());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [RequiredEnvironmentFact(TestMySqlEnvVar, "1", skipReason: "Set HONUA_TEST_MYSQL=1 to run MySQL Testcontainers integration tests.")]
    public async Task GetAsync_KnownId_ReturnsFeature()
    {
        var feature = await _store.GetAsync(LayerId, featureId: 1);

        Assert.NotNull(feature);
        Assert.Equal(1, feature!.Value.Id);
    }

    [RequiredEnvironmentFact(TestMySqlEnvVar, "1", skipReason: "Set HONUA_TEST_MYSQL=1 to run MySQL Testcontainers integration tests.")]
    public async Task StreamFeaturesAsync_PagesThroughAllRows()
    {
        var seen = new List<long>();
        await foreach (var feature in _store.StreamFeaturesAsync(LayerId, new FeatureQuery
        {
            OrderBy = [new OrderByClause("id", ascending: true)]
        }))
        {
            seen.Add(feature.Id);
        }

        Assert.Equal(10, seen.Count);
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (long)i), seen);
    }

    [RequiredEnvironmentFact(TestMySqlEnvVar, "1", skipReason: "Set HONUA_TEST_MYSQL=1 to run MySQL Testcontainers integration tests.")]
    public async Task StreamFeatureBatchesAsync_HonoursBatchSize()
    {
        var batches = new List<IReadOnlyList<Feature>>();
        await foreach (var batch in _store.StreamFeatureBatchesAsync(
            LayerId,
            new FeatureQuery { OrderBy = [new OrderByClause("id", ascending: true)] },
            batchSize: 3))
        {
            batches.Add(batch);
        }

        Assert.Equal(4, batches.Count); // 3 + 3 + 3 + 1
        Assert.Equal(3, batches[0].Count);
        Assert.Single(batches[3]);
    }

    [RequiredEnvironmentFact(TestMySqlEnvVar, "1", skipReason: "Set HONUA_TEST_MYSQL=1 to run MySQL Testcontainers integration tests.")]
    public async Task StreamGmlFeaturesAsync_ThrowsNotSupported()
    {
        Assert.Throws<NotSupportedException>(() =>
            _store.StreamGmlFeaturesAsync(LayerId, new FeatureQuery()));
        await Task.CompletedTask;
    }
}

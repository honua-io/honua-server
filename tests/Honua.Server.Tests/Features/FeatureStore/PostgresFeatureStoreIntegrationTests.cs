// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Core.Queries.Filters;
using Honua.Postgres.Features.FeatureStore;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Features.FeatureStore;

/// <summary>
/// Comprehensive integration tests for PostgreSQL feature store operations
/// </summary>
[Collection("Database")]
[Protocol(Protocols.FeatureServer)]
public class PostgresFeatureStoreIntegrationTests : IAsyncLifetime
{
    private const int PointsLayerId = 11001;
    private const int PolygonsLayerId = 11002;
    private const int LinesLayerId = 11003;
    private static readonly GeometryFactory GeometryFactory = new(new PrecisionModel(), 4326);

    private readonly DatabaseFixtureAdapter _fixture;
    private readonly string _testSchema;

    public PostgresFeatureStoreIntegrationTests(DatabaseFixtureAdapter fixture)
    {
        _fixture = fixture;
        _testSchema = $"test_fs_{Guid.NewGuid():N}";
    }

    public async Task InitializeAsync()
    {
        await _fixture.ExecuteAsync($"CREATE SCHEMA {_testSchema}");
        await EnsureLayerCatalogAsync();

        var createSql = $@"
            CREATE TABLE {_testSchema}.features (
                objectid bigserial PRIMARY KEY,
                layer_id integer NOT NULL,
                geometry geometry,
                attributes jsonb NOT NULL DEFAULT '{{}}'::jsonb
            );

            CREATE INDEX idx_features_layer_id ON {_testSchema}.features(layer_id);
            CREATE INDEX idx_features_geom ON {_testSchema}.features USING GIST (geometry);
            ";
        await _fixture.ExecuteAsync(createSql);

        await InsertTestDataAsync();
    }

    public async Task DisposeAsync()
    {
        await _fixture.ExecuteAsync($"DROP SCHEMA {_testSchema} CASCADE");
    }

    private async Task EnsureLayerCatalogAsync()
    {
        var createCatalogSql = """
            CREATE SCHEMA IF NOT EXISTS honua;

            CREATE TABLE IF NOT EXISTS honua.layers (
                layer_id SERIAL PRIMARY KEY,
                layer_name TEXT NOT NULL,
                description TEXT,
                table_schema TEXT NOT NULL DEFAULT current_schema(),
                table_name TEXT NOT NULL,
                geometry_type TEXT NOT NULL,
                srid INT NOT NULL DEFAULT 4326,
                extent GEOMETRY(POLYGON, 4326),
                min_scale DOUBLE PRECISION,
                max_scale DOUBLE PRECISION,
                default_visibility BOOLEAN NOT NULL DEFAULT TRUE,
                created_at TIMESTAMPTZ DEFAULT NOW()
            );

            ALTER TABLE honua.layers
                ADD COLUMN IF NOT EXISTS table_schema TEXT NOT NULL DEFAULT current_schema();
            """;

        await _fixture.ExecuteAsync(createCatalogSql);

        var insertSql = $"""
            INSERT INTO honua.layers (
                layer_id,
                layer_name,
                table_schema,
                table_name,
                geometry_type,
                srid
            )
            VALUES
                ({PointsLayerId}, 'Test Points', '{_testSchema}', 'features', 'Point', 4326),
                ({PolygonsLayerId}, 'Test Polygons', '{_testSchema}', 'features', 'Polygon', 4326),
                ({LinesLayerId}, 'Test Lines', '{_testSchema}', 'features', 'LineString', 4326)
            ON CONFLICT (layer_id) DO UPDATE SET
                layer_name = EXCLUDED.layer_name,
                table_schema = EXCLUDED.table_schema,
                table_name = EXCLUDED.table_name,
                geometry_type = EXCLUDED.geometry_type,
                srid = EXCLUDED.srid;
            """;

        await _fixture.ExecuteAsync(insertSql);
    }

    private async Task InsertTestDataAsync()
    {
        var store = CreateFeatureStore();
        var now = DateTime.UtcNow;

        for (int i = 0; i < 100; i++)
        {
            var attributes = ImmutableDictionary<string, object?>.Empty
                .Add("category", i % 3 == 0 ? "retail" : i % 3 == 1 ? "residential" : "commercial")
                .Add("value", i * 10)
                .Add("active", i % 2 == 0)
                .Add("created_date", now.AddDays(-i));

            var geometry = CreatePointWkb(-122.0 + (i * 0.01), 37.0 + (i * 0.01));
            var feature = Feature.Create(0, geometry, attributes);

            await store.CreateAsync(PointsLayerId, feature, CancellationToken.None);
        }

        for (int i = 0; i < 20; i++)
        {
            var wkt = $"POLYGON(({-122 + i * 0.1} {37 + i * 0.1}, {-122 + i * 0.1 + 0.05} {37 + i * 0.1}, {-122 + i * 0.1 + 0.05} {37 + i * 0.1 + 0.05}, {-122 + i * 0.1} {37 + i * 0.1 + 0.05}, {-122 + i * 0.1} {37 + i * 0.1}))";
            var attributes = ImmutableDictionary<string, object?>.Empty
                .Add("area_name", $"Zone_{i}")
                .Add("population", 1000 + i * 100)
                .Add("density", 50.5 + i);

            var geometry = CreatePolygonWkb(wkt);
            var feature = Feature.Create(0, geometry, attributes);

            await store.CreateAsync(PolygonsLayerId, feature, CancellationToken.None);
        }

        for (int i = 0; i < 30; i++)
        {
            var coordinates = new[]
            {
                (-122.0 + i * 0.05, 37.0 + i * 0.05),
                (-122.0 + i * 0.05 + 0.02, 37.0 + i * 0.05 + 0.02)
            };

            var attributes = ImmutableDictionary<string, object?>.Empty
                .Add("road_type", i % 2 == 0 ? "highway" : "street")
                .Add("length_km", Math.Round(2.5 + i * 0.3, 2));

            var geometry = CreateLineStringWkb(coordinates);
            var feature = Feature.Create(0, geometry, attributes);

            await store.CreateAsync(LinesLayerId, feature, CancellationToken.None);
        }
    }

    [IntegrationTest]
    [Operation("Query")]
    [Endpoint("PostgreSQL FeatureStore Query")]
    public async Task Query_WithSimpleFilter_ShouldReturnMatchingFeatures()
    {
        // Arrange
        var store = CreateFeatureStore();
        var query = FeatureQuery.WithWhere("category = 'retail'");

        // Act
        var result = await store.QueryAsync(GetLayerId("points"), query, CancellationToken.None);

        // Assert
        result.Items.Should().NotBeEmpty();
        result.Items.Should().AllSatisfy(f => f.Attributes["category"].Should().Be("retail"));
    }

    [IntegrationTest]
    [Operation("Query")]
    [Endpoint("PostgreSQL FeatureStore Complex Query")]
    public async Task Query_WithComplexFilter_ShouldReturnCorrectResults()
    {
        // Arrange
        var store = CreateFeatureStore();
        var sqlFilter = new SqlFragment(
            "attributes->>'category' IN (@p0, @p1) AND NULLIF(attributes->>'value', '')::int >= @p2 AND attributes->>'active' = @p3",
            new object?[] { "retail", "commercial", 500, "true" });

        var query = FeatureQuery.WithSqlFilter(sqlFilter);

        // Act
        var result = await store.QueryAsync(GetLayerId("points"), query, CancellationToken.None);

        // Assert
        result.Items.Should().NotBeEmpty();
        result.Items.Should().AllSatisfy(f =>
        {
            var category = f.Attributes["category"]?.ToString();
            category.Should().BeOneOf("retail", "commercial");
            Convert.ToInt32(f.Attributes["value"], CultureInfo.InvariantCulture).Should().BeGreaterOrEqualTo(500);
            Convert.ToBoolean(f.Attributes["active"], CultureInfo.InvariantCulture).Should().BeTrue();
        });
    }

    [IntegrationTest]
    [Operation("Query")]
    [Endpoint("PostgreSQL FeatureStore Spatial Query")]
    public async Task Query_WithSpatialFilter_ShouldReturnFeaturesInBounds()
    {
        // Arrange
        var store = CreateFeatureStore();
        var envelope = new Envelope(-122.0, -121.9, 37.0, 37.1);
        var geometry = GeometryFactory.ToGeometry(envelope);
        var wkb = new WKBWriter().Write(geometry);
        var spatialFilter = SpatialFilter.Create(wkb, SpatialRelationship.Intersects, 4326);
        var query = FeatureQuery.WithSpatialFilter(spatialFilter);

        // Act
        var result = await store.QueryAsync(GetLayerId("points"), query, CancellationToken.None);

        // Assert
        result.Items.Should().NotBeEmpty();
        result.Items.Should().HaveCountGreaterThan(5);
    }

    [IntegrationTest]
    [Operation("Count")]
    [Endpoint("PostgreSQL FeatureStore Count")]
    public async Task Count_WithFilter_ShouldReturnCorrectCount()
    {
        // Arrange
        var store = CreateFeatureStore();
        var query = FeatureQuery.WithWhere("category = 'retail'");

        // Act
        var count = await store.CountAsync(GetLayerId("points"), query, CancellationToken.None);

        // Assert
        count.Should().BeGreaterThan(0);
        count.Should().Be(34);
    }

    [IntegrationTest]
    [Operation("Get")]
    [Endpoint("PostgreSQL FeatureStore Get Single")]
    public async Task Get_WithValidId_ShouldReturnFeature()
    {
        // Arrange
        var store = CreateFeatureStore();

        // Act
        var feature = await store.GetAsync(GetLayerId("points"), 1, CancellationToken.None);

        // Assert
        feature.Should().NotBeNull();
        var retrieved = feature!.Value;
        retrieved.Id.Should().Be(1);
        retrieved.Attributes.Should().ContainKey("category");
        retrieved.Attributes.Should().ContainKey("value");
        retrieved.Geometry.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation("Get")]
    [Endpoint("PostgreSQL FeatureStore Get Nonexistent")]
    public async Task Get_WithInvalidId_ShouldReturnNull()
    {
        // Arrange
        var store = CreateFeatureStore();

        // Act
        var feature = await store.GetAsync(GetLayerId("points"), 99999, CancellationToken.None);

        // Assert
        feature.Should().BeNull();
    }

    [IntegrationTest]
    [Operation("Create")]
    [Endpoint("PostgreSQL FeatureStore Create")]
    public async Task Create_WithNewFeature_ShouldInsertSuccessfully()
    {
        // Arrange
        var store = CreateFeatureStore();
        var attributes = ImmutableDictionary<string, object?>.Empty
            .Add("category", "new_retail")
            .Add("value", 1000)
            .Add("active", true)
            .Add("created_date", DateTime.UtcNow);

        var geometry = CreatePointWkb(-1, 1);
        var newFeature = Feature.Create(0, geometry, attributes);

        // Act
        var result = await store.CreateAsync(GetLayerId("points"), newFeature, CancellationToken.None);

        // Assert
        result.Id.Should().BeGreaterThan(0);

        var insertedFeature = await store.GetAsync(GetLayerId("points"), result.Id, CancellationToken.None);
        insertedFeature.Should().NotBeNull();
        insertedFeature!.Value.Attributes["category"].Should().Be("new_retail");
    }

    [IntegrationTest]
    [Operation("Update")]
    [Endpoint("PostgreSQL FeatureStore Update")]
    public async Task Update_ExistingFeature_ShouldModifySuccessfully()
    {
        // Arrange
        var store = CreateFeatureStore();
        var existingFeature = await store.GetAsync(GetLayerId("points"), 1, CancellationToken.None);
        existingFeature.Should().NotBeNull();
        var existing = existingFeature!.Value;
        var updatedAttributes = existing.Attributes
            .SetItem("category", "updated_category")
            .SetItem("value", 9999);

        var updatedFeature = Feature.Create(existing.Id, existing.Geometry, updatedAttributes);

        // Act
        var updated = await store.UpdateAsync(GetLayerId("points"), updatedFeature, CancellationToken.None);

        // Assert
        updated.Attributes["category"].Should().Be("updated_category");
        updated.Attributes["value"].Should().Be(9999L);

        var verifyFeature = await store.GetAsync(GetLayerId("points"), 1, CancellationToken.None);
        verifyFeature.Should().NotBeNull();
        var verified = verifyFeature!.Value;
        verified.Attributes["category"].Should().Be("updated_category");
        verified.Attributes["value"].Should().Be(9999L);
    }

    [IntegrationTest]
    [Operation("Delete")]
    [Endpoint("PostgreSQL FeatureStore Delete")]
    public async Task Delete_ExistingFeature_ShouldRemoveSuccessfully()
    {
        // Arrange
        var store = CreateFeatureStore();
        var existingFeature = await store.GetAsync(GetLayerId("points"), 2, CancellationToken.None);
        existingFeature.Should().NotBeNull();

        // Act
        var success = await store.DeleteAsync(GetLayerId("points"), 2, CancellationToken.None);

        // Assert
        success.Should().BeTrue();

        var deletedFeature = await store.GetAsync(GetLayerId("points"), 2, CancellationToken.None);
        deletedFeature.Should().BeNull();
    }

    [IntegrationTest]
    [Operation("ApplyEdits")]
    [Endpoint("PostgreSQL FeatureStore Batch Operations")]
    public async Task ApplyEdits_WithMixedOperations_ShouldExecuteAllSuccessfully()
    {
        // Arrange
        var store = CreateFeatureStore();

        var newFeature = Feature.Create(0,
            CreatePointWkb(-1, 1),
            ImmutableDictionary<string, object?>.Empty
                .Add("category", "batch_new")
                .Add("value", 500)
                .Add("active", true)
                .Add("created_date", DateTime.UtcNow));

        var existingFeature = await store.GetAsync(GetLayerId("points"), 3, CancellationToken.None);
        existingFeature.Should().NotBeNull();
        var existing = existingFeature!.Value;
        var updatedFeature = Feature.Create(existing.Id, existing.Geometry,
            existing.Attributes.SetItem("category", "batch_updated"));

        var editBatch = FeatureEditBatch.Create(
            creates: ImmutableArray.Create(newFeature),
            updates: ImmutableArray.Create(updatedFeature),
            deletes: ImmutableArray.Create<long>(4));

        // Act
        var result = await store.ApplyEditsAsync(GetLayerId("points"), editBatch, CancellationToken.None);

        // Assert
        result.CreatedCount.Should().Be(1);
        result.UpdatedCount.Should().Be(1);
        result.DeletedCount.Should().Be(1);
        result.IsSuccess.Should().BeTrue();

        var deletedFeature = await store.GetAsync(GetLayerId("points"), 4, CancellationToken.None);
        deletedFeature.Should().BeNull();

        var verifyUpdatedFeature = await store.GetAsync(GetLayerId("points"), 3, CancellationToken.None);
        verifyUpdatedFeature.Should().NotBeNull();
        verifyUpdatedFeature!.Value.Attributes["category"].Should().Be("batch_updated");
    }

    [IntegrationTest]
    [Operation("Stream")]
    [Endpoint("PostgreSQL FeatureStore Streaming")]
    public async Task Stream_LargeResultSet_ShouldStreamEfficiently()
    {
        // Arrange
        var store = CreateFeatureStore();
        var query = new FeatureQuery
        {
            OrderBy = ImmutableArray.Create(OrderByClause.Asc("objectid"))
        };

        // Act
        var features = new List<Feature>();
        await foreach (var feature in store.StreamFeaturesAsync(GetLayerId("points"), query, CancellationToken.None))
        {
            features.Add(feature);
        }

        // Assert
        features.Should().HaveCount(100);
        features.Should().BeInAscendingOrder(f => f.Id);
        features.Should().AllSatisfy(f => f.Geometry.Should().NotBeNull());
    }

    [IntegrationTest]
    [Operation("Query")]
    [Endpoint("PostgreSQL FeatureStore Pagination")]
    public async Task Query_WithPagination_ShouldReturnPagedResults()
    {
        // Arrange
        var store = CreateFeatureStore();
        var query = new FeatureQuery
        {
            Limit = 10,
            Offset = 20,
            OrderBy = ImmutableArray.Create(OrderByClause.Asc("objectid"))
        };

        // Act
        var result = await store.QueryAsync(GetLayerId("points"), query, CancellationToken.None);

        // Assert
        result.Items.Should().HaveCount(10);
        result.Items.Should().BeInAscendingOrder(f => f.Id);
        result.Items.First().Id.Should().Be(21);
    }

    [IntegrationTest]
    [Operation("Query")]
    [Endpoint("PostgreSQL FeatureStore Geometry Types")]
    public async Task Query_DifferentGeometryTypes_ShouldHandleAllTypes()
    {
        // Arrange
        var store = CreateFeatureStore();
        var query = new FeatureQuery
        {
            Limit = 1,
            OrderBy = ImmutableArray.Create(OrderByClause.Asc("objectid"))
        };

        // Act & Assert
        var pointResult = await store.QueryAsync(GetLayerId("points"), query, CancellationToken.None);
        pointResult.Items.Should().HaveCount(1);
        pointResult.Items.First().Geometry.Should().NotBeNull();

        var polygonResult = await store.QueryAsync(GetLayerId("polygons"), query, CancellationToken.None);
        polygonResult.Items.Should().HaveCount(1);
        polygonResult.Items.First().Geometry.Should().NotBeNull();

        var lineResult = await store.QueryAsync(GetLayerId("lines"), query, CancellationToken.None);
        lineResult.Items.Should().HaveCount(1);
        lineResult.Items.First().Geometry.Should().NotBeNull();
    }

    private PostgresFeatureStore CreateFeatureStore()
    {
        var connectionProvider = new TestDatabaseConnectionProvider(_fixture.DataSource);
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new Honua.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy());
        var performanceMonitor = Substitute.For<IPerformanceMonitor>();

        return new PostgresFeatureStore(
            connectionProvider,
            stringBuilderPool,
            performanceMonitor,
            NullLogger<PostgresFeatureStore>.Instance,
            _testSchema);
    }

    private static int GetLayerId(string tableName)
    {
        return tableName switch
        {
            "points" => PointsLayerId,
            "polygons" => PolygonsLayerId,
            "lines" => LinesLayerId,
            _ => throw new ArgumentException($"Unknown table: {tableName}")
        };
    }

    private static byte[] CreatePointWkb(double lon, double lat)
    {
        var point = GeometryFactory.CreatePoint(new Coordinate(lon, lat));
        return new WKBWriter().Write(point);
    }

    private static byte[] CreateLineStringWkb(IEnumerable<(double lon, double lat)> coordinates)
    {
        var coords = coordinates.Select(c => new Coordinate(c.lon, c.lat)).ToArray();
        var line = GeometryFactory.CreateLineString(coords);
        return new WKBWriter().Write(line);
    }

    private static byte[] CreatePolygonWkb(string wkt)
    {
        var polygon = new WKTReader(NtsGeometryServices.Instance).Read(wkt);
        polygon.SRID = GeometryFactory.SRID;
        return new WKBWriter().Write(polygon);
    }
}

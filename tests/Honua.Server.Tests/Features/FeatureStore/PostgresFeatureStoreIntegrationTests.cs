// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.FeatureStore;

/// <summary>
/// Comprehensive integration tests for PostgreSQL feature store operations
/// </summary>
[Collection("Database")]
[Protocol(Protocols.FeatureServer)]
public class PostgresFeatureStoreIntegrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly string _testSchema;

    public PostgresFeatureStoreIntegrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _testSchema = $"test_fs_{Guid.NewGuid():N}";
    }

    public async Task InitializeAsync()
    {
        await _fixture.ExecuteAsync($"CREATE SCHEMA {_testSchema}");

        // Create comprehensive test data
        await _fixture.CreateTestData(_testSchema)
            .WithTable("points", "POINT", 4326, new Dictionary<string, string>
            {
                ["category"] = "TEXT",
                ["value"] = "INTEGER",
                ["active"] = "BOOLEAN",
                ["created_date"] = "TIMESTAMPTZ"
            })
            .WithTable("polygons", "POLYGON", 4326, new Dictionary<string, string>
            {
                ["area_name"] = "TEXT",
                ["population"] = "BIGINT",
                ["density"] = "REAL"
            })
            .WithTable("lines", "LINESTRING", 4326, new Dictionary<string, string>
            {
                ["road_type"] = "TEXT",
                ["length_km"] = "NUMERIC(10,2)"
            })
            .BuildAsync();

        // Insert comprehensive test data
        await InsertTestDataAsync();
    }

    public async Task DisposeAsync()
    {
        await _fixture.ExecuteAsync($"DROP SCHEMA {_testSchema} CASCADE");
    }

    private async Task InsertTestDataAsync()
    {
        var builder = _fixture.CreateTestData(_testSchema);

        // Insert points with various attributes
        for (int i = 0; i < 100; i++)
        {
            var additionalValues = new Dictionary<string, object>
            {
                ["category"] = i % 3 == 0 ? "retail" : i % 3 == 1 ? "residential" : "commercial",
                ["value"] = i * 10,
                ["active"] = i % 2 == 0,
                ["created_date"] = DateTime.UtcNow.AddDays(-i)
            };

            builder.WithPoint("points", $"Point_{i}", -122.0 + (i * 0.01), 37.0 + (i * 0.01), additionalValues);
        }

        // Insert polygons
        for (int i = 0; i < 20; i++)
        {
            var wkt = $"POLYGON(({-122 + i * 0.1} {37 + i * 0.1}, {-122 + i * 0.1 + 0.05} {37 + i * 0.1}, {-122 + i * 0.1 + 0.05} {37 + i * 0.1 + 0.05}, {-122 + i * 0.1} {37 + i * 0.1 + 0.05}, {-122 + i * 0.1} {37 + i * 0.1}))";
            var additionalValues = new Dictionary<string, object>
            {
                ["area_name"] = $"Zone_{i}",
                ["population"] = 1000 + i * 100,
                ["density"] = 50.5f + i
            };

            builder.WithPolygon("polygons", $"Polygon_{i}", wkt, 4326, additionalValues);
        }

        // Insert linestrings
        for (int i = 0; i < 30; i++)
        {
            var coordinates = new[]
            {
                (-122.0 + i * 0.05, 37.0 + i * 0.05),
                (-122.0 + i * 0.05 + 0.02, 37.0 + i * 0.05 + 0.02)
            };
            var additionalValues = new Dictionary<string, object>
            {
                ["road_type"] = i % 2 == 0 ? "highway" : "street",
                ["length_km"] = Math.Round(2.5 + i * 0.3, 2)
            };

            builder.WithLineString("lines", $"Line_{i}", coordinates);

            // Add attributes manually as WithLineString doesn't support additional values
            await _fixture.ExecuteAsync($"""
                UPDATE {_testSchema}.lines
                SET road_type = @road_type, length_km = @length_km
                WHERE name = @name
                """, _testSchema, new Dictionary<string, object>
            {
                ["road_type"] = additionalValues["road_type"],
                ["length_km"] = additionalValues["length_km"],
                ["name"] = $"Line_{i}"
            });
        }

        await builder.BuildAsync();
    }

    [IntegrationTest]
    [Operation("Query")]
    [Endpoint("PostgreSQL FeatureStore Query")]
    public async Task Query_WithSimpleFilter_ShouldReturnMatchingFeatures()
    {
        // Arrange
        var store = CreateFeatureStore();
        var filter = FilterExpression.Comparison(
            FilterExpression.Property("category"),
            FilterOperator.Equal,
            FilterExpression.Literal("retail"));

        var query = new ParameterizedQuery
        {
            Sql = "SELECT id, name, category, value, active, created_date, ST_AsBinary(geom) as geom FROM points WHERE category = @p0",
            WhereParameters = new object[] { "retail" }
        };

        // Act
        var result = await store.QueryAsync(GetLayerId("points"), query, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Features.Should().NotBeEmpty();
        result.Features.Should().AllSatisfy(f => f.Attributes["category"].Should().Be("retail"));
    }

    [IntegrationTest]
    [Operation("Query")]
    [Endpoint("PostgreSQL FeatureStore Complex Query")]
    public async Task Query_WithComplexFilter_ShouldReturnCorrectResults()
    {
        // Arrange
        var store = CreateFeatureStore();
        var query = new ParameterizedQuery
        {
            Sql = """
                SELECT id, name, category, value, active, created_date, ST_AsBinary(geom) as geom
                FROM points
                WHERE category IN ('retail', 'commercial')
                AND value >= @p0
                AND active = @p1
                """,
            WhereParameters = new object[] { 500, true }
        };

        // Act
        var result = await store.QueryAsync(GetLayerId("points"), query, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Features.Should().NotBeEmpty();
        result.Features.Should().AllSatisfy(f =>
        {
            var category = f.Attributes["category"]?.ToString();
            category.Should().BeOneOf("retail", "commercial");
            Convert.ToInt32(f.Attributes["value"]).Should().BeGreaterOrEqualTo(500);
            Convert.ToBoolean(f.Attributes["active"]).Should().BeTrue();
        });
    }

    [IntegrationTest]
    [Operation("Query")]
    [Endpoint("PostgreSQL FeatureStore Spatial Query")]
    public async Task Query_WithSpatialFilter_ShouldReturnFeaturesInBounds()
    {
        // Arrange
        var store = CreateFeatureStore();
        var bounds = "ST_MakeEnvelope(-122.0, 37.0, -121.9, 37.1, 4326)";
        var query = new ParameterizedQuery
        {
            Sql = $"""
                SELECT id, name, category, value, active, created_date, ST_AsBinary(geom) as geom
                FROM points
                WHERE ST_Intersects(geom, {bounds})
                """,
            WhereParameters = new object[0]
        };

        // Act
        var result = await store.QueryAsync(GetLayerId("points"), query, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Features.Should().NotBeEmpty();
        result.Features.Should().HaveCountGreaterThan(5); // Should find multiple points in this area
    }

    [IntegrationTest]
    [Operation("Count")]
    [Endpoint("PostgreSQL FeatureStore Count")]
    public async Task Count_WithFilter_ShouldReturnCorrectCount()
    {
        // Arrange
        var store = CreateFeatureStore();
        var query = new ParameterizedQuery
        {
            Sql = "SELECT COUNT(*) FROM points WHERE category = @p0",
            WhereParameters = new object[] { "retail" }
        };

        // Act
        var count = await store.CountAsync(GetLayerId("points"), query, CancellationToken.None);

        // Assert
        count.Should().BeGreaterThan(0);
        count.Should().Be(34); // ~1/3 of 100 points should be retail
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
        feature!.Id.Should().Be(1);
        feature.Attributes.Should().ContainKey("category");
        feature.Attributes.Should().ContainKey("value");
        feature.Geometry.Should().NotBeNull();
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

        var geometry = "0101000020E6100000000000000000F0BF000000000000F03F"; // POINT(-1 1) in WKB hex
        var newFeature = Feature.Create(0, Convert.FromHexString(geometry), attributes);

        // Act
        var result = await store.CreateAsync(GetLayerId("points"), newFeature, CancellationToken.None);

        // Assert
        result.Should().BeGreaterThan(0);

        // Verify it was inserted
        var insertedFeature = await store.GetAsync(GetLayerId("points"), result, CancellationToken.None);
        insertedFeature.Should().NotBeNull();
        insertedFeature!.Attributes["category"].Should().Be("new_retail");
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

        var updatedAttributes = existingFeature!.Attributes
            .SetItem("category", "updated_category")
            .SetItem("value", 9999);

        var updatedFeature = Feature.Create(existingFeature.Id, existingFeature.Geometry, updatedAttributes);

        // Act
        var success = await store.UpdateAsync(GetLayerId("points"), updatedFeature, CancellationToken.None);

        // Assert
        success.Should().BeTrue();

        // Verify the update
        var verifyFeature = await store.GetAsync(GetLayerId("points"), 1, CancellationToken.None);
        verifyFeature.Should().NotBeNull();
        verifyFeature!.Attributes["category"].Should().Be("updated_category");
        verifyFeature.Attributes["value"].Should().Be(9999);
    }

    [IntegrationTest]
    [Operation("Delete")]
    [Endpoint("PostgreSQL FeatureStore Delete")]
    public async Task Delete_ExistingFeature_ShouldRemoveSuccessfully()
    {
        // Arrange
        var store = CreateFeatureStore();

        // Verify feature exists first
        var existingFeature = await store.GetAsync(GetLayerId("points"), 2, CancellationToken.None);
        existingFeature.Should().NotBeNull();

        // Act
        var success = await store.DeleteAsync(GetLayerId("points"), 2, CancellationToken.None);

        // Assert
        success.Should().BeTrue();

        // Verify deletion
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
            Convert.FromHexString("0101000020E6100000000000000000F0BF000000000000F03F"),
            ImmutableDictionary<string, object?>.Empty
                .Add("category", "batch_new")
                .Add("value", 500)
                .Add("active", true)
                .Add("created_date", DateTime.UtcNow));

        var existingFeature = await store.GetAsync(GetLayerId("points"), 3, CancellationToken.None);
        var updatedFeature = Feature.Create(existingFeature!.Id, existingFeature.Geometry,
            existingFeature.Attributes.SetItem("category", "batch_updated"));

        var editBatch = new FeatureEditBatch
        {
            Adds = new[] { newFeature },
            Updates = new[] { updatedFeature },
            Deletes = new long[] { 4 }
        };

        // Act
        var result = await store.ApplyEditsAsync(GetLayerId("points"), editBatch, CancellationToken.None);

        // Assert
        result.AddedCount.Should().Be(1);
        result.UpdatedCount.Should().Be(1);
        result.DeletedCount.Should().Be(1);
        result.Success.Should().BeTrue();

        // Verify operations
        var deletedFeature = await store.GetAsync(GetLayerId("points"), 4, CancellationToken.None);
        deletedFeature.Should().BeNull();

        var verifyUpdatedFeature = await store.GetAsync(GetLayerId("points"), 3, CancellationToken.None);
        verifyUpdatedFeature!.Attributes["category"].Should().Be("batch_updated");
    }

    [IntegrationTest]
    [Operation("Stream")]
    [Endpoint("PostgreSQL FeatureStore Streaming")]
    public async Task Stream_LargeResultSet_ShouldStreamEfficiently()
    {
        // Arrange
        var store = CreateFeatureStore();
        var query = new ParameterizedQuery
        {
            Sql = "SELECT id, name, category, value, active, created_date, ST_AsBinary(geom) as geom FROM points ORDER BY id",
            WhereParameters = new object[0]
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
        var query = new ParameterizedQuery
        {
            Sql = "SELECT id, name, category, value, active, created_date, ST_AsBinary(geom) as geom FROM points ORDER BY id LIMIT @p0 OFFSET @p1",
            WhereParameters = new object[] { 10, 20 }
        };

        // Act
        var result = await store.QueryAsync(GetLayerId("points"), query, CancellationToken.None);

        // Assert
        result.Features.Should().HaveCount(10);
        result.Features.Should().BeInAscendingOrder(f => f.Id);
        result.Features.First().Id.Should().Be(21); // Due to 1-based IDs and OFFSET 20
    }

    [IntegrationTest]
    [Operation("Query")]
    [Endpoint("PostgreSQL FeatureStore Geometry Types")]
    public async Task Query_DifferentGeometryTypes_ShouldHandleAllTypes()
    {
        // Arrange
        var store = CreateFeatureStore();

        // Test points
        var pointQuery = new ParameterizedQuery
        {
            Sql = "SELECT id, name, ST_AsBinary(geom) as geom FROM points LIMIT 1",
            WhereParameters = new object[0]
        };

        // Test polygons
        var polygonQuery = new ParameterizedQuery
        {
            Sql = "SELECT id, name, area_name, population, density, ST_AsBinary(geom) as geom FROM polygons LIMIT 1",
            WhereParameters = new object[0]
        };

        // Test lines
        var lineQuery = new ParameterizedQuery
        {
            Sql = "SELECT id, name, road_type, length_km, ST_AsBinary(geom) as geom FROM lines LIMIT 1",
            WhereParameters = new object[0]
        };

        // Act & Assert
        var pointResult = await store.QueryAsync(GetLayerId("points"), pointQuery, CancellationToken.None);
        pointResult.Features.Should().HaveCount(1);
        pointResult.Features.First().Geometry.Should().NotBeNull();

        var polygonResult = await store.QueryAsync(GetLayerId("polygons"), polygonQuery, CancellationToken.None);
        polygonResult.Features.Should().HaveCount(1);
        polygonResult.Features.First().Geometry.Should().NotBeNull();

        var lineResult = await store.QueryAsync(GetLayerId("lines"), lineQuery, CancellationToken.None);
        lineResult.Features.Should().HaveCount(1);
        lineResult.Features.First().Geometry.Should().NotBeNull();
    }

    private IFeatureStore CreateFeatureStore()
    {
        // This would be injected in real scenarios
        // For tests, we create a mock or test implementation
        throw new NotImplementedException("Feature store creation needs to be implemented based on actual DI setup");
    }

    private int GetLayerId(string tableName)
    {
        // In real scenarios, this would come from the layer catalog
        // For tests, we use simple mapping
        return tableName switch
        {
            "points" => 1,
            "polygons" => 2,
            "lines" => 3,
            _ => throw new ArgumentException($"Unknown table: {tableName}")
        };
    }
}

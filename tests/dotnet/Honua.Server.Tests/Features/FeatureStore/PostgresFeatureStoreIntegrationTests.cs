// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Postgres.Features.FeatureStore;
using Honua.Postgres.Features.FeatureStore.Services;
using Honua.Server.Features.Infrastructure.Rendering;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.FeatureStore;

/// <summary>
/// Comprehensive integration tests for PostgreSQL feature store operations
/// </summary>
[Collection("Database")]
[Protocol(Protocols.TestQuality)]
public class PostgresFeatureStoreIntegrationTests : IAsyncLifetime
{
    private const int PointsLayerId = 11001;
    private const int PolygonsLayerId = 11002;
    private const int LinesLayerId = 11003;
    private static readonly GeometryFactory _geometryFactory = new(new PrecisionModel(), 4326);

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
                enabled BOOLEAN NOT NULL DEFAULT TRUE,
                metadata JSONB,
                created_at TIMESTAMPTZ DEFAULT NOW()
            );

            ALTER TABLE honua.layers
                ADD COLUMN IF NOT EXISTS table_schema TEXT NOT NULL DEFAULT current_schema();

            ALTER TABLE honua.layers
                ADD COLUMN IF NOT EXISTS metadata JSONB;
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
    [Operation(Operations.Query)]
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
    [Operation(Operations.Query)]
    public async Task Query_WithNullAttributes_ShouldReturnObjectId()
    {
        const long objectId = 999999;

        await _fixture.ExecuteAsync($"""
            ALTER TABLE {_testSchema}.features ALTER COLUMN attributes DROP NOT NULL;

            INSERT INTO {_testSchema}.features (objectid, layer_id, geometry, attributes)
            VALUES ({objectId}, {PointsLayerId}, ST_GeomFromText('POINT(1 1)', 4326), NULL);
            """);

        try
        {
            var store = CreateFeatureStore();
            var query = new FeatureQuery
            {
                ObjectIds = ImmutableArray.Create(objectId)
            };

            var result = await store.QueryAsync(PointsLayerId, query, CancellationToken.None);
            result.Items.Should().HaveCount(1);
            result.Items[0].Attributes.Should().ContainKey("objectid");

            var gmlResult = await store.QueryGmlAsync(PointsLayerId, query, CancellationToken.None);
            gmlResult.Items.Should().HaveCount(1);
            gmlResult.Items[0].Attributes.Should().ContainKey("objectid");
        }
        finally
        {
            await _fixture.ExecuteAsync($"""
                DELETE FROM {_testSchema}.features WHERE objectid = {objectId};
                ALTER TABLE {_testSchema}.features ALTER COLUMN attributes SET NOT NULL;
                """);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    public async Task QueryGmlAsync_WithNorthEastAxisOrder_UsesLatitudeLongitudeCoordinates()
    {
        var store = CreateFeatureStore();
        var query = new FeatureQuery
        {
            ObjectIds = ImmutableArray.Create(1L),
            SpatialReferenceSrid = 4326,
            OutputSrid = 4326,
            OutputAxisOrder = AxisOrder.NorthEast
        };

        var result = await store.QueryGmlAsync(PointsLayerId, query, CancellationToken.None);

        result.Items.Should().HaveCount(1);
        result.Items[0].GeometryGml.Should().Contain("37 -122");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    public async Task QueryGmlAsync_WithEastNorthAxisOrder_UsesLongitudeLatitudeCoordinates()
    {
        var store = CreateFeatureStore();
        var query = new FeatureQuery
        {
            ObjectIds = ImmutableArray.Create(1L),
            SpatialReferenceSrid = 4326,
            OutputSrid = 4326,
            OutputAxisOrder = AxisOrder.EastNorth
        };

        var result = await store.QueryGmlAsync(PointsLayerId, query, CancellationToken.None);

        result.Items.Should().HaveCount(1);
        result.Items[0].GeometryGml.Should().Contain("-122 37");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    public async Task QueryProjectedPointsAsync_WithRasterGrid_ShouldThinDensePointRows()
    {
        var store = CreateFeatureStore();
        var rasterPointReader = store.Should().BeAssignableTo<IRasterPointReader>().Subject;
        var query = new FeatureQuery
        {
            SpatialReferenceSrid = 4326,
            OutputSrid = 4326,
            Limit = 100,
            SpatialFilter = SpatialFilter.Create(
                CreatePolygonWkb("POLYGON((-122.1 36.9, -120.8 36.9, -120.8 38.2, -122.1 38.2, -122.1 36.9))"),
                SpatialRelationship.Intersects,
                4326),
            RasterPointGrid = new RasterPointGrid
            {
                OriginX = -122.1,
                OriginY = 38.2,
                CellWidth = 0.25,
                CellHeight = 0.25
            }
        };

        var points = await rasterPointReader.QueryProjectedPointsAsync(PointsLayerId, query, CancellationToken.None);

        points.Should().NotBeEmpty();
        points.Length.Should().BeLessThan(100);
        points.Should().OnlyContain(point =>
            point.X >= -122.1 && point.X <= -120.8 &&
            point.Y >= 36.9 && point.Y <= 38.2);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    public async Task QueryProjectedPointsAsync_WithWebMercatorOutput_ProjectsCoordinatesAccurately()
    {
        var store = CreateFeatureStore();
        var rasterPointReader = store.Should().BeAssignableTo<IRasterPointReader>().Subject;
        var query = new FeatureQuery
        {
            ObjectIds = ImmutableArray.Create(1L),
            SpatialReferenceSrid = 4326,
            OutputSrid = 3857,
            Limit = 10,
            RasterPointGrid = new RasterPointGrid
            {
                OriginX = -20037508.342789244,
                OriginY = 20037508.342789244,
                CellWidth = 500000d,
                CellHeight = 500000d
            }
        };

        var points = await rasterPointReader.QueryProjectedPointsAsync(PointsLayerId, query, CancellationToken.None);
        var expected = CoordinateTransformer.TransformPoint(-122d, 37d, 4326, 3857);

        points.Should().ContainSingle();
        points[0].X.Should().BeApproximately(expected.X, 0.001);
        points[0].Y.Should().BeApproximately(expected.Y, 0.001);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    public async Task QueryGeoJsonAsync_WithObjectIdFilter_ReturnsGeoJsonGeometry()
    {
        var store = CreateFeatureStore();
        var query = new FeatureQuery
        {
            ObjectIds = ImmutableArray.Create(1L),
            SpatialReferenceSrid = 4326,
            OutputSrid = 4326
        };

        var result = await ((IGeoJsonFeatureStore)store).QueryGeoJsonAsync(PointsLayerId, query, CancellationToken.None);

        result.Items.Should().HaveCount(1);
        result.Items[0].GeometryGeoJson.Should().Contain("\"type\":\"Point\"");
        result.Items[0].Attributes.Should().ContainKey("objectid");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    public async Task QueryKmlAsync_WithObjectIdFilter_ReturnsKmlGeometry()
    {
        var store = CreateFeatureStore();
        var query = new FeatureQuery
        {
            ObjectIds = ImmutableArray.Create(1L),
            SpatialReferenceSrid = 4326,
            OutputSrid = 4326
        };

        var result = await ((IKmlFeatureStore)store).QueryKmlAsync(PointsLayerId, query, CancellationToken.None);

        result.Items.Should().HaveCount(1);
        result.Items[0].GeometryKml.Should().Contain("<Point>");
        result.Items[0].GeometryKml.Should().Contain("<coordinates>");
        result.Items[0].Attributes.Should().ContainKey("objectid");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    public async Task QueryGeobufAsync_WithObjectIdFilter_ReturnsPayload()
    {
        var store = CreateFeatureStore();
        var query = new FeatureQuery
        {
            ObjectIds = ImmutableArray.Create(1L),
            SpatialReferenceSrid = 4326,
            OutputSrid = 4326
        };

        var payload = await ((IGeobufFeatureStore)store).QueryGeobufAsync(PointsLayerId, query, CancellationToken.None);

        payload.Should().NotBeNull();
        payload.Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
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
    [Operation(Operations.Query)]
    public async Task Query_WithSpatialFilter_ShouldReturnFeaturesInBounds()
    {
        // Arrange
        var store = CreateFeatureStore();
        var envelope = new Envelope(-122.0, -121.9, 37.0, 37.1);
        var geometry = _geometryFactory.ToGeometry(envelope);
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
    [Operation(Operations.Query)]
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
    [Operation(Operations.GetById)]
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
    [Operation(Operations.GetById)]
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
    [Operation(Operations.Create)]
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
    [Operation(Operations.Update)]
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
    [Operation(Operations.Delete)]
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
    [Operation(Operations.ApplyEdits)]
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
    [Operation(Operations.ApplyEdits)]
    public async Task ApplyEdits_WithMultipleCreates_ReturnsCreatedIdsInRequestOrder()
    {
        var store = CreateFeatureStore();
        var firstFeature = Feature.Create(
            0,
            CreatePointWkb(-5, 5),
            ImmutableDictionary<string, object?>.Empty
                .Add("name", "first-batch-create")
                .Add("globalId", "batch-create-1"));
        var secondFeature = Feature.Create(
            0,
            CreatePointWkb(-6, 6),
            ImmutableDictionary<string, object?>.Empty
                .Add("name", "second-batch-create")
                .Add("globalId", "batch-create-2"));

        var result = await store.ApplyEditsAsync(
            GetLayerId("points"),
            FeatureEditBatch.Create(
                creates: ImmutableArray.Create(firstFeature, secondFeature),
                rollbackOnFailure: false),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.CreatedCount.Should().Be(2);
        result.CreatedIds.Should().HaveCount(2);
        result.CreateResults.Should().HaveCount(2);
        result.CreateResults[0].GlobalId.Should().Be("batch-create-1");
        result.CreateResults[1].GlobalId.Should().Be("batch-create-2");
        result.CreatedIds[0].Should().NotBe(result.CreatedIds[1]);

        var firstCreated = await store.GetAsync(GetLayerId("points"), result.CreatedIds[0], CancellationToken.None);
        var secondCreated = await store.GetAsync(GetLayerId("points"), result.CreatedIds[1], CancellationToken.None);

        firstCreated.Should().NotBeNull();
        secondCreated.Should().NotBeNull();
        firstCreated!.Value.Attributes["name"].Should().Be("first-batch-create");
        secondCreated!.Value.Attributes["name"].Should().Be("second-batch-create");
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    public async Task ApplyEdits_WithOrderedOperations_PreservesRequestOrderWithinRollbackTransaction()
    {
        var store = CreateFeatureStore();
        var existingFeature = await store.GetAsync(GetLayerId("points"), 1, CancellationToken.None);
        existingFeature.Should().NotBeNull();

        var updatedFeature = Feature.Create(
            existingFeature!.Value.Id,
            existingFeature.Value.Geometry,
            existingFeature.Value.Attributes.SetItem("category", "ordered-update"));

        var result = await store.ApplyEditsAsync(
            GetLayerId("points"),
            FeatureEditBatch.Create(
                rollbackOnFailure: true,
                operations: ImmutableArray.Create(
                    FeatureEditOperation.Delete(existingFeature.Value.Id),
                    FeatureEditOperation.Update(updatedFeature))),
            CancellationToken.None);

        result.WasRolledBack.Should().BeTrue();
        result.DeleteResults.Should().ContainSingle();
        result.UpdateResults.Should().ContainSingle();
        result.DeleteResults[0].ErrorMessage.Should().Be("Operation rolled back.");
        result.UpdateResults[0].ErrorMessage.Should().Be("Feature not found.");

        var restoredFeature = await store.GetAsync(GetLayerId("points"), existingFeature.Value.Id, CancellationToken.None);
        restoredFeature.Should().NotBeNull();
        restoredFeature!.Value.Attributes["category"].Should().Be(existingFeature.Value.Attributes["category"]);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
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
    [Operation(Operations.Query)]
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
    [Operation(Operations.Query)]
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

    private PostgresFeatureStoreRefactored CreateFeatureStore()
    {
        var connectionProvider = new TestDatabaseConnectionProvider(_fixture.DataSource, () => _testSchema);
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new Microsoft.Extensions.ObjectPool.StringBuilderPooledObjectPolicy());
        var dictionaryPool = poolProvider.Create(new DictionaryPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        var cacheManager = new FeatureCacheManager(connectionProvider, NullLogger<FeatureCacheManager>.Instance, _testSchema);
        var queryBuilder = new FeatureQueryBuilder(stringBuilderPool, geometryProcessor, _testSchema);
        var dataAccess = new FeatureDataAccess(new FeatureDataAccessDependencies(
            connectionProvider,
            geometryProcessor,
            cacheManager,
            dictionaryPool,
            StatementCache: null,
            Logger: NullLogger<FeatureDataAccess>.Instance,
            PerformanceOptions: null,
            LimitsOptions: null,
            PerformanceMonitor: null,
            SchemaName: _testSchema));

        return new PostgresFeatureStoreRefactored(queryBuilder, dataAccess, cacheManager, CreateLayerCatalog());
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
        var point = _geometryFactory.CreatePoint(new Coordinate(lon, lat));
        return new WKBWriter().Write(point);
    }

    private static byte[] CreateLineStringWkb(IEnumerable<(double lon, double lat)> coordinates)
    {
        var coords = coordinates.Select(c => new Coordinate(c.lon, c.lat)).ToArray();
        var line = _geometryFactory.CreateLineString(coords);
        return new WKBWriter().Write(line);
    }

    private static byte[] CreatePolygonWkb(string wkt)
    {
        var polygon = new WKTReader(NtsGeometryServices.Instance).Read(wkt);
        polygon.SRID = _geometryFactory.SRID;
        return new WKBWriter().Write(polygon);
    }

    private static StubLayerCatalog CreateLayerCatalog()
    {
        var spatialReference = SpatialReference.Create(4326);

        var layers = new Dictionary<int, LayerDefinition>
        {
            [PointsLayerId] = new LayerDefinition(
                PointsLayerId,
                "Test Points",
                null,
                Honua.Core.Features.Catalog.Domain.GeometryType.Point,
                spatialReference,
                [
                    new FieldDefinition("objectid", FieldType.Integer, Nullable: false),
                    new FieldDefinition("shape", FieldType.Geometry),
                    new FieldDefinition("category", FieldType.String, Length: 64),
                    new FieldDefinition("value", FieldType.Integer),
                    new FieldDefinition("active", FieldType.Boolean),
                    new FieldDefinition("created_date", FieldType.DateTime)
                ]),
            [PolygonsLayerId] = new LayerDefinition(
                PolygonsLayerId,
                "Test Polygons",
                null,
                Honua.Core.Features.Catalog.Domain.GeometryType.Polygon,
                spatialReference,
                [
                    new FieldDefinition("objectid", FieldType.Integer, Nullable: false),
                    new FieldDefinition("shape", FieldType.Geometry),
                    new FieldDefinition("area_name", FieldType.String, Length: 128),
                    new FieldDefinition("population", FieldType.Integer),
                    new FieldDefinition("density", FieldType.Double)
                ]),
            [LinesLayerId] = new LayerDefinition(
                LinesLayerId,
                "Test Lines",
                null,
                Honua.Core.Features.Catalog.Domain.GeometryType.LineString,
                spatialReference,
                [
                    new FieldDefinition("objectid", FieldType.Integer, Nullable: false),
                    new FieldDefinition("shape", FieldType.Geometry),
                    new FieldDefinition("road_type", FieldType.String, Length: 64),
                    new FieldDefinition("length_km", FieldType.Double)
                ])
        };

        return new StubLayerCatalog(layers);
    }

    private sealed class StubLayerCatalog(Dictionary<int, LayerDefinition> layers) : ILayerCatalog
    {
        public Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(layers.TryGetValue(layerId, out var layer) ? layer : null);

        public Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(layers.Values.ToArray());

        public Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
            => Task.FromResult<ServiceDefinition?>(null);

        public Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<ServiceDefinition>());

        public Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(layers.ContainsKey(layerId));

        public Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<Relationship?> GetRelationshipAsync(int layerId, int relationshipId, CancellationToken cancellationToken = default)
            => Task.FromResult<Relationship?>(null);

        public Task<Relationship[]> ListRelationshipsAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<Relationship>());
    }
}

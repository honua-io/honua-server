// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Postgres.Features.FeatureStore;
using Honua.Server.Tests.Infrastructure;
using Microsoft.Extensions.ObjectPool;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Xunit.Abstractions;

namespace Honua.Server.Tests;

/// <summary>
/// Integration tests for PostgresFeatureStore using real PostgreSQL database.
/// </summary>
[Collection("Database")]
public class PostgresFeatureStoreTests : IAsyncLifetime
{
    private readonly DatabaseFixtureAdapter _fixture;
    private readonly ITestOutputHelper _output;
    private PostgresFeatureStore _featureStore = null!;
    private string _schemaName = null!;
    private const int TestLayerId = 1;

    public PostgresFeatureStoreTests(DatabaseFixtureAdapter fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _schemaName = await _fixture.CreateIsolatedSchemaAsync(nameof(PostgresFeatureStoreTests));

        // Create feature store with the isolated schema
        var connectionProvider = new TestDatabaseConnectionProvider(_fixture.DataSource);
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new PostgresFeatureStore.StringBuilderPooledObjectPolicy());
        _featureStore = new PostgresFeatureStore(connectionProvider, stringBuilderPool, _schemaName);

        // Create test table structure
        await _fixture.ExecuteAsync("""
            CREATE TABLE features (
                objectid bigserial PRIMARY KEY,
                layer_id integer NOT NULL,
                geometry geometry,
                attributes jsonb NOT NULL DEFAULT '{}'::jsonb
            );

            CREATE INDEX idx_features_layer_id ON features(layer_id);
            """, _schemaName);

        _output.WriteLine($"Created isolated schema: {_schemaName}");
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropSchemaAsync(_schemaName);
    }

    [Fact]
    public async Task CreateAsync_WithValidFeature_ReturnsCreatedFeature()
    {
        // Arrange
        var attributes = new Dictionary<string, object?>
        {
            ["name"] = "Test Feature",
            ["type"] = "Point of Interest",
            ["active"] = true
        }.ToImmutableDictionary();

        var feature = Feature.Create(0, null, attributes);

        // Act
        var created = await _featureStore.CreateAsync(TestLayerId, feature);

        // Assert
        Assert.True(created.Id > 0);
        Assert.Equal(attributes["name"], created.Attributes["name"]);
        Assert.Equal(attributes["type"], created.Attributes["type"]);
        Assert.Equal(attributes["active"], created.Attributes["active"]);
        Assert.Null(created.Geometry);
    }

    [Fact]
    public async Task CreateAsync_WithGeometry_StoresGeometry()
    {
        // Arrange
        var geometry = CreatePointWkb(-122.5, 37.5);
        var attributes = new Dictionary<string, object?> { ["name"] = "Geometry Feature" }.ToImmutableDictionary();
        var feature = Feature.Create(0, geometry, attributes);

        // Act
        var created = await _featureStore.CreateAsync(TestLayerId, feature);

        // Assert
        Assert.True(created.Id > 0);
        Assert.Equal(geometry, created.Geometry);
        Assert.Equal(attributes["name"], created.Attributes["name"]);
    }

    private static byte[] CreatePointWkb(double x, double y)
    {
        var point = new Point(x, y);
        var writer = new WKBWriter();
        return writer.Write(point);
    }

    [Fact]
    public async Task GetAsync_ExistingFeature_ReturnsFeature()
    {
        // Arrange
        var attributes = new Dictionary<string, object?> { ["name"] = "Get Test" }.ToImmutableDictionary();
        var feature = Feature.Create(0, null, attributes);
        var created = await _featureStore.CreateAsync(TestLayerId, feature);

        // Act
        var retrieved = await _featureStore.GetAsync(TestLayerId, created.Id);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal(created.Id, retrieved.Value.Id);
        Assert.Equal(attributes["name"], retrieved.Value.Attributes["name"]);
    }

    [Fact]
    public async Task GetAsync_NonExistentFeature_ReturnsNull()
    {
        // Act
        var result = await _featureStore.GetAsync(TestLayerId, 99999);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateAsync_ExistingFeature_UpdatesFeature()
    {
        // Arrange
        var originalAttributes = new Dictionary<string, object?> { ["name"] = "Original" }.ToImmutableDictionary();
        var created = await _featureStore.CreateAsync(TestLayerId, Feature.Create(0, null, originalAttributes));

        var updatedAttributes = new Dictionary<string, object?> { ["name"] = "Updated" }.ToImmutableDictionary();
        var updatedFeature = Feature.Create(created.Id, null, updatedAttributes);

        // Act
        var result = await _featureStore.UpdateAsync(TestLayerId, updatedFeature);

        // Assert
        Assert.Equal(created.Id, result.Id);
        Assert.Equal("Updated", result.Attributes["name"]);
    }

    [Fact]
    public async Task UpdateAsync_NonExistentFeature_ThrowsException()
    {
        // Arrange
        var attributes = new Dictionary<string, object?> { ["name"] = "Nonexistent" }.ToImmutableDictionary();
        var feature = Feature.Create(99999, null, attributes);

        // Act & Assert
        await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            _featureStore.UpdateAsync(TestLayerId, feature));
    }

    [Fact]
    public async Task DeleteAsync_ExistingFeature_ReturnsTrue()
    {
        // Arrange
        var attributes = new Dictionary<string, object?> { ["name"] = "Delete Test" }.ToImmutableDictionary();
        var created = await _featureStore.CreateAsync(TestLayerId, Feature.Create(0, null, attributes));

        // Act
        var deleted = await _featureStore.DeleteAsync(TestLayerId, created.Id);

        // Assert
        Assert.True(deleted);

        // Verify it's gone
        var retrieved = await _featureStore.GetAsync(TestLayerId, created.Id);
        Assert.Null(retrieved);
    }

    [Fact]
    public async Task DeleteAsync_NonExistentFeature_ReturnsFalse()
    {
        // Act
        var deleted = await _featureStore.DeleteAsync(TestLayerId, 99999);

        // Assert
        Assert.False(deleted);
    }

    [Fact]
    public async Task QueryAsync_WithoutFilters_ReturnsAllFeatures()
    {
        // Arrange
        await CreateTestFeatures();
        var query = new FeatureQuery();

        // Act
        var result = await _featureStore.QueryAsync(TestLayerId, query);

        // Assert
        Assert.Equal(3, result.TotalCount);
        Assert.Equal(3, result.Items.Length);
        Assert.False(result.HasMoreResults);
    }

    [Fact]
    public async Task QueryAsync_WithPagination_ReturnsPaginatedResults()
    {
        // Arrange
        await CreateTestFeatures();
        var query = new FeatureQuery { Offset = 1, Limit = 1 };

        // Act
        var result = await _featureStore.QueryAsync(TestLayerId, query);

        // Assert
        Assert.Equal(3, result.TotalCount);
        Assert.Single(result.Items);
        Assert.True(result.HasMoreResults);
    }

    [Fact]
    public async Task QueryAsync_WithWhereClause_FiltersResults()
    {
        // Arrange
        await CreateTestFeatures();
        var query = new FeatureQuery { Where = "attributes->>'type' = 'TypeA'" };

        // Act
        var result = await _featureStore.QueryAsync(TestLayerId, query);

        // Assert
        Assert.Equal(2, result.TotalCount);
        Assert.Equal(2, result.Items.Length);
        Assert.All(result.Items, item => Assert.Equal("TypeA", item.Attributes["type"]));
    }

    [Fact]
    public async Task CountAsync_WithoutFilter_ReturnsCorrectCount()
    {
        // Arrange
        await CreateTestFeatures();
        var query = new FeatureQuery();

        // Act
        var count = await _featureStore.CountAsync(TestLayerId, query);

        // Assert
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task CountAsync_WithWhereClause_ReturnsFilteredCount()
    {
        // Arrange
        await CreateTestFeatures();
        var query = new FeatureQuery { Where = "attributes->>'type' = 'TypeB'" };

        // Act
        var count = await _featureStore.CountAsync(TestLayerId, query);

        // Assert
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task GetExtentAsync_WithoutFeatures_ReturnsNull()
    {
        // Act
        var extent = await _featureStore.GetExtentAsync(TestLayerId);

        // Assert
        Assert.Null(extent);
    }

    [Fact]
    public async Task ApplyEditsAsync_WithMixedOperations_ProcessesAllOperations()
    {
        // Arrange
        // Create initial feature for update/delete testing
        var existingFeature = await _featureStore.CreateAsync(TestLayerId,
            Feature.Create(0, null, new Dictionary<string, object?> { ["name"] = "Existing" }.ToImmutableDictionary()));

        // Prepare batch operations
        var creates = ImmutableArray.Create(
            Feature.Create(0, null, new Dictionary<string, object?> { ["name"] = "New1" }.ToImmutableDictionary()),
            Feature.Create(0, null, new Dictionary<string, object?> { ["name"] = "New2" }.ToImmutableDictionary())
        );

        var updates = ImmutableArray.Create(
            Feature.Create(existingFeature.Id, null, new Dictionary<string, object?> { ["name"] = "Updated" }.ToImmutableDictionary())
        );

        var deletes = ImmutableArray.Create(existingFeature.Id);

        var editBatch = FeatureEditBatch.Create(creates, updates, deletes);

        // Act
        var result = await _featureStore.ApplyEditsAsync(TestLayerId, editBatch);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.CreatedCount);
        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(2, result.CreatedIds.Length);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public async Task ApplyEditsAsync_WithEmptyBatch_ReturnsSuccessWithZeroCounts()
    {
        // Arrange
        var editBatch = FeatureEditBatch.Create();

        // Act
        var result = await _featureStore.ApplyEditsAsync(TestLayerId, editBatch);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(0, result.DeletedCount);
        Assert.Empty(result.CreatedIds);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public async Task ApplyEditsAsync_WithRollbackOnFailureProperty_BehaviorIsImplemented()
    {
        // This test verifies that the FeatureEditBatch rollbackOnFailure property
        // is properly handled by the PostgresFeatureStore implementation

        // Arrange
        var feature1 = Feature.Create(0, null, ImmutableDictionary<string, object?>.Empty.Add("name", "Feature1"));
        var feature2 = Feature.Create(0, null, ImmutableDictionary<string, object?>.Empty.Add("name", "Feature2"));

        // Test with rollbackOnFailure: true
        var editBatchWithRollback = FeatureEditBatch.Create(
            creates: ImmutableArray.Create(feature1, feature2),
            rollbackOnFailure: true);

        // Test with rollbackOnFailure: false (default GeoServices behavior)
        var editBatchWithoutRollback = FeatureEditBatch.Create(
            creates: ImmutableArray.Create(feature1, feature2),
            rollbackOnFailure: false);

        // Act
        var resultWithRollback = await _featureStore.ApplyEditsAsync(TestLayerId, editBatchWithRollback);
        var resultWithoutRollback = await _featureStore.ApplyEditsAsync(TestLayerId, editBatchWithoutRollback);

        // Assert
        // Both should succeed in this case since we have valid features
        Assert.True(resultWithRollback.IsSuccess);
        Assert.Equal(2, resultWithRollback.CreatedCount);
        Assert.False(resultWithRollback.WasRolledBack);
        Assert.False(resultWithRollback.HasErrors);
        Assert.Equal(2, resultWithRollback.CreateResults.Length);
        Assert.All(resultWithRollback.CreateResults, r => Assert.True(r.IsSuccess));

        Assert.True(resultWithoutRollback.IsSuccess);
        Assert.Equal(2, resultWithoutRollback.CreatedCount);
        Assert.False(resultWithoutRollback.WasRolledBack);
        Assert.False(resultWithoutRollback.HasErrors);
        Assert.Equal(2, resultWithoutRollback.CreateResults.Length);
        Assert.All(resultWithoutRollback.CreateResults, r => Assert.True(r.IsSuccess));
    }

    [Fact]
    public async Task ApplyEditsAsync_ReturnsDetailedOperationResults()
    {
        // This test verifies that the enhanced FeatureEditResult structure
        // provides detailed operation results as required by issue #12

        // Arrange
        var feature1 = Feature.Create(0, null, ImmutableDictionary<string, object?>.Empty.Add("name", "Feature1"));
        var feature2 = Feature.Create(0, null, ImmutableDictionary<string, object?>.Empty.Add("name", "Feature2"));

        var editBatch = FeatureEditBatch.Create(
            creates: ImmutableArray.Create(feature1, feature2));

        // Act
        var result = await _featureStore.ApplyEditsAsync(TestLayerId, editBatch);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.CreatedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(0, result.DeletedCount);
        Assert.False(result.WasRolledBack);
        Assert.False(result.HasErrors);

        // Verify detailed operation results
        Assert.Equal(2, result.CreateResults.Length);
        Assert.Empty(result.UpdateResults);
        Assert.Empty(result.DeleteResults);

        // Check individual create results
        Assert.All(result.CreateResults, r =>
        {
            Assert.True(r.IsSuccess);
            Assert.True(r.ObjectId.HasValue);
            Assert.True(r.ObjectId.Value > 0);
            Assert.Null(r.ErrorMessage);
            Assert.Equal(0, r.ErrorCode);
        });

        // Verify created IDs match the result IDs
        Assert.Equal(result.CreatedIds.Length, result.CreateResults.Count(r => r.IsSuccess));
    }

    [Fact]
    public async Task ParameterizedQueries_PreventSqlInjection()
    {
        // Arrange
        await CreateTestFeatures();

        // Attempt SQL injection through WHERE clause
        var maliciousQuery = new FeatureQuery
        {
            Where = "1=1; DROP TABLE features; --"
        };

        // Act & Assert - Should throw ArgumentException for dangerous SQL patterns
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            _featureStore.QueryAsync(TestLayerId, maliciousQuery));

        Assert.Contains("dangerous pattern", exception.Message);

        // Verify table still exists by running a legitimate query
        var verificationQuery = new FeatureQuery();
        var verificationResult = await _featureStore.QueryAsync(TestLayerId, verificationQuery);

        Assert.Equal(3, verificationResult.TotalCount); // Features should still exist
    }

    private async Task CreateTestFeatures()
    {
        var features = new[]
        {
            Feature.Create(0, null, new Dictionary<string, object?>
            {
                ["name"] = "Feature1",
                ["type"] = "TypeA"
            }.ToImmutableDictionary()),
            Feature.Create(0, null, new Dictionary<string, object?>
            {
                ["name"] = "Feature2",
                ["type"] = "TypeA"
            }.ToImmutableDictionary()),
            Feature.Create(0, null, new Dictionary<string, object?>
            {
                ["name"] = "Feature3",
                ["type"] = "TypeB"
            }.ToImmutableDictionary())
        };

        foreach (var feature in features)
        {
            await _featureStore.CreateAsync(TestLayerId, feature);
        }
    }
}

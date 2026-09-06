// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Exceptions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Postgres.Features.FeatureStore;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Server.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Xunit.Abstractions;

namespace Honua.Server.Tests;

/// <summary>
/// Integration tests for PostgresFeatureStore using real PostgreSQL database.
/// </summary>
[Collection("Database.CoreFeatureStore")]
public class PostgresFeatureStoreTests : IAsyncLifetime
{
    private readonly DatabaseFixtureAdapter _fixture;
    private readonly ITestOutputHelper _output;
    private PostgresFeatureStoreRefactored _featureStore = null!;
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
        var connectionProvider = new TestDatabaseConnectionProvider(_fixture.DataSource, () => _schemaName);
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new Microsoft.Extensions.ObjectPool.StringBuilderPooledObjectPolicy());
        var dictionaryPool = poolProvider.Create(new DictionaryPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        var cacheManager = new FeatureCacheManager(connectionProvider, NullLogger<FeatureCacheManager>.Instance, _schemaName);
        var queryBuilder = new FeatureQueryBuilder(stringBuilderPool, geometryProcessor, _schemaName);
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
            SchemaName: _schemaName));
        _featureStore = new PostgresFeatureStoreRefactored(queryBuilder, dataAccess, cacheManager);

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
        Assert.Equal(created.Id, retrieved!.Value.Id);
        Assert.Equal(attributes["name"], retrieved!.Value.Attributes["name"]);
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

    /// <summary>
    /// honua-server#4406: the previous body of this test carried two <em>valid</em> creates on both
    /// paths and asserted <c>WasRolledBack == false</c> twice, so it was a happy-path create test
    /// wearing a rollback name — a genuinely broken rollback could not have failed it. It now
    /// includes an operation that must fail (an update of an object id that does not exist), so
    /// the two <c>rollbackOnFailure</c> paths are distinguished by what they leave in the database:
    /// all-or-nothing discards the sibling create, partial-commit keeps it.
    /// </summary>
    [Fact]
    public async Task ApplyEditsAsync_WithRollbackOnFailure_DiscardsTheSiblingCreate()
    {
        const long missingObjectId = 987_654_321;
        var create = Feature.Create(0, null, ImmutableDictionary<string, object?>.Empty.Add("name", "rollback-sibling"));
        var doomedUpdate = Feature.Create(
            missingObjectId,
            null,
            ImmutableDictionary<string, object?>.Empty.Add("name", "rollback-doomed"));

        var result = await _featureStore.ApplyEditsAsync(
            TestLayerId,
            FeatureEditBatch.Create(
                creates: ImmutableArray.Create(create),
                updates: ImmutableArray.Create(doomedUpdate),
                rollbackOnFailure: true));

        Assert.True(result.WasRolledBack);
        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Single(result.UpdateResults);
        Assert.False(result.UpdateResults[0].IsSuccess);

        // The proof the old test could not make: the valid sibling create must not be in the table.
        var survivors = await _featureStore.QueryAsync(
            TestLayerId,
            new FeatureQuery { Where = "attributes->>'name' = 'rollback-sibling'" });
        Assert.Empty(survivors.Items);
    }

    /// <summary>
    /// The contrasting half of <see cref="ApplyEditsAsync_WithRollbackOnFailure_DiscardsTheSiblingCreate"/>:
    /// with <c>rollbackOnFailure=false</c> the same batch commits the create the failing update
    /// would otherwise have discarded. Together the two tests prove the flag changes behaviour.
    /// </summary>
    [Fact]
    public async Task ApplyEditsAsync_WithoutRollbackOnFailure_KeepsTheSiblingCreate()
    {
        const long missingObjectId = 987_654_322;
        var create = Feature.Create(0, null, ImmutableDictionary<string, object?>.Empty.Add("name", "partial-sibling"));
        var doomedUpdate = Feature.Create(
            missingObjectId,
            null,
            ImmutableDictionary<string, object?>.Empty.Add("name", "partial-doomed"));

        var result = await _featureStore.ApplyEditsAsync(
            TestLayerId,
            FeatureEditBatch.Create(
                creates: ImmutableArray.Create(create),
                updates: ImmutableArray.Create(doomedUpdate),
                rollbackOnFailure: false));

        Assert.False(result.WasRolledBack);
        Assert.Equal(1, result.CreatedCount);
        Assert.Single(result.UpdateResults);
        Assert.False(result.UpdateResults[0].IsSuccess);

        var survivors = await _featureStore.QueryAsync(
            TestLayerId,
            new FeatureQuery { Where = "attributes->>'name' = 'partial-sibling'" });
        Assert.Single(survivors.Items);
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

        var isSqlInjectionRejectionMessage =
            exception.Message.Contains("dangerous pattern", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("unsupported expression", StringComparison.OrdinalIgnoreCase);
        Assert.True(
            isSqlInjectionRejectionMessage,
            $"Expected SQL-injection rejection message but got: {exception.Message}");

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

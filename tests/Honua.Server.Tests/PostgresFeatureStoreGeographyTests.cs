// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Postgres.Features.FeatureStore;
using Honua.Postgres.Features.FeatureStore.Services;
using Honua.Server.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using FeatureStoreStringBuilderPooledObjectPolicy = Honua.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy;

namespace Honua.Server.Tests;

[Collection("Database")]
public sealed class PostgresFeatureStoreGeographyTests : IAsyncLifetime
{
    private readonly DatabaseFixtureAdapter _fixture;
    private PostgresFeatureStoreRefactored _featureStore = null!;
    private string _schemaName = null!;
    private const int TestLayerId = 1;
    private long _originId;
    private long _offsetId;
    private long _datelineEastId;
    private long _datelineWestId;

    public PostgresFeatureStoreGeographyTests(DatabaseFixtureAdapter fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _schemaName = await _fixture.CreateIsolatedSchemaAsync(nameof(PostgresFeatureStoreGeographyTests));

        var connectionProvider = new TestDatabaseConnectionProvider(_fixture.DataSource, () => _schemaName);
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var dictionaryPool = poolProvider.Create(new DictionaryPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        var cacheManager = new FeatureCacheManager(connectionProvider, NullLogger<FeatureCacheManager>.Instance, _schemaName);
        var queryBuilder = new FeatureQueryBuilder(stringBuilderPool, geometryProcessor, _schemaName);
        var dataAccess = new FeatureDataAccess(new FeatureDataAccessDependencies(
            connectionProvider,
            geometryProcessor,
            cacheManager,
            dictionaryPool,
            statementCache: null,
            logger: NullLogger<FeatureDataAccess>.Instance,
            performanceOptions: null,
            limitsOptions: null,
            performanceMonitor: null,
            schemaName: _schemaName));
        _featureStore = new PostgresFeatureStoreRefactored(queryBuilder, dataAccess, cacheManager);

        await _fixture.ExecuteAsync("""
            DROP TABLE IF EXISTS features;

            CREATE TABLE features (
                objectid bigserial PRIMARY KEY,
                layer_id integer NOT NULL,
                geometry geography(Point, 4326),
                attributes jsonb NOT NULL DEFAULT '{}'::jsonb
            );

            CREATE INDEX idx_features_layer_id ON features(layer_id);
            """, _schemaName);

        _originId = (await _featureStore.CreateAsync(
            TestLayerId,
            Feature.Create(0, CreatePointWkb(0, 0), CreateAttributes("origin")))).Id;
        _offsetId = (await _featureStore.CreateAsync(
            TestLayerId,
            Feature.Create(0, CreatePointWkb(0.01, 0), CreateAttributes("offset")))).Id;
        _datelineEastId = (await _featureStore.CreateAsync(
            TestLayerId,
            Feature.Create(0, CreatePointWkb(179.9, 0), CreateAttributes("dateline-east")))).Id;
        _datelineWestId = (await _featureStore.CreateAsync(
            TestLayerId,
            Feature.Create(0, CreatePointWkb(-179.9, 0), CreateAttributes("dateline-west")))).Id;
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropSchemaAsync(_schemaName);
    }

    [Fact]
    public async Task QueryAsync_WithGeographyWithinDistance_UsesMeters()
    {
        var query = new FeatureQuery
        {
            SpatialReferenceSrid = 4326,
            SpatialFilter = SpatialFilter.CreateDistanceFilter(
                CreatePointWkb(0, 0),
                distance: 1000,
                unit: DistanceUnit.Meters,
                withinDistance: true,
                srid: 4326)
        };

        var result = await _featureStore.QueryAsync(TestLayerId, query);

        Assert.Single(result.Items);
        Assert.Equal(_originId, result.Items[0].Id);
    }

    [Fact]
    public async Task QueryAsync_WithGeographyKnn_ReturnsGeodesicOrderAndDistance()
    {
        var query = new FeatureQuery
        {
            SpatialReferenceSrid = 4326,
            SpatialFilter = SpatialFilter.CreateKnnFilter(
                CreatePointWkb(0, 0),
                count: 2,
                returnDistance: true,
                srid: 4326)
        };

        var result = await _featureStore.QueryAsync(TestLayerId, query);

        Assert.Equal(2, result.Items.Length);
        Assert.Equal(_originId, result.Items[0].Id);
        Assert.Equal(_offsetId, result.Items[1].Id);

        var firstDistance = (double)result.Items[0].Attributes["distance"]!;
        var secondDistance = (double)result.Items[1].Attributes["distance"]!;

        Assert.InRange(firstDistance, 0, 1);
        Assert.InRange(secondDistance, 1000, 1300);
    }

    [Fact]
    public async Task QueryAsync_WithGeographyWithinDistance_AcrossDateline_ReturnsBothSides()
    {
        var query = new FeatureQuery
        {
            SpatialReferenceSrid = 4326,
            SpatialFilter = SpatialFilter.CreateDistanceFilter(
                CreatePointWkb(179.9, 0),
                distance: 30000,
                unit: DistanceUnit.Meters,
                withinDistance: true,
                srid: 4326)
        };

        var result = await _featureStore.QueryAsync(TestLayerId, query);
        var ids = result.Items.Select(feature => feature.Id).ToArray();

        Assert.Equal(2, ids.Length);
        Assert.Contains(_datelineEastId, ids);
        Assert.Contains(_datelineWestId, ids);
    }

    private static ImmutableDictionary<string, object?> CreateAttributes(string name)
        => new Dictionary<string, object?> { ["name"] = name }.ToImmutableDictionary();

    private static byte[] CreatePointWkb(double x, double y)
    {
        var point = new Point(x, y);
        var writer = new WKBWriter();
        return writer.Write(point);
    }
}

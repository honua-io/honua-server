// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Tiles;
using Honua.Postgres.Features.FeatureStore.Services;
using Microsoft.Extensions.ObjectPool;
using FeatureStoreStringBuilderPooledObjectPolicy = Honua.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy;

namespace Honua.Postgres.Tests.Features.FeatureStore;

public sealed class FeatureQueryBuilderSpatialFilterParameterTests
{
    [Fact]
    public void BuildSelectGeoServicesPointQuery_WithDisplayPointEnvelope_UsesEnvelopeOnlyPredicate()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        var queryBuilder = new FeatureQueryBuilder(stringBuilderPool, geometryProcessor);

        var query = new FeatureQuery
        {
            GeometryType = MetadataV2GeometryType.Point,
            SpatialReferenceSrid = 4326,
            SpatialFilter = SpatialFilter.Create(
                [1, 2, 3, 4],
                SpatialRelationship.Intersects,
                srid: 4326,
                isSimpleEnvelope: true,
                allowEnvelopeOnly: true,
                envelopeMinX: 1,
                envelopeMinY: 2,
                envelopeMaxX: 3,
                envelopeMaxY: 4)
        };

        var result = queryBuilder.BuildSelectGeoServicesPointQuery(layerId: 1, query);

        result.Sql.Should().Contain("geometry &&");
        result.Sql.Should().NotContain("ST_Intersects");
        result.Sql.Should().NotContain("ST_X(geometry) >=");
        result.Sql.Should().NotContain("ST_Y(geometry) >=");
    }

    [Fact]
    public void BuildSelectGeoServicesPointQuery_WithExactPointSimpleEnvelope_UsesExactCoordinatePredicate()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        var queryBuilder = new FeatureQueryBuilder(stringBuilderPool, geometryProcessor);

        var query = new FeatureQuery
        {
            GeometryType = MetadataV2GeometryType.Point,
            SpatialReferenceSrid = 4326,
            SpatialFilter = SpatialFilter.Create(
                [1, 2, 3, 4],
                SpatialRelationship.Intersects,
                srid: 4326,
                isSimpleEnvelope: true,
                envelopeMinX: 1,
                envelopeMinY: 2,
                envelopeMaxX: 3,
                envelopeMaxY: 4)
        };

        var result = queryBuilder.BuildSelectGeoServicesPointQuery(layerId: 1, query);

        result.Sql.Should().Contain("geometry &&");
        result.Sql.Should().Contain("GeometryType(geometry) = 'POINT'");
        result.Sql.Should().Contain("ST_Intersects");
        result.Sql.Should().Contain("ST_X(geometry) >= ");
        result.Sql.Should().Contain("ST_Y(geometry) >= ");
        result.Sql.Should().Contain("ST_X(geometry) <= ");
        result.Sql.Should().Contain("ST_Y(geometry) <= ");
        result.WhereParameters.OfType<double>().Should().Equal(1d, 2d, 3d, 4d);
    }

    [Fact]
    public void BuildSelectGeoServicesPointQuery_WithPointSimpleEnvelopeWithoutBounds_KeepsExactPredicate()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        var queryBuilder = new FeatureQueryBuilder(stringBuilderPool, geometryProcessor);

        var query = new FeatureQuery
        {
            GeometryType = MetadataV2GeometryType.Point,
            SpatialReferenceSrid = 4326,
            SpatialFilter = SpatialFilter.Create(
                [1, 2, 3, 4],
                SpatialRelationship.Intersects,
                srid: 4326,
                isSimpleEnvelope: true)
        };

        var result = queryBuilder.BuildSelectGeoServicesPointQuery(layerId: 1, query);

        result.Sql.Should().Contain("geometry &&");
        result.Sql.Should().Contain("ST_Intersects");
        result.Sql.Should().NotContain("ST_X(geometry) >=");
        result.Sql.Should().NotContain("ST_Y(geometry) >=");
    }

    [Fact]
    public void BuildSelectGeoServicesPointQuery_WithEnvelopeIntersectsAndBounds_UsesEnvelopeOnlyPredicate()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        var queryBuilder = new FeatureQueryBuilder(stringBuilderPool, geometryProcessor);

        var query = new FeatureQuery
        {
            GeometryType = MetadataV2GeometryType.Point,
            SpatialReferenceSrid = 4326,
            SpatialFilter = SpatialFilter.Create(
                [1, 2, 3, 4],
                SpatialRelationship.EnvelopeIntersects,
                srid: 4326,
                isSimpleEnvelope: true,
                envelopeMinX: 1,
                envelopeMinY: 2,
                envelopeMaxX: 3,
                envelopeMaxY: 4)
        };

        var result = queryBuilder.BuildSelectGeoServicesPointQuery(layerId: 1, query);

        result.Sql.Should().Contain("geometry &&");
        result.Sql.Should().NotContain("ST_Intersects");
        result.Sql.Should().NotContain("ST_X(geometry) >=");
        result.Sql.Should().NotContain("ST_Y(geometry) >=");
    }

    [Fact]
    public void BuildSelectGeoServicesPointQuery_WithNonEnvelopeIntersects_KeepsExactPredicate()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        var queryBuilder = new FeatureQueryBuilder(stringBuilderPool, geometryProcessor);

        var query = new FeatureQuery
        {
            GeometryType = MetadataV2GeometryType.Point,
            SpatialReferenceSrid = 4326,
            SpatialFilter = SpatialFilter.Create([1, 2, 3, 4], SpatialRelationship.Intersects, 4326)
        };

        var result = queryBuilder.BuildSelectGeoServicesPointQuery(layerId: 1, query);

        result.Sql.Should().Contain("geometry &&");
        result.Sql.Should().Contain("ST_Intersects");
    }

    [Fact]
    public void BuildExtentQuery_WithSpatialFilter_AddsGeometryParameter()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        var queryBuilder = new FeatureQueryBuilder(stringBuilderPool, geometryProcessor);

        var geometry = new byte[] { 1, 2, 3, 4 };
        var query = new FeatureQuery
        {
            SpatialReferenceSrid = 4326,
            SpatialFilter = SpatialFilter.Create(geometry, SpatialRelationship.Intersects, 4326)
        };

        var result = queryBuilder.BuildExtentQuery(layerId: 1, query);

        result.WhereParameters.OfType<byte[]>().Should().ContainSingle(value => value.SequenceEqual(geometry));
        result.Sql.Should().Contain("ST_Intersects");
    }

    [Fact]
    public void BuildMvtTileQuery_WithDistanceFilter_AddsGeometryAndDistanceParameters()
    {
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        var queryBuilder = new FeatureQueryBuilder(stringBuilderPool, geometryProcessor);

        var geometry = new byte[] { 5, 6, 7, 8 };
        var query = new FeatureQuery
        {
            SpatialReferenceSrid = 4326,
            SpatialFilter = SpatialFilter.CreateDistanceFilter(
                geometry,
                distance: 5,
                unit: DistanceUnit.Miles,
                withinDistance: true,
                srid: 4326)
        };

        var tileOptions = new TileOptions
        {
            SimplifyZoom = 0,
            TileBuffer = 0,
            TileExtent = 4096
        };

        var tileLimits = new TileLimits
        {
            MaxFeaturesPerTile = 0
        };

        var result = queryBuilder.BuildMvtTileQuery(
            layerId: 1,
            x: 0,
            y: 0,
            z: 5,
            query: query,
            tileOptions: tileOptions,
            tileLimits: tileLimits);

        var distanceInMeters = geometryProcessor.ConvertDistanceToMeters(5, DistanceUnit.Miles);

        result.WhereParameters.OfType<byte[]>().Should().ContainSingle(value => value.SequenceEqual(geometry));
        result.WhereParameters.Should().Contain(distanceInMeters);
        result.Sql.Should().Contain("ST_DWithin");
    }

    [Fact]
    public void BuildMvtTileQuery_WithCustomGridset_UsesGridsetSridEnvelopeAndTransform()
    {
        // #1839: a custom gridset (here British National Grid, SRID 27700) must rasterize the tile
        // in the gridset CRS — ST_MakeEnvelope(..., 27700) and ST_Transform(geom, 27700) — rather
        // than the WebMercator (3857) / WorldCRS84Quad (4326) built-in paths.
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        var queryBuilder = new FeatureQueryBuilder(stringBuilderPool, geometryProcessor);

        var gridGeometry = new GridGeometry
        {
            Id = "BritishNationalGrid",
            Srid = 27700,
            IsGeographic = false,
            TopLeftX = 0,
            TopLeftY = 1_300_000,
            TileWidth = 256,
            TileHeight = 256,
            Levels =
            [
                new GridLevel(0, ScaleDenominator: 10_000_000, CellSize: 5000, MatrixWidth: 1, MatrixHeight: 1)
            ]
        };

        var query = new FeatureQuery { SpatialReferenceSrid = 4326 };
        var tileOptions = new TileOptions { SimplifyZoom = 0, TileBuffer = 0, TileExtent = 4096 };
        var tileLimits = new TileLimits { MaxFeaturesPerTile = 0 };

        var result = queryBuilder.BuildMvtTileQuery(
            layerId: 1,
            x: 0,
            y: 0,
            z: 0,
            query: query,
            tileOptions: tileOptions,
            tileLimits: tileLimits,
            gridGeometry: gridGeometry);

        result.Sql.Should().Contain("ST_AsMVT");
        result.Sql.Should().Contain("ST_MakeEnvelope");
        result.Sql.Should().Contain("27700");
        result.Sql.Should().Contain("ST_Transform");
        // Built-in tile-envelope helpers must not appear on the custom path.
        result.Sql.Should().NotContain("ST_TileEnvelope");
    }

    [Fact]
    public void BuildMvtTileQuery_WithNullGridGeometry_KeepsBuiltInWebMercatorPath()
    {
        // Guard the byte-identical built-in path: a null gridGeometry must still emit the
        // ST_TileEnvelope-based WebMercator query (the CITE snapshot guard depends on this).
        var poolProvider = new DefaultObjectPoolProvider();
        var stringBuilderPool = poolProvider.Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        var geometryProcessor = new GeometryProcessor();
        var queryBuilder = new FeatureQueryBuilder(stringBuilderPool, geometryProcessor);

        var query = new FeatureQuery { SpatialReferenceSrid = 3857 };
        var tileOptions = new TileOptions { SimplifyZoom = 0, TileBuffer = 0, TileExtent = 4096 };
        var tileLimits = new TileLimits { MaxFeaturesPerTile = 0 };

        var result = queryBuilder.BuildMvtTileQuery(
            layerId: 1,
            x: 0,
            y: 0,
            z: 5,
            query: query,
            tileOptions: tileOptions,
            tileLimits: tileLimits,
            gridGeometry: null);

        result.Sql.Should().Contain("ST_TileEnvelope");
    }
}

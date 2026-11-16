// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.SpatialAnalytics.Domain;
using Honua.Core.Features.Tiles;

namespace Honua.Core.Features.FeatureStore.Abstractions;

internal interface IFeatureQueryBuilder
{
    ParameterizedQuery BuildSelectQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    ParameterizedQuery BuildProjectedPointQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    ParameterizedQuery BuildObjectIdsQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    ParameterizedQuery BuildPreChangeObjectIdsQuery(
        int layerId,
        FeatureQuery query,
        IReadOnlyCollection<long> changeIds,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => throw new NotSupportedException("This provider does not record pre-change row images.");

    ParameterizedQuery BuildSelectFlatGeobufQuery(
        MetadataV2Object resource,
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    ParameterizedQuery BuildSelectGeobufQuery(
        MetadataV2Object resource,
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    ParameterizedQuery BuildSelectGmlQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    ParameterizedQuery BuildSelectGeoJsonQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    ParameterizedQuery BuildSelectKmlQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    ParameterizedQuery BuildCountQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    ParameterizedQuery BuildOptimizedSelectQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    ParameterizedQuery BuildOptimizedSelectGmlQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    ParameterizedQuery BuildOptimizedSelectGeoJsonQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    ParameterizedQuery BuildOptimizedSelectKmlQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    ParameterizedQuery BuildExtentQuery(
        int layerId,
        FeatureQuery? query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    ParameterizedQuery BuildTemporalExtentQuery(
        int layerId,
        string fieldName,
        TemporalPropertyType propertyType);

    ParameterizedQuery BuildStatisticsQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    ParameterizedQuery BuildTopFeaturesQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    ParameterizedQuery BuildDateBinsQuery(
        int layerId,
        FeatureQuery query,
        DateBinDefinition dateBin,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    ParameterizedQuery BuildBinsQuery(
        int layerId,
        FeatureQuery query,
        BinDefinition binDefinition,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    ParameterizedQuery BuildMvtTileQuery(
        int layerId,
        int x,
        int y,
        int z,
        FeatureQuery? query,
        TileOptions tileOptions,
        TileLimits tileLimits,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry,
        Honua.Core.Features.Tiles.GridGeometry? gridGeometry = null);

    ParameterizedQuery BuildH3AggregationQuery(
        int layerId,
        FeatureQuery query,
        H3AggregationQuery h3Query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    ParameterizedQuery BuildH3TileQuery(
        int layerId,
        int x,
        int y,
        int z,
        int resolution,
        FeatureQuery? query,
        TileOptions tileOptions,
        TileLimits tileLimits,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    ParameterizedQuery BuildClusterQuery(
        int layerId,
        FeatureQuery query,
        ClusterQuery clusterQuery,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    ParameterizedQuery BuildBufferAggregateQuery(
        int layerId,
        FeatureQuery query,
        BufferAggregateQuery bufferQuery,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    ParameterizedQuery BuildDensityQuery(
        int layerId,
        FeatureQuery query,
        DensityQuery densityQuery,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    ParameterizedQuery BuildSpatialJoinQuery(
        int targetLayerId,
        FeatureQuery targetQuery,
        SpatialJoinQuery joinQuery,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);
}

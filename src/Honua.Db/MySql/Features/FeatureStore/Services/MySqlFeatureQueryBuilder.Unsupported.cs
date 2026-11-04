// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.SpatialAnalytics.Domain;
using Honua.Core.Features.Tiles;
using Honua.Db.MySql;

namespace Honua.Db.MySql.Features.FeatureStore.Services;

internal sealed partial class MySqlFeatureQueryBuilder
{
    private static ParameterizedQuery ThrowNotSupported([CallerMemberName] string operation = "")
        => throw MySqlUnsupportedFeature.Operation(operation);

    public ParameterizedQuery BuildProjectedPointQuery(
        int layerId, FeatureQuery query, GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    public ParameterizedQuery BuildSelectFlatGeobufQuery(
        MetadataV2Resource resource, int layerId, FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    public ParameterizedQuery BuildSelectGeobufQuery(
        MetadataV2Resource resource, int layerId, FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    public ParameterizedQuery BuildSelectGmlQuery(
        int layerId, FeatureQuery query, GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    public ParameterizedQuery BuildSelectGeoJsonQuery(
        int layerId, FeatureQuery query, GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    public ParameterizedQuery BuildSelectKmlQuery(
        int layerId, FeatureQuery query, GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    public ParameterizedQuery BuildOptimizedSelectGmlQuery(
        int layerId, FeatureQuery query, GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    public ParameterizedQuery BuildOptimizedSelectGeoJsonQuery(
        int layerId, FeatureQuery query, GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    public ParameterizedQuery BuildOptimizedSelectKmlQuery(
        int layerId, FeatureQuery query, GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    public ParameterizedQuery BuildTemporalExtentQuery(int layerId, string fieldName, TemporalPropertyType propertyType)
        => ThrowNotSupported();

    public ParameterizedQuery BuildStatisticsQuery(
        int layerId, FeatureQuery query, GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    public ParameterizedQuery BuildTopFeaturesQuery(
        int layerId, FeatureQuery query, GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    public ParameterizedQuery BuildDateBinsQuery(
        int layerId, FeatureQuery query, DateBinDefinition dateBin,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    public ParameterizedQuery BuildBinsQuery(
        int layerId, FeatureQuery query, BinDefinition binDefinition,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    public ParameterizedQuery BuildMvtTileQuery(
        int layerId, int x, int y, int z, FeatureQuery? query, TileOptions tileOptions, TileLimits tileLimits,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry,
        Honua.Core.Features.Tiles.GridGeometry? gridGeometry = null)
        => ThrowNotSupported();

    public ParameterizedQuery BuildH3AggregationQuery(
        int layerId, FeatureQuery query, H3AggregationQuery h3Query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    public ParameterizedQuery BuildH3TileQuery(
        int layerId, int x, int y, int z, int resolution, FeatureQuery? query,
        TileOptions tileOptions, TileLimits tileLimits,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    public ParameterizedQuery BuildClusterQuery(
        int layerId, FeatureQuery query, ClusterQuery clusterQuery,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    public ParameterizedQuery BuildBufferAggregateQuery(
        int layerId, FeatureQuery query, BufferAggregateQuery bufferQuery,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    public ParameterizedQuery BuildDensityQuery(
        int layerId, FeatureQuery query, DensityQuery densityQuery,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    public ParameterizedQuery BuildSpatialJoinQuery(
        int targetLayerId, FeatureQuery targetQuery, SpatialJoinQuery joinQuery,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();
}

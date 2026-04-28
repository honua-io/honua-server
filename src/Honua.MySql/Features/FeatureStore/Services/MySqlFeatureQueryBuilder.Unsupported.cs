// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.SpatialAnalytics.Domain;
using Honua.Core.Features.Tiles;

namespace Honua.MySql.Features.FeatureStore.Services;

internal sealed partial class MySqlFeatureQueryBuilder
{
    private static ParameterizedQuery ThrowNotSupported([CallerMemberName] string operation = "")
        => throw new NotSupportedException(
            $"Operation '{operation}' is not supported by the MySQL/MariaDB provider.");

    /// <inheritdoc />
    public ParameterizedQuery BuildProjectedPointQuery(
        int layerId, FeatureQuery query, GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    /// <inheritdoc />
    public ParameterizedQuery BuildSelectFlatGeobufQuery(
        LayerDefinition layer, int layerId, FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    /// <inheritdoc />
    public ParameterizedQuery BuildSelectGeobufQuery(
        LayerDefinition layer, int layerId, FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    /// <inheritdoc />
    public ParameterizedQuery BuildSelectGmlQuery(
        int layerId, FeatureQuery query, GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    /// <inheritdoc />
    public ParameterizedQuery BuildSelectGeoJsonQuery(
        int layerId, FeatureQuery query, GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    /// <inheritdoc />
    public ParameterizedQuery BuildSelectKmlQuery(
        int layerId, FeatureQuery query, GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    /// <inheritdoc />
    public ParameterizedQuery BuildOptimizedSelectGmlQuery(
        int layerId, FeatureQuery query, GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    /// <inheritdoc />
    public ParameterizedQuery BuildOptimizedSelectGeoJsonQuery(
        int layerId, FeatureQuery query, GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    /// <inheritdoc />
    public ParameterizedQuery BuildOptimizedSelectKmlQuery(
        int layerId, FeatureQuery query, GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    /// <inheritdoc />
    public ParameterizedQuery BuildTemporalExtentQuery(int layerId, string fieldName, FieldType fieldType)
        => ThrowNotSupported();

    /// <inheritdoc />
    public ParameterizedQuery BuildStatisticsQuery(
        int layerId, FeatureQuery query, GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    /// <inheritdoc />
    public ParameterizedQuery BuildTopFeaturesQuery(
        int layerId, FeatureQuery query, GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    /// <inheritdoc />
    public ParameterizedQuery BuildDateBinsQuery(
        int layerId, FeatureQuery query, DateBinDefinition dateBin,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    /// <inheritdoc />
    public ParameterizedQuery BuildBinsQuery(
        int layerId, FeatureQuery query, BinDefinition binDefinition,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    /// <inheritdoc />
    public ParameterizedQuery BuildMvtTileQuery(
        int layerId, int x, int y, int z, FeatureQuery? query, TileOptions tileOptions, TileLimits tileLimits,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    /// <inheritdoc />
    public ParameterizedQuery BuildH3AggregationQuery(
        int layerId, FeatureQuery query, H3AggregationQuery h3Query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    /// <inheritdoc />
    public ParameterizedQuery BuildH3TileQuery(
        int layerId, int x, int y, int z, int resolution, FeatureQuery? query,
        TileOptions tileOptions, TileLimits tileLimits,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    /// <inheritdoc />
    public ParameterizedQuery BuildClusterQuery(
        int layerId, FeatureQuery query, ClusterQuery clusterQuery,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    /// <inheritdoc />
    public ParameterizedQuery BuildBufferAggregateQuery(
        int layerId, FeatureQuery query, BufferAggregateQuery bufferQuery,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    /// <inheritdoc />
    public ParameterizedQuery BuildDensityQuery(
        int layerId, FeatureQuery query, DensityQuery densityQuery,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();

    /// <inheritdoc />
    public ParameterizedQuery BuildSpatialJoinQuery(
        int targetLayerId, FeatureQuery targetQuery, SpatialJoinQuery joinQuery,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry)
        => ThrowNotSupported();
}

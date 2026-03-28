// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Tiles;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Abstraction for building SQL queries for feature operations
/// </summary>
internal interface IFeatureQueryBuilder
{
    /// <summary>
    /// Builds a SELECT query for features
    /// </summary>
    ParameterizedQuery BuildSelectQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    /// <summary>
    /// Builds a SELECT query for object IDs only.
    /// </summary>
    ParameterizedQuery BuildObjectIdsQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    /// <summary>
    /// Builds a provider-native FlatGeobuf query.
    /// </summary>
    ParameterizedQuery BuildSelectFlatGeobufQuery(
        LayerDefinition layer,
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    /// <summary>
    /// Builds a provider-native Geobuf query.
    /// </summary>
    ParameterizedQuery BuildSelectGeobufQuery(
        LayerDefinition layer,
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    /// <summary>
    /// Builds a SELECT query for GML features
    /// </summary>
    ParameterizedQuery BuildSelectGmlQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    /// <summary>
    /// Builds a SELECT query for GeoJSON-encoded features.
    /// </summary>
    ParameterizedQuery BuildSelectGeoJsonQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    /// <summary>
    /// Builds a SELECT query for KML features.
    /// </summary>
    ParameterizedQuery BuildSelectKmlQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    /// <summary>
    /// Builds a COUNT query for features
    /// </summary>
    ParameterizedQuery BuildCountQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    /// <summary>
    /// Builds an optimized query with window functions for pagination
    /// </summary>
    ParameterizedQuery BuildOptimizedSelectQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    /// <summary>
    /// Builds an optimized GML query with window functions for pagination
    /// </summary>
    ParameterizedQuery BuildOptimizedSelectGmlQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    /// <summary>
    /// Builds an optimized GeoJSON query with window functions for pagination.
    /// </summary>
    ParameterizedQuery BuildOptimizedSelectGeoJsonQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    /// <summary>
    /// Builds an optimized KML query with window functions for pagination.
    /// </summary>
    ParameterizedQuery BuildOptimizedSelectKmlQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    /// <summary>
    /// Builds an extent query for calculating layer bounds
    /// </summary>
    ParameterizedQuery BuildExtentQuery(
        int layerId,
        FeatureQuery? query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    /// <summary>
    /// Builds a temporal extent query for calculating min/max of a temporal field.
    /// </summary>
    ParameterizedQuery BuildTemporalExtentQuery(
        int layerId,
        string fieldName,
        FieldType fieldType);

    /// <summary>
    /// Builds an aggregate statistics query (outStatistics with optional GROUP BY)
    /// </summary>
    ParameterizedQuery BuildStatisticsQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    /// <summary>
    /// Builds a top features query using window functions for partitioning and ranking
    /// </summary>
    ParameterizedQuery BuildTopFeaturesQuery(
        int layerId,
        FeatureQuery query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    /// <summary>
    /// Builds a date binning query that groups features into temporal intervals
    /// </summary>
    ParameterizedQuery BuildDateBinsQuery(
        int layerId,
        FeatureQuery query,
        DateBinDefinition dateBin,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    /// <summary>
    /// Builds a numeric/classification binning query
    /// </summary>
    ParameterizedQuery BuildBinsQuery(
        int layerId,
        FeatureQuery query,
        BinDefinition binDefinition,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    /// <summary>
    /// Builds an MVT tile query
    /// </summary>
    ParameterizedQuery BuildMvtTileQuery(
        int layerId,
        int x,
        int y,
        int z,
        FeatureQuery? query,
        TileOptions tileOptions,
        TileLimits tileLimits,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    /// <summary>
    /// Builds an H3 hexagonal grid aggregation query that groups features into
    /// H3 cells and computes statistics per cell.
    /// </summary>
    ParameterizedQuery BuildH3AggregationQuery(
        int layerId,
        FeatureQuery query,
        H3AggregationQuery h3Query,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);

    /// <summary>
    /// Builds an MVT tile query that renders H3 cell boundaries as vector tile features.
    /// </summary>
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
}

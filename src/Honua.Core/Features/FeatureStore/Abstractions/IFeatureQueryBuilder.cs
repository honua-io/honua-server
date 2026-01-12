// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Tiles;
using Honua.Core.Features.Catalog.Domain;

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
    /// Builds a SELECT query for GML features
    /// </summary>
    ParameterizedQuery BuildSelectGmlQuery(
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
    /// Builds an MVT tile query
    /// </summary>
    ParameterizedQuery BuildMvtTileQuery(
        int layerId,
        int x,
        int y,
        int z,
        FeatureQuery? query,
        TileOptions tileOptions,
        GeometryStorageType geometryStorageType = GeometryStorageType.Geometry);
}

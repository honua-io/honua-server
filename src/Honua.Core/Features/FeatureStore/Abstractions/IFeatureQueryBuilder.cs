// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Abstraction for building SQL queries for feature operations
/// </summary>
internal interface IFeatureQueryBuilder
{
    /// <summary>
    /// Builds a SELECT query for features
    /// </summary>
    ParameterizedQuery BuildSelectQuery(int layerId, FeatureQuery query);

    /// <summary>
    /// Builds a SELECT query for GML features
    /// </summary>
    ParameterizedQuery BuildSelectGmlQuery(int layerId, FeatureQuery query);

    /// <summary>
    /// Builds a COUNT query for features
    /// </summary>
    ParameterizedQuery BuildCountQuery(int layerId, FeatureQuery query);

    /// <summary>
    /// Builds an optimized query with window functions for pagination
    /// </summary>
    ParameterizedQuery BuildOptimizedSelectQuery(int layerId, FeatureQuery query);

    /// <summary>
    /// Builds an optimized GML query with window functions for pagination
    /// </summary>
    ParameterizedQuery BuildOptimizedSelectGmlQuery(int layerId, FeatureQuery query);

    /// <summary>
    /// Builds an extent query for calculating layer bounds
    /// </summary>
    ParameterizedQuery BuildExtentQuery(int layerId, FeatureQuery? query);

    /// <summary>
    /// Builds an MVT tile query
    /// </summary>
    ParameterizedQuery BuildMvtTileQuery(int layerId, int x, int y, int z, FeatureQuery? query, string? tileBuffer = null);
}


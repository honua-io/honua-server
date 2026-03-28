// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Provides read-only access to features with query and aggregation capabilities.
/// Segregated from write operations to support read-only consumers and caching strategies.
/// </summary>
public interface IFeatureReader
{
    /// <summary>
    /// Retrieves a feature by its unique identifier
    /// </summary>
    /// <param name="layerId">Layer identifier containing the feature</param>
    /// <param name="featureId">Unique feature identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Feature if found, null otherwise</returns>
    Task<Feature?> GetAsync(int layerId, long featureId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries features with optional filtering and pagination
    /// </summary>
    /// <param name="layerId">Layer identifier to query</param>
    /// <param name="query">Query specification including filters and pagination</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Paginated query result with total count</returns>
    Task<QueryResult<Feature>> QueryAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries features as a FlatGeobuf payload when the underlying provider supports it.
    /// </summary>
    /// <param name="layerId">Layer identifier to query</param>
    /// <param name="query">Query specification including filters and pagination</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>FlatGeobuf payload, or null when no features match</returns>
    Task<byte[]?> QueryFlatGeobufAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries only object IDs with optional filtering, sorting, and pagination.
    /// </summary>
    /// <param name="layerId">Layer identifier to query</param>
    /// <param name="query">Query specification including filters and pagination</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Matching object identifiers</returns>
    Task<ImmutableArray<long>> QueryObjectIdsAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts features matching the specified criteria
    /// </summary>
    /// <param name="layerId">Layer identifier to query</param>
    /// <param name="query">Query specification for filtering</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of features matching the criteria</returns>
    Task<long> CountAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the spatial extent (bounding box) of features
    /// </summary>
    /// <param name="layerId">Layer identifier to analyze</param>
    /// <param name="query">Optional query specification for filtering</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Spatial extent of the features, null if no features found</returns>
    Task<FeatureExtent?> GetExtentAsync(int layerId, FeatureQuery? query = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes an aggregate statistics query with optional grouping
    /// </summary>
    /// <param name="layerId">Layer identifier to query</param>
    /// <param name="query">Query specification including OutStatistics and GroupByFields</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Array of rows, each containing statistic field names mapped to computed values</returns>
    Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryStatisticsAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the temporal extent (min/max) for a temporal field.
    /// </summary>
    /// <param name="layerId">Layer identifier to analyze</param>
    /// <param name="fieldName">Temporal field name</param>
    /// <param name="fieldType">Temporal field type</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Temporal extent of the field, null if no values found</returns>
    Task<TemporalExtentResult?> GetTemporalExtentAsync(
        int layerId,
        string fieldName,
        FieldType fieldType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets approximate count and extent estimates using catalog statistics
    /// </summary>
    /// <param name="layerId">Layer identifier to estimate</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Estimated count and extent</returns>
    Task<EstimateResult> GetEstimatesAsync(
        int layerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries top features per group using window functions
    /// </summary>
    /// <param name="layerId">Layer identifier to query</param>
    /// <param name="query">Query specification including filters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Paginated query result with top features per group</returns>
    Task<QueryResult<Feature>> QueryTopFeaturesAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries features binned by date intervals
    /// </summary>
    /// <param name="layerId">Layer identifier to query</param>
    /// <param name="query">Query specification including filters</param>
    /// <param name="dateBin">Date bin definition specifying calendar or fixed intervals</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Rows containing bin boundaries and aggregate values</returns>
    Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryDateBinsAsync(
        int layerId,
        FeatureQuery query,
        DateBinDefinition dateBin,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries features binned by numeric or classification intervals
    /// </summary>
    /// <param name="layerId">Layer identifier to query</param>
    /// <param name="query">Query specification including filters</param>
    /// <param name="binDefinition">Bin definition specifying the binning algorithm</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Rows containing bin boundaries and aggregate values</returns>
    Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryBinsAsync(
        int layerId,
        FeatureQuery query,
        BinDefinition binDefinition,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries features aggregated by H3 hexagonal grid cells at a given resolution.
    /// Returns one row per occupied cell with cell index, boundary geometry, and statistics.
    /// </summary>
    /// <param name="layerId">Layer identifier to query</param>
    /// <param name="query">Query specification including filters</param>
    /// <param name="h3Query">H3 aggregation parameters (resolution, polyfill, k-ring, statistics)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Rows containing H3 cell index, boundary geometry as GeoJSON, and aggregate values</returns>
    Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryH3Async(
        int layerId,
        FeatureQuery query,
        H3AggregationQuery h3Query,
        CancellationToken cancellationToken = default);
}

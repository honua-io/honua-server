// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Query;

/// <summary>
/// Unified query processor that handles query execution across all protocols.
/// Provides shared query optimization, validation, and execution logic.
/// </summary>
public interface IQueryProcessor
{
    /// <summary>
    /// Validates a unified query against layer constraints and protocol limits.
    /// </summary>
    /// <param name="query">Unified query to validate</param>
    /// <param name="layer">Target layer definition</param>
    /// <returns>Validation result with any errors</returns>
    QueryValidationResult ValidateQuery(UnifiedQuery query, LayerDefinition layer);

    /// <summary>
    /// Optimizes a unified query for efficient execution.
    /// </summary>
    /// <param name="query">Query to optimize</param>
    /// <param name="layer">Target layer definition</param>
    /// <returns>Optimized query</returns>
    UnifiedQuery OptimizeQuery(UnifiedQuery query, LayerDefinition layer);

    /// <summary>
    /// Converts a unified query to a FeatureQuery for data access.
    /// </summary>
    /// <param name="query">Unified query</param>
    /// <param name="layer">Target layer definition</param>
    /// <returns>Feature query for data access layer</returns>
    FeatureQuery ToFeatureQuery(UnifiedQuery query, LayerDefinition layer);

    /// <summary>
    /// Builds cache key for the given query and layer.
    /// </summary>
    /// <param name="query">Unified query</param>
    /// <param name="layer">Target layer definition</param>
    /// <param name="protocol">Protocol-specific identifier</param>
    /// <returns>Cache key string</returns>
    string BuildCacheKey(UnifiedQuery query, LayerDefinition layer, string protocol);

    /// <summary>
    /// Determines if the query should use streaming response.
    /// </summary>
    /// <param name="query">Unified query</param>
    /// <param name="layer">Target layer definition</param>
    /// <param name="outputFormat">Requested output format</param>
    /// <returns>True if streaming should be used</returns>
    bool ShouldUseStreaming(UnifiedQuery query, LayerDefinition layer, string outputFormat);

    /// <summary>
    /// Estimates the result count for the given query without executing it.
    /// </summary>
    /// <param name="query">Unified query</param>
    /// <param name="layer">Target layer definition</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Estimated result count</returns>
    Task<long> EstimateResultCountAsync(UnifiedQuery query, LayerDefinition layer, CancellationToken cancellationToken);
}

/// <summary>
/// Result of query validation.
/// </summary>
public sealed record QueryValidationResult
{
    /// <summary>
    /// Whether the query is valid.
    /// </summary>
    public required bool IsValid { get; init; }

    /// <summary>
    /// Error message if validation failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Validation warnings that don't prevent execution.
    /// </summary>
    public IReadOnlyList<string>? Warnings { get; init; }

    /// <summary>
    /// Creates a successful validation result.
    /// </summary>
    /// <param name="warnings">Optional warnings</param>
    /// <returns>Successful validation result</returns>
    public static QueryValidationResult Success(IReadOnlyList<string>? warnings = null)
        => new() { IsValid = true, Warnings = warnings };

    /// <summary>
    /// Creates a failed validation result.
    /// </summary>
    /// <param name="errorMessage">Error message</param>
    /// <returns>Failed validation result</returns>
    public static QueryValidationResult Failure(string errorMessage)
        => new() { IsValid = false, ErrorMessage = errorMessage };
}
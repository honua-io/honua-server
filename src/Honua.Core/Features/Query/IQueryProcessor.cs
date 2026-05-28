// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Query;

/// <summary>
/// Unified query processor that handles query execution across all protocols.
/// Provides shared query optimization, validation, and execution logic.
/// </summary>
public interface IQueryProcessor
{
    /// <summary>
    /// Validates a unified query against resource constraints and protocol limits.
    /// </summary>
    /// <remarks>
    /// Audit-C1: abstract (no default body) so the compiler forces every <see cref="IQueryProcessor"/>
    /// implementation to ship a working override. Previously a default-interface-method that
    /// threw <see cref="NotSupportedException"/> at runtime; half-migrations were not caught at build time.
    /// </remarks>
    QueryValidationResult ValidateQuery(UnifiedQuery query, MetadataV2Resource resource);

    /// <summary>
    /// Optimizes a unified query for efficient execution.
    /// </summary>
    /// <remarks>
    /// Audit-C1: abstract (no default body) so the compiler forces every <see cref="IQueryProcessor"/>
    /// implementation to ship a working override.
    /// </remarks>
    UnifiedQuery OptimizeQuery(UnifiedQuery query, MetadataV2Resource resource);

    /// <summary>
    /// Converts a unified query to a FeatureQuery for data access.
    /// </summary>
    /// <remarks>
    /// Audit-C1: abstract (no default body) so the compiler forces every <see cref="IQueryProcessor"/>
    /// implementation to ship a working override.
    /// </remarks>
    FeatureQuery ToFeatureQuery(UnifiedQuery query, MetadataV2Resource resource);

    /// <summary>
    /// Builds cache key for the given query and resource.
    /// </summary>
    /// <remarks>
    /// Audit-C1: abstract (no default body) so the compiler forces every <see cref="IQueryProcessor"/>
    /// implementation to ship a working override.
    /// </remarks>
    string BuildCacheKey(UnifiedQuery query, MetadataV2Resource resource, string protocol);

    /// <summary>
    /// Determines if the query should use streaming response.
    /// </summary>
    /// <remarks>
    /// Audit-C1: abstract (no default body) so the compiler forces every <see cref="IQueryProcessor"/>
    /// implementation to ship a working override.
    /// </remarks>
    bool ShouldUseStreaming(UnifiedQuery query, MetadataV2Resource resource, string outputFormat);

    /// <summary>
    /// Estimates the result count for the given query without executing it.
    /// </summary>
    /// <remarks>
    /// Audit-C1: abstract (no default body) so the compiler forces every <see cref="IQueryProcessor"/>
    /// implementation to ship a working override.
    /// </remarks>
    Task<long> EstimateResultCountAsync(UnifiedQuery query, MetadataV2Resource resource, CancellationToken cancellationToken);
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

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Proto = Honua.Server.Features.Grpc.Proto.V2;

namespace Honua.Server.Features.Infrastructure.Services;

/// <summary>
/// Service for optimizing queries and results for mobile devices.
/// Focuses on reducing battery usage, bandwidth consumption, and improving responsiveness.
/// </summary>
public interface IMobileOptimizationService
{
    /// <summary>
    /// Optimizes a feature query for mobile device constraints.
    /// </summary>
    /// <param name="query">The original feature query.</param>
    /// <param name="mobileOptions">Mobile-specific optimization options.</param>
    /// <returns>Optimized feature query.</returns>
    Task<FeatureQuery> OptimizeQueryAsync(
        FeatureQuery query,
        Proto.MobileOptimizations? mobileOptions);

    /// <summary>
    /// Optimizes query results for mobile delivery.
    /// </summary>
    /// <param name="result">The original query result.</param>
    /// <param name="mobileOptions">Mobile-specific optimization options.</param>
    /// <returns>Optimized query result.</returns>
    Task<QueryResult<FeatureRecord>> OptimizeResultAsync(
        QueryResult<FeatureRecord> result,
        Proto.MobileOptimizations? mobileOptions);

    /// <summary>
    /// Calculates geometry simplification level based on mobile context.
    /// </summary>
    /// <param name="levelOfDetail">Level of detail specification.</param>
    /// <param name="mobileOptions">Mobile optimization options.</param>
    /// <returns>Simplification level (0-10, where 0 is no simplification).</returns>
    int CalculateSimplificationLevel(
        Proto.LevelOfDetail? levelOfDetail,
        Proto.MobileOptimizations? mobileOptions);

    /// <summary>
    /// Determines optimal batch size for streaming based on mobile constraints.
    /// </summary>
    /// <param name="mobileOptions">Mobile optimization options.</param>
    /// <param name="defaultBatchSize">Default batch size.</param>
    /// <returns>Optimized batch size.</returns>
    int GetOptimalBatchSize(Proto.MobileOptimizations? mobileOptions, int defaultBatchSize);

    /// <summary>
    /// Gets field priority ordering for progressive loading.
    /// </summary>
    /// <param name="layerFields">Available layer fields.</param>
    /// <param name="priorityFields">User-specified priority fields.</param>
    /// <returns>Ordered field list for progressive loading.</returns>
    IEnumerable<string> GetProgressiveFieldOrder(
        IEnumerable<FieldDefinition> layerFields,
        IEnumerable<string>? priorityFields);

    /// <summary>
    /// Estimates mobile resource consumption for a query.
    /// </summary>
    /// <param name="query">The feature query.</param>
    /// <param name="layerDefinition">Layer metadata.</param>
    /// <returns>Resource consumption estimates.</returns>
    MobileResourceEstimate EstimateResourceConsumption(
        FeatureQuery query,
        LayerDefinition layerDefinition);
}

/// <summary>
/// Estimates for mobile resource consumption.
/// </summary>
public class MobileResourceEstimate
{
    /// <summary>
    /// Estimated bandwidth usage in bytes.
    /// </summary>
    public long EstimatedBandwidthBytes { get; init; }

    /// <summary>
    /// Estimated battery usage percentage (0-100).
    /// </summary>
    public double EstimatedBatteryUsagePercent { get; init; }

    /// <summary>
    /// Estimated processing time in milliseconds.
    /// </summary>
    public int EstimatedProcessingTimeMs { get; init; }

    /// <summary>
    /// Estimated memory usage in MB.
    /// </summary>
    public double EstimatedMemoryUsageMB { get; init; }

    /// <summary>
    /// Recommended optimizations to reduce resource consumption.
    /// </summary>
    public IEnumerable<string> RecommendedOptimizations { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Whether the query is suitable for mobile execution.
    /// </summary>
    public bool IsMobileFriendly => EstimatedBatteryUsagePercent < 5.0 && EstimatedBandwidthBytes < 1_000_000;
}
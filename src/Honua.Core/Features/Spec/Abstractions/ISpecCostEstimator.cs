// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Abstractions;

/// <summary>
/// Estimates per-node cost (rows, bytes, duration) from catalog metadata only.
/// </summary>
public interface ISpecCostEstimator
{
    /// <summary>
    /// Returns a cost estimate and any per-node warnings for the given node.
    /// </summary>
    /// <param name="node">The node being estimated.</param>
    /// <param name="resolvedDependencies">Already-estimated upstream nodes keyed by id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SpecCostEstimationResult> EstimateAsync(
        CanonicalSpecNode node,
        IReadOnlyDictionary<string, SpecPlanNode> resolvedDependencies,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Combined output of cost estimation: the estimate and any warnings surfaced
/// while computing it.
/// </summary>
public sealed record SpecCostEstimationResult
{
    /// <summary>Cost estimate; unknown fields are null.</summary>
    public required SpecCostEstimate Estimate { get; init; }

    /// <summary>Per-node diagnostics emitted during estimation.</summary>
    public IReadOnlyList<SpecWarning> Warnings { get; init; } = [];
}

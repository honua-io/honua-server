// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Abstractions;

/// <summary>
/// Executes a single compute node on behalf of the apply orchestrator.
/// Implementations adapt the canonical spec op to an underlying process-family
/// invocation and return the raw artifact bytes.
/// </summary>
public interface ISpecComputeExecutor
{
    /// <summary>
    /// Runs the compute node and returns its output. The returned payload's
    /// <see cref="SpecArtifactPayload.ContentHash"/> must equal
    /// <paramref name="contentHash"/> — the orchestrator writes it into the
    /// cache without rehashing.
    /// </summary>
    /// <param name="node">The node being executed.</param>
    /// <param name="contentHash">Cache key the orchestrator reserved.</param>
    /// <param name="inputs">Resolved upstream artifact references keyed by
    /// node id.</param>
    /// <param name="cancellationToken">Cancellation token that is tripped by
    /// both client cancellation and <c>POST /v1/spec/cancel</c>.</param>
    Task<SpecComputeResult> ExecuteAsync(
        CanonicalSpecNode node,
        string contentHash,
        IReadOnlyDictionary<string, CachedArtifactRef> inputs,
        CancellationToken cancellationToken);
}

/// <summary>
/// Result of a <see cref="ISpecComputeExecutor.ExecuteAsync"/> call.
/// </summary>
public sealed record SpecComputeResult
{
    /// <summary>The artifact payload to write into the cache.</summary>
    public required SpecArtifactPayload Payload { get; init; }

    /// <summary>Actual cost observed during execution.</summary>
    public required SpecCostActual ActualCost { get; init; }

    /// <summary>Optional warnings emitted during execution.</summary>
    public IReadOnlyList<SpecWarning> Warnings { get; init; } = [];
}

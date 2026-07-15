// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Routing.Features.Routing.Domain;

namespace Honua.Routing.Features.Routing.Abstractions;

/// <summary>
/// Canonical atomic promotion/rollback service for the active network-topology generation
/// (#2719). Every mutation runs in one Postgres transaction that verifies preconditions,
/// flips the active-generation pointer, repoints <c>honua.network_datasets</c> so
/// <see cref="INetworkDatasetResolver"/> resolves every routing solve family from one
/// consistent snapshot, and records an immutable history entry. Exactly one active
/// generation exists before and after every call.
/// </summary>
public interface INetworkTopologyPromotionStore
{
    /// <summary>
    /// Promotes a <c>ready</c> candidate generation to active. Verifies the candidate is
    /// <c>ready</c> with materialized shadow artifacts, that the caller's expected active
    /// generation/row version still matches, then atomically retires the current active
    /// generation and activates the candidate. Idempotent: replaying the same
    /// <paramref name="idempotencyKey"/> for this dataset returns the original result
    /// without mutating again.
    /// </summary>
    Task<NetworkTopologyPromotionRecord> PromoteAsync(
        string datasetId,
        long candidateGeneration,
        long expectedActiveGeneration,
        long expectedActiveRowVersion,
        string actor,
        string? reason,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rolls back to a previously active, now-<c>retired</c> generation whose physical
    /// artifacts are still present. Fails when the target is not <c>retired</c> or its
    /// tables have been cleaned up (retention-expired). Idempotent like
    /// <see cref="PromoteAsync"/>.
    /// </summary>
    Task<NetworkTopologyPromotionRecord> RollbackAsync(
        string datasetId,
        long targetGeneration,
        long expectedActiveGeneration,
        long expectedActiveRowVersion,
        string actor,
        string? reason,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>Lists a dataset's promotion/rollback history, newest first.</summary>
    Task<IReadOnlyList<NetworkTopologyPromotionRecord>> ListHistoryAsync(
        string datasetId,
        CancellationToken cancellationToken = default);
}

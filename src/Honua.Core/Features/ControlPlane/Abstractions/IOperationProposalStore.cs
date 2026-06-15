// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;

namespace Honua.Core.Features.ControlPlane.Abstractions;

/// <summary>
/// Durable store for protocol-neutral operation proposals awaiting human
/// approval, mirroring the workflow operation store (retention, optimistic
/// version, leases, active-by-kind index) (#1692).
/// </summary>
public interface IOperationProposalStore : IOperationStore
{
    /// <summary>
    /// Attempts to create a new proposal record. Fails when a proposal with the
    /// same identifier already exists.
    /// </summary>
    /// <param name="proposal">Proposal to persist.</param>
    /// <param name="ttl">Optional retention override.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when created; false when the identifier already exists.</returns>
    Task<bool> TryCreateAsync(
        OperationProposal proposal,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a proposal by identifier.
    /// </summary>
    /// <param name="proposalId">Proposal identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The proposal, or null when not found.</returns>
    Task<OperationProposal?> GetAsync(
        string proposalId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a proposal using optimistic concurrency. The supplied proposal's
    /// <see cref="OperationProposal.Version"/> must match the stored version; the
    /// stored record is written with an incremented version on success.
    /// </summary>
    /// <param name="proposal">Proposal to persist.</param>
    /// <param name="ttl">Optional retention override.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when persisted; false on a version conflict.</returns>
    Task<bool> TrySetAsync(
        OperationProposal proposal,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists active (non-terminal) proposals, optionally filtered by kind.
    /// </summary>
    /// <param name="kind">Optional operation class filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Active proposals ordered by most recent update.</returns>
    Task<IReadOnlyList<OperationProposal>> ListActiveAsync(
        OperationClass? kind = null,
        CancellationToken cancellationToken = default);
}

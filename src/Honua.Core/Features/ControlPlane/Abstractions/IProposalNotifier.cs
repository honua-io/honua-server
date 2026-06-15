// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.ControlPlane.Abstractions;

/// <summary>
/// Publishes proposal lifecycle notifications (pending / resolved) so the console
/// can react live. Emitted from the shared gateway/store boundary, not per-endpoint
/// (#1695).
/// </summary>
public interface IProposalNotifier
{
    /// <summary>
    /// Notifies subscribers that a new proposal is pending human approval.
    /// </summary>
    /// <param name="proposal">The pending proposal.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task NotifyPendingAsync(OperationProposal proposal, CancellationToken cancellationToken = default);

    /// <summary>
    /// Notifies subscribers that a proposal was resolved (approved, rejected, or
    /// reached a terminal execution state).
    /// </summary>
    /// <param name="proposal">The resolved proposal.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task NotifyResolvedAsync(OperationProposal proposal, CancellationToken cancellationToken = default);
}

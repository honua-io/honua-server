// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.ControlPlane.Abstractions;

/// <summary>
/// Observes durable workflow-operation transitions (created / submitted / promoted / rolled-back /
/// manual-intervention). Implementations are invoked, best-effort, after the authoritative store write
/// by the transition-observing store decorator, so a single seam fans a transition out to every
/// registered consumer regardless of whether the write came from the deploy workflow service or the
/// reconciler. Consumers must be side-effect isolated: a listener throwing must never fail the write or
/// starve sibling listeners.
/// </summary>
public interface IWorkflowOperationTransitionListener
{
    /// <summary>
    /// Handles an observed workflow-operation transition.
    /// </summary>
    /// <param name="transition">The observed transition, carrying the persisted record and its classified kind.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task OnTransitionAsync(
        WorkflowOperationTransition transition,
        CancellationToken cancellationToken = default);
}

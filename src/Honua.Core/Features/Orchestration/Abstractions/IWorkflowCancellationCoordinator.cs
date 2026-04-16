// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Orchestration.Domain;

namespace Honua.Core.Features.Orchestration.Abstractions;

/// <summary>
/// Coordinates cancellation of a workflow run against the authoritative run store so the
/// reconcile loop can cascade cancellation to underlying child jobs. Exposes the engine's
/// cancel entry point across feature boundaries without leaking Server-feature types.
/// </summary>
public interface IWorkflowCancellationCoordinator
{
    /// <summary>
    /// Attempts to cancel the workflow run identified by <paramref name="runId"/>. Returns an
    /// outcome describing whether the run was missing, already terminal, already cancelled,
    /// or had cancellation recorded for subsequent reconcile-driven cascade.
    /// </summary>
    Task<WorkflowCancellationOutcome> CancelRunAsync(
        string runId,
        CancellationToken cancellationToken = default);
}

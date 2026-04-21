// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Orchestration.Domain;

namespace Honua.Core.Features.Orchestration.Abstractions;

/// <summary>
/// Durable store for workflow runs. Extends <see cref="IOperationStore"/> so orchestration
/// reconciliation can reuse the canonical lease pattern.
/// </summary>
public interface IWorkflowRunStore : IOperationStore
{
    /// <summary>
    /// Attempts to atomically create a workflow run.
    /// </summary>
    /// <returns>True when created; false when a run with the same identifier already exists.</returns>
    Task<bool> TryCreateAsync(
        WorkflowRun run,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a workflow run by identifier.
    /// </summary>
    Task<WorkflowRun?> GetAsync(
        string runId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the latest workflow run state.
    /// </summary>
    Task SetAsync(
        WorkflowRun run,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists workflow runs in a non-terminal state.
    /// </summary>
    Task<IReadOnlyList<WorkflowRun>> ListActiveAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all runs for the given workflow identifier, newest first.
    /// </summary>
    Task<IReadOnlyList<WorkflowRun>> ListByWorkflowAsync(
        string workflowId,
        CancellationToken cancellationToken = default);
}

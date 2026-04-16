// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Orchestration.Domain;

/// <summary>
/// Lifecycle status of a workflow run.
/// </summary>
public enum WorkflowRunStatus
{
    /// <summary>
    /// Run has been created but reconciliation has not started.
    /// </summary>
    Pending,

    /// <summary>
    /// Run is actively reconciling steps.
    /// </summary>
    Running,

    /// <summary>
    /// All steps reached a successful terminal state (succeeded or skipped per policy).
    /// </summary>
    Succeeded,

    /// <summary>
    /// At least one required step failed and the run cannot proceed.
    /// </summary>
    Failed,

    /// <summary>
    /// Run was cancelled before reaching a natural terminal state.
    /// </summary>
    Cancelled
}

/// <summary>
/// Lifecycle status of an individual workflow step.
/// </summary>
public enum WorkflowStepStatus
{
    /// <summary>
    /// Step has not been submitted. Its dependencies may still be outstanding.
    /// </summary>
    Pending,

    /// <summary>
    /// Step has been submitted to the job substrate and is awaiting execution.
    /// </summary>
    Queued,

    /// <summary>
    /// Step is actively running on the job substrate.
    /// </summary>
    Running,

    /// <summary>
    /// Step completed successfully.
    /// </summary>
    Succeeded,

    /// <summary>
    /// Step failed after exhausting retries.
    /// </summary>
    Failed,

    /// <summary>
    /// Step was cancelled, either directly or as a cascade of run cancellation.
    /// </summary>
    Cancelled,

    /// <summary>
    /// Step was skipped by policy after an upstream failure.
    /// </summary>
    Skipped
}

/// <summary>
/// Policy applied when a step exhausts its retry budget.
/// </summary>
public enum WorkflowStepFailurePolicy
{
    /// <summary>
    /// Fail the workflow run; cascade cancellation to non-terminal dependents.
    /// </summary>
    Fail,

    /// <summary>
    /// Mark this step skipped and cascade skips to dependents that consume its outputs.
    /// </summary>
    Skip
}

/// <summary>
/// Source that produced a workflow run.
/// </summary>
public enum WorkflowTriggerKind
{
    /// <summary>
    /// Run was created by an explicit API call.
    /// </summary>
    Manual,

    /// <summary>
    /// Run was created by the cron scheduler.
    /// </summary>
    Cron
}

/// <summary>
/// Outcome categories returned when a caller requests cancellation of a workflow run
/// via <see cref="Abstractions.IWorkflowCancellationCoordinator"/>.
/// </summary>
public enum WorkflowCancellationOutcome
{
    /// <summary>The run was not found in the durable store.</summary>
    NotFound,

    /// <summary>The run already reached a terminal non-cancelled status.</summary>
    AlreadyTerminal,

    /// <summary>The run was already cancelled.</summary>
    AlreadyCancelled,

    /// <summary>Cancellation has been recorded and will propagate on the next reconcile tick.</summary>
    CancellationRequested,

    /// <summary>
    /// The reconcile lease is currently held by another writer and could not be acquired
    /// within the bounded cancel polling window. Callers may retry after a short delay.
    /// </summary>
    LeaseContention
}

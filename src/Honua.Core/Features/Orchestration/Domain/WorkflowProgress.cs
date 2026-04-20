// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Orchestration.Domain;

/// <summary>
/// Progress projection for a workflow run, surfaced through the universal progress store.
/// </summary>
public sealed record WorkflowProgress : IOperationProgress, ICancellableOperationProgress
{
    /// <summary>
    /// Back-reference to the workflow run whose state this progress projects.
    /// </summary>
    public required string OperationId { get; init; }

    /// <summary>
    /// Back-reference to the workflow definition.
    /// </summary>
    public required string WorkflowId { get; init; }

    /// <summary>
    /// Workflow-level status of the run.
    /// </summary>
    public required WorkflowRunStatus RunStatus { get; init; }

    /// <summary>
    /// Number of steps that have reached a successful terminal state.
    /// </summary>
    public int StepsCompleted { get; init; }

    /// <summary>
    /// Total number of steps in the run.
    /// </summary>
    public int TotalSteps { get; init; }

    /// <inheritdoc />
    public double? PercentComplete => TotalSteps > 0
        ? Math.Round((double)StepsCompleted / TotalSteps * 100, 2)
        : null;

    /// <inheritdoc />
    public DateTimeOffset StartedAt { get; init; }

    /// <inheritdoc />
    public DateTimeOffset? CompletedAt { get; init; }

    /// <inheritdoc />
    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    /// <inheritdoc />
    public string? ErrorMessage { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <inheritdoc />
    public string? CurrentPhase { get; init; }

    /// <inheritdoc />
    public OperationStatus Status => RunStatus switch
    {
        WorkflowRunStatus.Pending => OperationStatus.Queued,
        WorkflowRunStatus.Running => OperationStatus.Processing,
        WorkflowRunStatus.Succeeded => OperationStatus.Completed,
        WorkflowRunStatus.Failed => OperationStatus.Failed,
        WorkflowRunStatus.Cancelled => OperationStatus.Cancelled,
        _ => OperationStatus.Queued
    };

    OperationType IOperationProgress.Type => OperationType.Orchestration;

    /// <inheritdoc />
    public IOperationProgress WithCancellation(DateTimeOffset completedAt, string? currentPhase)
        => this with
        {
            RunStatus = WorkflowRunStatus.Cancelled,
            CompletedAt = completedAt,
            CurrentPhase = currentPhase
        };

    /// <summary>
    /// Creates a progress record for a newly created run.
    /// </summary>
    public static WorkflowProgress CreateForPendingRun(
        string runId,
        string workflowId,
        int totalSteps,
        DateTimeOffset startedAt)
        => new()
        {
            OperationId = runId,
            WorkflowId = workflowId,
            RunStatus = WorkflowRunStatus.Pending,
            StepsCompleted = 0,
            TotalSteps = totalSteps,
            StartedAt = startedAt,
            CurrentPhase = "Pending"
        };
}

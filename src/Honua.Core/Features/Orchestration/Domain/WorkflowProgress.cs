// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Orchestration.Domain;

public sealed record WorkflowProgress : IOperationProgress, ICancellableOperationProgress
{
    public required string OperationId { get; init; }

    public required string WorkflowId { get; init; }

    public required WorkflowRunStatus RunStatus { get; init; }

    public int StepsCompleted { get; init; }

    public int TotalSteps { get; init; }

    public double? PercentComplete => TotalSteps > 0
        ? Math.Round((double)StepsCompleted / TotalSteps * 100, 2)
        : null;

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    public string? ErrorMessage { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string? CurrentPhase { get; init; }

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

    public IOperationProgress WithCancellation(DateTimeOffset completedAt, string? currentPhase)
        => this with
        {
            RunStatus = WorkflowRunStatus.Cancelled,
            CompletedAt = completedAt,
            CurrentPhase = currentPhase
        };

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

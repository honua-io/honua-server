// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Geoprocessing.Domain;

public sealed record GeoprocessingProgress : IOperationProgress, ICancellableOperationProgress
{
    public required string OperationId { get; init; }

    public required GeoprocessingWorkflowStatus WorkflowStatus { get; init; }

    public required GeoprocessingStageKind CurrentStage { get; init; }

    public GeoprocessingStageStatus CurrentStageStatus { get; init; }

    public string? PlanId { get; init; }

    public int StepsCompleted { get; init; }

    public int? TotalSteps { get; init; }

    public double? PercentComplete => TotalSteps > 0 ? (double)StepsCompleted / TotalSteps * 100 : null;

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    public string? ErrorMessage { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string? CurrentPhase { get; init; }

    public OperationStatus Status => WorkflowStatus switch
    {
        GeoprocessingWorkflowStatus.Draft => OperationStatus.Queued,
        GeoprocessingWorkflowStatus.AwaitingClarification => OperationStatus.Processing,
        GeoprocessingWorkflowStatus.Validated => OperationStatus.Processing,
        GeoprocessingWorkflowStatus.AwaitingApproval => OperationStatus.Processing,
        GeoprocessingWorkflowStatus.AwaitingExecution => OperationStatus.Queued,
        GeoprocessingWorkflowStatus.Running => OperationStatus.Processing,
        GeoprocessingWorkflowStatus.Completed => OperationStatus.Completed,
        GeoprocessingWorkflowStatus.Failed => OperationStatus.Failed,
        GeoprocessingWorkflowStatus.Cancelled => OperationStatus.Cancelled,
        _ => OperationStatus.Queued
    };

    // IOperationProgress explicit implementation
    string IOperationProgress.OperationId => OperationId;
    OperationType IOperationProgress.Type => OperationType.Geoprocessing;

    public IOperationProgress WithCancellation(DateTimeOffset completedAt, string? currentPhase)
        => this with
        {
            WorkflowStatus = GeoprocessingWorkflowStatus.Cancelled,
            CurrentStageStatus = GeoprocessingStageStatus.Cancelled,
            CompletedAt = completedAt,
            CurrentPhase = currentPhase
        };

    public static GeoprocessingProgress CreateInitial(string operationId)
        => new()
        {
            OperationId = operationId,
            WorkflowStatus = GeoprocessingWorkflowStatus.Draft,
            CurrentStage = GeoprocessingStageKind.CaptureIntent,
            CurrentStageStatus = GeoprocessingStageStatus.Pending,
            StartedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Capturing intent"
        };

    public static GeoprocessingProgress CreateForSubmittedJob(string operationId, string? planId = null)
        => new()
        {
            OperationId = operationId,
            WorkflowStatus = GeoprocessingWorkflowStatus.AwaitingExecution,
            CurrentStage = GeoprocessingStageKind.Execute,
            CurrentStageStatus = GeoprocessingStageStatus.Pending,
            PlanId = planId,
            StartedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Queued"
        };
}

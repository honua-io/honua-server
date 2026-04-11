// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// Progress tracking for a geoprocessing workflow, implementing the unified operation
/// progress interface.
/// </summary>
public sealed record GeoprocessingProgress : IOperationProgress, ICancellableOperationProgress
{
    /// <summary>
    /// Unique identifier for this geoprocessing operation.
    /// </summary>
    public required string OperationId { get; init; }

    /// <summary>
    /// Workflow-level lifecycle status.
    /// </summary>
    public required GeoprocessingWorkflowStatus WorkflowStatus { get; init; }

    /// <summary>
    /// Current deterministic workflow stage.
    /// </summary>
    public required GeoprocessingStageKind CurrentStage { get; init; }

    /// <summary>
    /// Status of the current stage.
    /// </summary>
    public GeoprocessingStageStatus CurrentStageStatus { get; init; }

    /// <summary>
    /// Identifier of the analysis plan being executed, when available.
    /// </summary>
    public string? PlanId { get; init; }

    /// <summary>
    /// Number of plan steps that have completed.
    /// </summary>
    public int StepsCompleted { get; init; }

    /// <summary>
    /// Total number of plan steps, when known.
    /// </summary>
    public int? TotalSteps { get; init; }

    /// <summary>
    /// Progress percentage (0-100), null if total steps are unknown.
    /// </summary>
    public double? PercentComplete => TotalSteps > 0 ? (double)StepsCompleted / TotalSteps * 100 : null;

    /// <summary>
    /// When the operation started.
    /// </summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// When the operation completed (null if still running).
    /// </summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// Duration of the operation.
    /// </summary>
    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    /// <summary>
    /// Error message if the operation failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Non-fatal warnings encountered during the operation.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Current processing phase description.
    /// </summary>
    public string? CurrentPhase { get; init; }

    // IOperationProgress explicit implementation
    string IOperationProgress.OperationId => OperationId;
    OperationType IOperationProgress.Type => OperationType.Geoprocessing;
    OperationStatus IOperationProgress.Status => WorkflowStatus switch
    {
        GeoprocessingWorkflowStatus.Draft => OperationStatus.Queued,
        GeoprocessingWorkflowStatus.AwaitingClarification => OperationStatus.Processing,
        GeoprocessingWorkflowStatus.Validated => OperationStatus.Processing,
        GeoprocessingWorkflowStatus.AwaitingApproval => OperationStatus.Processing,
        GeoprocessingWorkflowStatus.Running => OperationStatus.Processing,
        GeoprocessingWorkflowStatus.Completed => OperationStatus.Completed,
        GeoprocessingWorkflowStatus.Failed => OperationStatus.Failed,
        GeoprocessingWorkflowStatus.Cancelled => OperationStatus.Cancelled,
        _ => OperationStatus.Queued
    };

    /// <inheritdoc />
    public IOperationProgress WithCancellation(DateTimeOffset completedAt, string? currentPhase)
        => this with
        {
            WorkflowStatus = GeoprocessingWorkflowStatus.Cancelled,
            CompletedAt = completedAt,
            CurrentPhase = currentPhase
        };

    /// <summary>
    /// Creates an initial progress record for a new geoprocessing operation.
    /// </summary>
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
}

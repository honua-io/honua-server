// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.GeoETL.Domain;

/// <summary>
/// Terminal and in-flight status values for a pipeline execution. These mirror the
/// substrate's job lifecycle so a pipeline execution record stays in lockstep with
/// the underlying <see cref="Honua.Core.Features.ControlPlane.Domain.ExecutionJobStatus"/>.
/// </summary>
public enum PipelineExecutionStatus
{
    /// <summary>
    /// Execution has been recorded but the job has not started running yet.
    /// </summary>
    Pending,

    /// <summary>
    /// The stage runner is actively processing the feature stream.
    /// </summary>
    Running,

    /// <summary>
    /// The pipeline completed successfully and the sink committed its writes.
    /// </summary>
    Succeeded,

    /// <summary>
    /// The pipeline failed. <see cref="PipelineExecution.ErrorMessage"/> carries the reason.
    /// </summary>
    Failed,

    /// <summary>
    /// The pipeline was cancelled by an operator or the worker shut down.
    /// </summary>
    Cancelled
}

/// <summary>
/// Durable record of a single pipeline run. Stored in <c>honua.pipeline_executions</c>
/// and correlated to the substrate <c>ExecutionJobRecord</c> by
/// <see cref="ExecutionJobId"/>.
/// </summary>
public sealed record PipelineExecution
{
    /// <summary>
    /// Stable execution identifier. Used as the substrate operation id so the
    /// pipeline execution and the job record share one key.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Identifier of the pipeline definition that was executed.
    /// </summary>
    public required string PipelineId { get; init; }

    /// <summary>
    /// Version of the pipeline definition that was executed.
    /// </summary>
    public int PipelineVersion { get; init; }

    /// <summary>
    /// Substrate execution job id (<c>ExecutionJobRecord.OperationId</c>) backing this run.
    /// </summary>
    public required string ExecutionJobId { get; init; }

    /// <summary>
    /// Current execution status.
    /// </summary>
    public PipelineExecutionStatus Status { get; init; } = PipelineExecutionStatus.Pending;

    /// <summary>
    /// Whether this run is a dry run that routes the sink to the null preview sink and
    /// never writes to durable targets.
    /// </summary>
    public bool IsDryRun { get; init; }

    /// <summary>
    /// Number of features read from the source.
    /// </summary>
    public long FeaturesRead { get; init; }

    /// <summary>
    /// Number of features written to the sink (or counted by the dry-run null sink).
    /// </summary>
    public long FeaturesWritten { get; init; }

    /// <summary>
    /// Number of features routed to the quarantine sink due to row-level errors.
    /// </summary>
    public long FeaturesQuarantined { get; init; }

    /// <summary>
    /// Batch identifier tagged on every sink write so a failed run can soft-delete its
    /// own rows. See ADR-0038 § Rollback strategy.
    /// </summary>
    public string? BatchId { get; init; }

    /// <summary>
    /// Error message when <see cref="Status"/> is <see cref="PipelineExecutionStatus.Failed"/>.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// UTC creation timestamp.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// UTC timestamp the run reached a terminal state, or null while in flight.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; init; }
}

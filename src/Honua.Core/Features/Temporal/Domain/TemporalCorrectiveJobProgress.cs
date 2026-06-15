// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Temporal.Domain;

/// <summary>
/// Progress record for an in-process temporal corrective (governed rollback) job, persisted through
/// <see cref="Infrastructure.Abstractions.IUniversalProgressStore"/> so the job id returned by
/// <see cref="Abstractions.ITemporalCorrectiveJobSink"/> resolves to an observable status (honua-server#1593).
/// </summary>
/// <remarks>
/// Intentionally does not implement <see cref="ICancellableOperationProgress"/>: the in-process corrective
/// work cannot be interrupted once started, so the admin cancel endpoint rejects cancellation instead of
/// recording a Cancelled state that would misrepresent edits that still run to completion.
/// </remarks>
public sealed record TemporalCorrectiveJobProgress : IOperationProgress
{
    /// <summary>
    /// Job id assigned by the corrective-job sink.
    /// </summary>
    public required string OperationId { get; init; }

    /// <summary>
    /// Current status of the corrective job.
    /// </summary>
    public required OperationStatus Status { get; init; }

    /// <summary>
    /// Stable operation name recorded for the job (for example <c>temporal.rollback</c>).
    /// </summary>
    public required string OperationName { get; init; }

    /// <summary>
    /// Owning service id.
    /// </summary>
    public required string ServiceId { get; init; }

    /// <summary>
    /// Service-local layer index.
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// When the job was submitted.
    /// </summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// When the job reached a terminal state (null while queued or running).
    /// </summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// Error message when the corrective run failed or was interrupted.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Warnings recorded during the corrective run.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Current processing phase description.
    /// </summary>
    public string? CurrentPhase { get; init; }

    /// <summary>
    /// Progress percentage. Corrective edit batches are opaque, so only completion reports 100.
    /// </summary>
    public double? PercentComplete => Status == OperationStatus.Completed ? 100 : null;

    /// <summary>
    /// Duration of the corrective job so far (or total duration when terminal).
    /// </summary>
    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    OperationType IOperationProgress.Type => OperationType.TemporalCorrective;

    /// <summary>
    /// Creates the initial queued progress record for a newly submitted corrective job.
    /// </summary>
    /// <param name="jobId">Job id assigned by the sink.</param>
    /// <param name="operationName">Stable operation name recorded for the job.</param>
    /// <param name="serviceId">Owning service id.</param>
    /// <param name="layerId">Service-local layer index.</param>
    public static TemporalCorrectiveJobProgress CreateQueued(string jobId, string operationName, string serviceId, int layerId)
        => new()
        {
            OperationId = jobId,
            Status = OperationStatus.Queued,
            OperationName = operationName,
            ServiceId = serviceId,
            LayerId = layerId,
            StartedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Queued"
        };
}

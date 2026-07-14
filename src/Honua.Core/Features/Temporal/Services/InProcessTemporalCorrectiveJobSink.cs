// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Temporal.Abstractions;
using Honua.Core.Features.Temporal.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Temporal.Services;

/// <summary>
/// Default corrective-job sink (slice 5 of honua-server#1166) that runs the forward corrective operation
/// as an in-process background task and assigns it a stable job id. This keeps the rollback path durable
/// enough for the baseline deployment without requiring the Redis-backed control plane; deployments that
/// route corrective work through the durable job runner register a sink that enqueues onto it instead.
/// </summary>
/// <remarks>
/// The corrective work runs on a detached task scope so submission returns immediately with a queued
/// handle, matching the async job semantics clients poll for. Job status (Queued, Processing, and the
/// terminal Completed/Failed/Cancelled states) is persisted to <see cref="IUniversalProgressStore"/> when
/// one is registered, so the returned job id resolves through the admin operations endpoints; terminal
/// transitions use compare-and-set so they can never clobber a terminal state another writer recorded
/// (honua-server#1593). The work delegate performs only forward edits through the canonical edit pipeline
/// and never deletes change-log history; it observes the host shutdown token so an interrupted run is
/// recorded as Cancelled rather than vanishing silently.
/// </remarks>
public sealed partial class InProcessTemporalCorrectiveJobSink : ITemporalCorrectiveJobSink
{
    private static readonly TimeSpan ProgressTtl = TimeSpan.FromHours(24);

    private readonly ILogger<InProcessTemporalCorrectiveJobSink> _logger;
    private readonly IUniversalProgressStore? _progressStore;
    private readonly IHostApplicationLifetime? _lifetime;

    /// <summary>Creates the sink.</summary>
    /// <param name="logger">Logger for corrective-job lifecycle events.</param>
    /// <param name="progressStore">Optional progress store backing job-status polling for the returned job id.</param>
    /// <param name="lifetime">Optional host lifetime; when present, corrective work observes application shutdown.</param>
    public InProcessTemporalCorrectiveJobSink(
        ILogger<InProcessTemporalCorrectiveJobSink> logger,
        IUniversalProgressStore? progressStore = null,
        IHostApplicationLifetime? lifetime = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _progressStore = progressStore;
        _lifetime = lifetime;
    }

    /// <inheritdoc />
    public async Task<(string JobId, string Status)> SubmitAsync(
        string operationName,
        string serviceId,
        int layerId,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(work);

        var jobId = $"temporal-{Guid.NewGuid():N}";
        var queued = TemporalCorrectiveJobProgress.CreateQueued(jobId, operationName, serviceId, layerId);

        if (_progressStore is not null)
        {
            try
            {
                await _progressStore.SetProgressAsync(jobId, queued, ProgressTtl, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Progress tracking is observability plumbing; a store outage must not block the rollback.
                LogCorrectiveJobProgressWriteFailed(jobId, operationName, ex);
            }
        }

        var shutdownToken = _lifetime?.ApplicationStopping ?? CancellationToken.None;

        // Detach the corrective run from the request scope so submission returns a queued handle the
        // client polls via the persisted job status. Exceptions are logged and recorded as a Failed
        // terminal status rather than crashing the host.
        _ = Task.Run(
            () => RunCorrectiveJobAsync(jobId, operationName, serviceId, layerId, queued, work, shutdownToken),
            CancellationToken.None);

        return (jobId, "Queued");
    }

    private async Task RunCorrectiveJobAsync(
        string jobId,
        string operationName,
        string serviceId,
        int layerId,
        TemporalCorrectiveJobProgress queued,
        Func<CancellationToken, Task> work,
        CancellationToken shutdownToken)
    {
        var processing = queued with { Status = OperationStatus.Processing, CurrentPhase = "Running corrective edits" };
        await TryTransitionAsync(jobId, operationName, processing, OperationStatus.Queued).ConfigureAwait(false);

        try
        {
            LogCorrectiveJobStarted(jobId, operationName, serviceId, layerId);
            await work(shutdownToken).ConfigureAwait(false);
            LogCorrectiveJobSucceeded(jobId, operationName);
            await TryTransitionAsync(
                jobId,
                operationName,
                processing with
                {
                    Status = OperationStatus.Completed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    CurrentPhase = "Completed"
                },
                OperationStatus.Processing).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            LogCorrectiveJobInterrupted(jobId, operationName);
            await TryTransitionAsync(
                jobId,
                operationName,
                processing with
                {
                    Status = OperationStatus.Cancelled,
                    CompletedAt = DateTimeOffset.UtcNow,
                    ErrorMessage = "The corrective run was interrupted by host shutdown before completing.",
                    CurrentPhase = "Interrupted by shutdown"
                },
                OperationStatus.Processing).ConfigureAwait(false);
        }
        // Intentionally broad: this is the top-level handler for a background corrective job run;
        // any failure from the work delegate must be recorded as a Failed transition rather than
        // crash the job runner.
        catch (Exception ex)
        {
            LogCorrectiveJobFailed(jobId, operationName, ex);
            await TryTransitionAsync(
                jobId,
                operationName,
                processing with
                {
                    Status = OperationStatus.Failed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    ErrorMessage = ex.Message,
                    CurrentPhase = "Failed"
                },
                OperationStatus.Processing).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Compare-and-set status transition: heals a skipped intermediate transition (for example when the
    /// Queued write failed) but never clobbers a terminal state another writer recorded.
    /// </summary>
    private async Task TryTransitionAsync(
        string jobId,
        string operationName,
        TemporalCorrectiveJobProgress progress,
        OperationStatus expectedStatus)
    {
        if (_progressStore is null)
        {
            return;
        }

        try
        {
            var result = await _progressStore
                .TrySetProgressAsync(jobId, progress, expectedStatus, ProgressTtl, CancellationToken.None)
                .ConfigureAwait(false);

            if (result.Outcome == ProgressCompareAndSetOutcome.StatusMismatch &&
                result.CurrentProgress is { Status: OperationStatus.Queued or OperationStatus.Processing } observed)
            {
                result = await _progressStore
                    .TrySetProgressAsync(jobId, progress, observed.Status, ProgressTtl, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (result.Outcome != ProgressCompareAndSetOutcome.Updated)
            {
                LogCorrectiveJobStatusConflict(jobId, operationName, progress.Status, expectedStatus, result.Outcome);
            }
        }
        // Intentionally broad: this is a best-effort progress write; the corrective job itself must
        // keep running even if the progress store is unavailable or rejects the write.
        catch (Exception ex)
        {
            LogCorrectiveJobProgressWriteFailed(jobId, operationName, ex);
        }
    }

    [LoggerMessage(12130, LogLevel.Information, "Temporal corrective job {JobId} ({Operation}) started for {ServiceId}/{LayerId}")]
    private partial void LogCorrectiveJobStarted(string jobId, string operation, string serviceId, int layerId);

    [LoggerMessage(12131, LogLevel.Information, "Temporal corrective job {JobId} ({Operation}) succeeded")]
    private partial void LogCorrectiveJobSucceeded(string jobId, string operation);

    [LoggerMessage(12132, LogLevel.Error, "Temporal corrective job {JobId} ({Operation}) failed")]
    private partial void LogCorrectiveJobFailed(string jobId, string operation, Exception exception);

    [LoggerMessage(12133, LogLevel.Warning, "Temporal corrective job {JobId} ({Operation}) was interrupted by host shutdown before completing")]
    private partial void LogCorrectiveJobInterrupted(string jobId, string operation);

    [LoggerMessage(12134, LogLevel.Warning, "Temporal corrective job {JobId} ({Operation}) status transition to {TargetStatus} rejected (expected {ExpectedStatus}, outcome {Outcome}); keeping the recorded state")]
    private partial void LogCorrectiveJobStatusConflict(string jobId, string operation, OperationStatus targetStatus, OperationStatus expectedStatus, ProgressCompareAndSetOutcome outcome);

    [LoggerMessage(12135, LogLevel.Warning, "Temporal corrective job {JobId} ({Operation}) progress write failed; job status may be stale")]
    private partial void LogCorrectiveJobProgressWriteFailed(string jobId, string operation, Exception exception);
}

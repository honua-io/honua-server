// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.ControlPlane;

internal static partial class ExecutionJobSubmissionHelper
{
    internal const string SubmissionFailurePhase = "Failed (submission)";
    internal const string SubmissionFailureMessage = "Submission failed.";

    public static async Task TryRollbackCreatedJobAsync(
        IExecutionJobStore jobStore,
        string operationId,
        IUniversalProgressStore? progressStore = null,
        TimeSpan? progressRetention = null,
        string? failureMessage = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        try
        {
            var current = await jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (current == null || current.Status is not (ExecutionJobStatus.Queued or ExecutionJobStatus.Provisioning))
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var failedJob = current with
            {
                Status = ExecutionJobStatus.Failed,
                UpdatedAt = now,
                CompletedAt = now,
                ErrorMessage = failureMessage ?? SubmissionFailureMessage,
                CurrentPhase = SubmissionFailurePhase
            };

            var committed = await jobStore.TrySetAsync(failedJob, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (committed)
            {
                ControlPlaneTelemetry.RecordExecutionTransition(current, failedJob);
            }

            if (progressStore != null && committed)
            {
                await BridgeTerminalSubmissionProgressAsync(
                    progressStore, failedJob, progressRetention ?? TimeSpan.FromDays(7), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // Best-effort rollback; job TTL or manual intervention will repair.
        }
    }

    public static bool IsSubmissionRollback(ExecutionJobRecord job)
        => job.Status == ExecutionJobStatus.Failed
            && string.Equals(job.CurrentPhase, SubmissionFailurePhase, StringComparison.Ordinal);

    /// <summary>
    /// Submits an execution job to a remote batch compute backend with CAS guards
    /// around the Provisioning and post-start state transitions.
    /// </summary>
    public static async Task<ExecutionJobRecord> StartOnRemoteBackendAsync(
        ExecutionJobRecord job,
        IBatchComputeBackend backend,
        IExecutionJobStore jobStore,
        IUniversalProgressStore progressStore,
        TimeSpan progressRetention,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var provisioning = job with
        {
            Status = ExecutionJobStatus.Provisioning,
            UpdatedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Submitting to backend"
        };
        if (!await jobStore.TrySetAsync(provisioning, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            var current = await jobStore.GetAsync(job.OperationId, cancellationToken).ConfigureAwait(false);
            if (current != null)
            {
                await BridgeTerminalSubmissionProgressAsync(progressStore, current, progressRetention, logger, cancellationToken)
                    .ConfigureAwait(false);
            }

            return current ?? job;
        }

        ControlPlaneTelemetry.RecordExecutionTransition(job, provisioning);

        var submission = await backend.StartAsync(provisioning, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var mergedSpec = MergeResolvedParameters(provisioning.Spec, submission.ResolvedParameters);
        var submittedAttemptCount = provisioning.AttemptCount + 1;
        var updated = provisioning with
        {
            Status = submission.Status,
            UpdatedAt = now,
            CompletedAt = ExecutionJobReconciler.IsTerminal(submission.Status) ? now : provisioning.CompletedAt,
            ProviderOperationId = submission.ProviderOperationId ?? provisioning.ProviderOperationId,
            CurrentPhase = submission.Message ?? provisioning.CurrentPhase,
            ErrorMessage = submission.Status == ExecutionJobStatus.Failed
                ? submission.Message ?? provisioning.ErrorMessage
                : provisioning.ErrorMessage,
            AttemptCount = submittedAttemptCount,
            NextRetryAt = null,
            Spec = mergedSpec
        };

        if (!await jobStore.TrySetAsync(updated, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (logger != null)
            {
                Log.PostStartCasConflict(logger, job.OperationId);
            }

            // The provider job has already been created, so the resolved coordinates
            // (e.g., Kubernetes namespace), provider identifier, and the attempt number
            // the Job name was derived from must survive even when a concurrent writer
            // (for example a cancellation-request stamp) wins the post-start CAS. Without
            // the attempt number the retry path's "...-aN" Job name is unreachable: a
            // later observe/cancel would re-resolve coordinates from the stale AttemptCount
            // and target the previous attempt's Job instead of the one just created.
            var current = await MergeSubmissionProvenanceOnCasConflictAsync(
                jobStore, job.OperationId, submission, submittedAttemptCount, logger, cancellationToken).ConfigureAwait(false);
            if (current != null)
            {
                await BridgeTerminalSubmissionProgressAsync(progressStore, current, progressRetention, logger, cancellationToken)
                    .ConfigureAwait(false);
            }

            return current ?? updated;
        }

        ControlPlaneTelemetry.RecordExecutionTransition(provisioning, updated);

        await BridgeTerminalSubmissionProgressAsync(progressStore, updated, progressRetention, logger, cancellationToken)
            .ConfigureAwait(false);
        return updated;
    }

    /// <summary>
    /// Bridges geoprocessing progress when a backend submission returns a terminal status
    /// synchronously, before the job drops out of the active index.
    /// </summary>
    public static Task BridgeTerminalSubmissionProgressAsync(
        IUniversalProgressStore progressStore,
        ExecutionJobRecord job,
        TimeSpan retention,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        if (!ExecutionJobReconciler.IsTerminal(job.Status))
        {
            return Task.CompletedTask;
        }

        return BridgeExecutionJobProgressAsync(progressStore, job, retention, logger, cancellationToken);
    }

    /// <summary>
    /// Bridges an execution-job record into the universal progress projection regardless
    /// of terminal state. Use this after persisting any nonterminal observation (e.g., a
    /// remote backend reporting "Cancellation pending") so admin progress clients can
    /// observe the phase change without waiting for the reconciler.
    /// </summary>
    public static async Task BridgeExecutionJobProgressAsync(
        IUniversalProgressStore progressStore,
        ExecutionJobRecord job,
        TimeSpan retention,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var existing = await progressStore
                .GetProgressAsync<GeoprocessingProgress>(job.OperationId, cancellationToken)
                .ConfigureAwait(false);

            var bridged = ExecutionJobReconciler.BuildProgress(job, existing);
            if (bridged != null)
            {
                await progressStore
                    .SetProgressAsync(job.OperationId, bridged, retention, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            if (logger != null)
            {
                Log.ExecutionJobProgressBridgeFailed(logger, job.OperationId, ex);
            }
        }
    }

    private static ExecutionJobSpec MergeResolvedParameters(
        ExecutionJobSpec spec,
        IReadOnlyDictionary<string, string>? resolved)
    {
        if (resolved == null || resolved.Count == 0)
        {
            return spec;
        }

        var merged = new Dictionary<string, string>(spec.Parameters, StringComparer.Ordinal);
        foreach (var pair in resolved)
        {
            merged[pair.Key] = pair.Value;
        }

        return spec with { Parameters = merged };
    }

    // Bounded CAS loop that re-reads the authoritative record after a post-start
    // conflict and re-persists only the submission provenance the winner could not
    // have known about: resolved spec parameters, ProviderOperationId, and the
    // submitted AttemptCount with its NextRetryAt cleared. The submitted attempt
    // number identifies the "...-aN" Job name just created, so later observe/cancel
    // calls must resolve coordinates against this value rather than the winner's
    // pre-submission AttemptCount. The winner's status, cancellation request, and
    // other mutable fields are preserved. Terminal records short-circuit: no future
    // observe/cancel/retry will consult the pinned coordinates, so there is nothing
    // to fix.
    private static async Task<ExecutionJobRecord?> MergeSubmissionProvenanceOnCasConflictAsync(
        IExecutionJobStore jobStore,
        string operationId,
        BatchComputeSubmissionResult submission,
        int submittedAttemptCount,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        ExecutionJobRecord? current = null;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            current = await jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (current == null || ExecutionJobReconciler.IsTerminal(current.Status))
            {
                return current;
            }

            if (!NeedsProvenanceMerge(current, submission, submittedAttemptCount))
            {
                return current;
            }

            var mergedSpec = MergeResolvedParameters(current.Spec, submission.ResolvedParameters);
            var mergedProviderId = string.IsNullOrEmpty(current.ProviderOperationId)
                ? submission.ProviderOperationId ?? current.ProviderOperationId
                : current.ProviderOperationId;
            // Never reduce AttemptCount: another writer that legitimately advanced
            // past our submission must win. In practice the helper is the sole writer
            // of AttemptCount, so the max degenerates to submittedAttemptCount.
            var mergedAttemptCount = Math.Max(current.AttemptCount, submittedAttemptCount);
            var merged = current with
            {
                Spec = mergedSpec,
                ProviderOperationId = mergedProviderId,
                AttemptCount = mergedAttemptCount,
                NextRetryAt = null,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            if (await jobStore.TrySetAsync(merged, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                return merged;
            }
        }

        if (logger != null)
        {
            Log.PostStartProvenanceMergeExhausted(logger, operationId);
        }

        return current;
    }

    private static bool NeedsProvenanceMerge(
        ExecutionJobRecord current,
        BatchComputeSubmissionResult submission,
        int submittedAttemptCount)
    {
        if (!string.IsNullOrEmpty(submission.ProviderOperationId)
            && string.IsNullOrEmpty(current.ProviderOperationId))
        {
            return true;
        }

        if (current.AttemptCount < submittedAttemptCount)
        {
            return true;
        }

        if (current.NextRetryAt.HasValue)
        {
            return true;
        }

        if (submission.ResolvedParameters == null || submission.ResolvedParameters.Count == 0)
        {
            return false;
        }

        foreach (var pair in submission.ResolvedParameters)
        {
            if (!current.Spec.Parameters.TryGetValue(pair.Key, out var existing)
                || !string.Equals(existing, pair.Value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static partial class Log
    {
        [LoggerMessage(9040, LogLevel.Warning, "Post-start CAS conflict for execution job {OperationId}: returning authoritative store record")]
        public static partial void PostStartCasConflict(ILogger logger, string operationId);

        [LoggerMessage(9041, LogLevel.Warning, "Failed to bridge execution-job progress for {OperationId}")]
        public static partial void ExecutionJobProgressBridgeFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9042, LogLevel.Warning, "Exhausted provenance-merge retries for execution job {OperationId}; resolved parameters may not be pinned on the authoritative record")]
        public static partial void PostStartProvenanceMergeExhausted(ILogger logger, string operationId);
    }
}

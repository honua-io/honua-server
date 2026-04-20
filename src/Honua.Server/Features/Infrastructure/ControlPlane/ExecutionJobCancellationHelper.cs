// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

internal enum ExecutionJobCancellationState
{
    Cancelled,
    CancellationRequested,
    TerminalConflict,
    Missing,
    Unconfirmed
}

internal readonly record struct ExecutionJobCancellationResult(
    ExecutionJobCancellationState State,
    ExecutionJobRecord? Job);

internal enum PreSubmissionCancelOutcome
{
    Cancelled,
    TerminalConflict,
    Missing,
    Unconfirmed
}

internal readonly record struct PreSubmissionCancelResult(
    PreSubmissionCancelOutcome Outcome,
    ExecutionJobRecord? Job);

internal enum BackendCancelApplyOutcome
{
    Applied,
    Missing,
    TerminalConflict,
    Unconfirmed
}

internal readonly record struct BackendCancelApplyResult(
    BackendCancelApplyOutcome Outcome,
    ExecutionJobRecord? Job,
    ExecutionJobStatus? TerminalStatus = null);

internal static class ExecutionJobCancellationHelper
{
    public static bool IsTerminal(ExecutionJobStatus status)
        => status is ExecutionJobStatus.Succeeded
            or ExecutionJobStatus.Failed
            or ExecutionJobStatus.Cancelled;

    /// <summary>
    /// Returns <c>true</c> when the job has evidence of provider-side state,
    /// meaning the backend has seen the job and a remote cancel is valid.
    /// </summary>
    public static bool WasSubmittedToProvider(ExecutionJobRecord job)
        => !string.IsNullOrEmpty(job.ProviderOperationId) || job.AttemptCount > 0;

    public static async Task<PreSubmissionCancelResult> TryCancelPreSubmissionAsync(
        IExecutionJobStore jobStore,
        ExecutionJobRecord job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobStore);

        var cancelNow = DateTimeOffset.UtcNow;
        var cancelled = job with
        {
            Status = ExecutionJobStatus.Cancelled,
            UpdatedAt = cancelNow,
            CompletedAt = cancelNow,
            CurrentPhase = "Cancelled before submission"
        };

        if (await jobStore.TrySetAsync(cancelled, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            ControlPlaneTelemetry.RecordExecutionTransition(job, cancelled);
            return new(PreSubmissionCancelOutcome.Cancelled, cancelled);
        }

        var fresh = await jobStore.GetAsync(job.OperationId, cancellationToken).ConfigureAwait(false);
        if (fresh == null)
        {
            return new(PreSubmissionCancelOutcome.Missing, null);
        }

        if (fresh.Status == ExecutionJobStatus.Cancelled)
        {
            return new(PreSubmissionCancelOutcome.Cancelled, fresh);
        }

        if (IsTerminal(fresh.Status))
        {
            return new(PreSubmissionCancelOutcome.TerminalConflict, fresh);
        }

        return new(PreSubmissionCancelOutcome.Unconfirmed, fresh);
    }

    public static async Task<ExecutionJobCancellationResult> TryApplyAsync(
        IExecutionJobStore jobStore,
        string operationId,
        ExecutionJobRecord initialJob,
        string cancelledPhase,
        int maxAttempts = 3,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        var current = initialJob;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (current.Status == ExecutionJobStatus.Cancelled)
            {
                return new(ExecutionJobCancellationState.Cancelled, current);
            }

            if (IsTerminal(current.Status))
            {
                return new(ExecutionJobCancellationState.TerminalConflict, current);
            }

            var now = DateTimeOffset.UtcNow;
            if (current.ClaimedBy != null)
            {
                if (current.CancellationRequestedAt.HasValue)
                {
                    return new(ExecutionJobCancellationState.CancellationRequested, current);
                }

                var requested = current with
                {
                    CancellationRequestedAt = now,
                    UpdatedAt = now
                };

                if (await jobStore.TrySetAsync(requested, cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    ControlPlaneTelemetry.RecordExecutionTransition(current, requested);
                    return new(ExecutionJobCancellationState.CancellationRequested, requested);
                }
            }
            else
            {
                var cancelled = current with
                {
                    Status = ExecutionJobStatus.Cancelled,
                    UpdatedAt = now,
                    CompletedAt = now,
                    CurrentPhase = cancelledPhase
                };

                if (await jobStore.TrySetAsync(cancelled, cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    ControlPlaneTelemetry.RecordExecutionTransition(current, cancelled);
                    return new(ExecutionJobCancellationState.Cancelled, cancelled);
                }
            }

            current = await jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (current == null)
            {
                return new(ExecutionJobCancellationState.Missing, null);
            }
        }

        if (current.Status == ExecutionJobStatus.Cancelled)
        {
            return new(ExecutionJobCancellationState.Cancelled, current);
        }

        if (IsTerminal(current.Status))
        {
            return new(ExecutionJobCancellationState.TerminalConflict, current);
        }

        if (current.ClaimedBy != null && current.CancellationRequestedAt.HasValue)
        {
            return new(ExecutionJobCancellationState.CancellationRequested, current);
        }

        return new(ExecutionJobCancellationState.Unconfirmed, current);
    }

    /// <summary>
    /// Merges a backend cancel observation onto an execution-job record using the same
    /// no-op invariant as the reconciler's <c>ApplyObservation</c>: when the observable
    /// fields are unchanged and a durable <see cref="ExecutionJobRecord.CancellationRequestedAt"/>
    /// already exists, the original record is returned so repeated idempotent cancel
    /// attempts do not refresh <see cref="ExecutionJobRecord.UpdatedAt"/> and extend the
    /// missing-registration grace window.
    /// </summary>
    public static ExecutionJobRecord MergeBackendCancelObservation(
        ExecutionJobRecord job,
        BatchComputeObservation observation,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(observation);

        var status = observation.Status;
        var providerId = observation.ProviderOperationId ?? job.ProviderOperationId;
        var percent = observation.PercentComplete ?? job.PercentComplete;
        var phase = observation.Message ?? job.CurrentPhase;
        var errorMessage = status == ExecutionJobStatus.Failed
            ? observation.Message ?? job.ErrorMessage
            : job.ErrorMessage;
        var terminal = IsTerminal(status);
        var newCancellationRequestedAt = terminal
            ? job.CancellationRequestedAt
            : (job.CancellationRequestedAt ?? now);

        if (status == job.Status
            && string.Equals(providerId, job.ProviderOperationId, StringComparison.Ordinal)
            && Math.Abs((percent ?? 0d) - (job.PercentComplete ?? 0d)) < 0.0001
            && string.Equals(phase, job.CurrentPhase, StringComparison.Ordinal)
            && string.Equals(errorMessage, job.ErrorMessage, StringComparison.Ordinal)
            && newCancellationRequestedAt == job.CancellationRequestedAt)
        {
            return job;
        }

        return job with
        {
            Status = status,
            UpdatedAt = now,
            CompletedAt = terminal ? now : job.CompletedAt,
            ProviderOperationId = providerId,
            PercentComplete = percent,
            CurrentPhase = phase,
            ErrorMessage = errorMessage,
            CancellationRequestedAt = newCancellationRequestedAt
        };
    }

    /// <summary>
    /// Applies a backend cancel observation to the durable job record with a CAS retry,
    /// preserving <c>UpdatedAt</c> for no-op observations and emitting the canonical
    /// execution-job transition telemetry for status changes. Centralizes the API-side
    /// remote cancel flow used by the geoprocessing service, admin operations, and the
    /// OGC Processes dismiss endpoint.
    /// </summary>
    public static async Task<BackendCancelApplyResult> TryApplyBackendCancelAsync(
        IExecutionJobStore jobStore,
        ExecutionJobRecord job,
        BatchComputeObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobStore);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(observation);

        var now = DateTimeOffset.UtcNow;
        var merged = MergeBackendCancelObservation(job, observation, now);

        if (ReferenceEquals(merged, job))
        {
            return new(BackendCancelApplyOutcome.Applied, job);
        }

        if (await jobStore.TrySetAsync(merged, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            ControlPlaneTelemetry.RecordExecutionTransition(job, merged);
            return new(BackendCancelApplyOutcome.Applied, merged);
        }

        var fresh = await jobStore.GetAsync(job.OperationId, cancellationToken).ConfigureAwait(false);
        if (fresh == null)
        {
            return new(BackendCancelApplyOutcome.Missing, null);
        }

        if (fresh.Status == ExecutionJobStatus.Cancelled)
        {
            return new(BackendCancelApplyOutcome.Applied, fresh);
        }

        if (IsTerminal(fresh.Status))
        {
            return new(BackendCancelApplyOutcome.TerminalConflict, fresh, fresh.Status);
        }

        var retryNow = DateTimeOffset.UtcNow;
        var retryMerged = MergeBackendCancelObservation(fresh, observation, retryNow);

        if (ReferenceEquals(retryMerged, fresh))
        {
            return new(BackendCancelApplyOutcome.Applied, fresh);
        }

        if (!await jobStore.TrySetAsync(retryMerged, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return new(BackendCancelApplyOutcome.Unconfirmed, fresh);
        }

        ControlPlaneTelemetry.RecordExecutionTransition(fresh, retryMerged);
        return new(BackendCancelApplyOutcome.Applied, retryMerged);
    }
}

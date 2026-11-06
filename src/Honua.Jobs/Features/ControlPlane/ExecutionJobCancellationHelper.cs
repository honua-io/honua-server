// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

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

internal enum RemoteCancelStampOutcome
{
    Stamped,
    AlreadyStamped,
    TerminalConflict,
    Missing,
    Unconfirmed
}

internal readonly record struct RemoteCancelStampResult(
    RemoteCancelStampOutcome Outcome,
    ExecutionJobRecord? Job,
    ExecutionJobStatus? TerminalStatus = null);

internal static class ExecutionJobCancellationHelper
{
    public static bool IsTerminal(ExecutionJobStatus status)
        => status is ExecutionJobStatus.Succeeded
            or ExecutionJobStatus.Failed
            or ExecutionJobStatus.Cancelled;

    public static bool HasSubmittedProviderMarker(ExecutionJobRecord job)
        => !string.IsNullOrEmpty(job.ProviderOperationId)
            || (job.AttemptCount > 0 && !job.NextRetryAt.HasValue);

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

    // CancellationRequestedAt is stamped before the first remote cancel so a NotFound race is recorded as cancelled rather than failed.
    public static async Task<RemoteCancelStampResult> TryStampRemoteCancelRequestedAtAsync(
        IExecutionJobStore jobStore,
        ExecutionJobRecord job,
        int maxAttempts = 3,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobStore);
        ArgumentNullException.ThrowIfNull(job);

        var current = job;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (IsTerminal(current.Status))
            {
                return new(RemoteCancelStampOutcome.TerminalConflict, current, current.Status);
            }

            if (current.CancellationRequestedAt.HasValue)
            {
                return new(RemoteCancelStampOutcome.AlreadyStamped, current);
            }

            var now = DateTimeOffset.UtcNow;
            var stamped = current with
            {
                CancellationRequestedAt = now,
                UpdatedAt = now
            };

            if (await jobStore.TrySetAsync(stamped, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                return new(RemoteCancelStampOutcome.Stamped, stamped);
            }

            var fresh = await jobStore.GetAsync(job.OperationId, cancellationToken).ConfigureAwait(false);
            if (fresh == null)
            {
                return new(RemoteCancelStampOutcome.Missing, null);
            }
            current = fresh;
        }

        if (IsTerminal(current.Status))
        {
            return new(RemoteCancelStampOutcome.TerminalConflict, current, current.Status);
        }

        if (current.CancellationRequestedAt.HasValue)
        {
            return new(RemoteCancelStampOutcome.AlreadyStamped, current);
        }

        return new(RemoteCancelStampOutcome.Unconfirmed, current);
    }

    // An unchanged cancel observation keeps the original record so UpdatedAt does not extend the missing-registration grace window.
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

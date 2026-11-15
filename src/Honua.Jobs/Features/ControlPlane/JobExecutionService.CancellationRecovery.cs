// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

internal sealed partial class JobExecutionService
{
    private const string OutcomeRecoveryPhase = "Recovering committed import outcome";

    private async Task<bool> TryRecoverPendingOutcomeAsync(
        ExecutionJobRecord job, string workerId, CancellationToken cancellationToken)
    {
        if (job.AttemptCount <= 1 ||
            ResolveExecutor(job.Spec.Kind, job.Spec.RuntimeProfile) is not IJobOutcomeRecoveryExecutor)
        {
            return false;
        }

        if (await TryRecoverOutcomeAsync(job, workerId, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (job.AttemptCount > (job.RetryPolicy ?? JobRetryPolicy.Default).MaxAttempts)
        {
            await TerminateJobAsync(job.OperationId, workerId, ExecutionJobStatus.Failed,
                "Execution retry budget exhausted; no committed import outcome was found.", cancellationToken, recoverOutcome: false).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private async Task<bool> TryRecoverOutcomeAsync(
        ExecutionJobRecord job, string workerId, CancellationToken cancellationToken, bool recoveryOnly = false)
    {
        if (ResolveExecutor(job.Spec.Kind, job.Spec.RuntimeProfile) is not IJobOutcomeRecoveryExecutor recovery)
        {
            return false;
        }

        var licenseCancellation = licensePolicy?.OperationCancellation ?? CancellationToken.None;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var recoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            budget.Token, cancellationToken, licenseCancellation);
        using var context = new JobExecutionContext(job.OperationId, workerId, jobStore, logStore,
            job.HeartbeatPolicy ?? JobHeartbeatPolicy.Default, null, logger, job.AttemptCount, licenseCancellation);
        try
        {
            licenseCancellation.ThrowIfCancellationRequested();
            var result = await recovery.RecoverOutcomeAsync(job, context, recoveryCancellation.Token).ConfigureAwait(false);
            licenseCancellation.ThrowIfCancellationRequested();
            if (result is null)
            {
                return false;
            }

            await FinalizeOutcomeRecoveryAsync(job, result, recoveryCancellation.Token, recoveryOnly).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (licenseCancellation.IsCancellationRequested)
        {
            // License expiry stops execution and publication, but cannot establish whether COMMIT landed.
            // Keep the receipt lookup pending until a valid license permits recovery.
            await RequeueOutcomeRecoveryAsync(job, workerId, recoveryOnly).ConfigureAwait(false);
        }
        // An unavailable receipt is an unknown outcome, never evidence that no rows committed.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.JobExecutionFailed(logger, job.OperationId, ex);
            await RequeueOutcomeRecoveryAsync(job, workerId, recoveryOnly).ConfigureAwait(false);
        }

        return true;
    }

    private async Task FinalizeOutcomeRecoveryAsync(
        ExecutionJobRecord job, JobExecutionResult result, CancellationToken cancellationToken, bool recoveryOnly)
    {
        var workerId = job.ClaimedBy!;
        if (!await FinalizeJobAsync(job.OperationId, workerId, result, null, null, cancellationToken).ConfigureAwait(false))
        {
            await RequeueOutcomeRecoveryAsync(job, workerId, recoveryOnly).ConfigureAwait(false);
        }
    }

    private async Task RequeueOutcomeRecoveryAsync(ExecutionJobRecord original, string workerId, bool recoveryOnly)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var current = await jobStore.GetAsync(original.OperationId, cleanup.Token).ConfigureAwait(false);
        if (current is null || IsTerminalOrNotOwnedBy(current, workerId) || current.AttemptCount != original.AttemptCount)
        {
            return;
        }

        var delay = TimeSpan.FromSeconds(5);
        var now = DateTimeOffset.UtcNow;
        var queued = current with
        {
            Status = ExecutionJobStatus.Queued,
            ClaimedBy = null,
            ClaimedAt = null,
            LastHeartbeatAt = null,
            UpdatedAt = now,
            CompletedAt = null,
            NextRetryAt = now.Add(delay),
            RetryPolicy = recoveryOnly
                ? (current.RetryPolicy ?? JobRetryPolicy.Default) with { MaxAttempts = current.AttemptCount }
                : current.RetryPolicy,
            CurrentPhase = OutcomeRecoveryPhase
        };
        if (!await jobStore.TrySetAsync(queued, cancellationToken: cleanup.Token).ConfigureAwait(false))
        {
            return;
        }

        ControlPlaneTelemetry.RecordExecutionTransition(current, queued);
        cancellationTokens.Remove(current.OperationId, workerId);
        await jobQueue.RequeueAsync(current.OperationId, current.Priority, delay, cleanup.Token).ConfigureAwait(false);
    }
}

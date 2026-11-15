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
            await TerminateUncommittedJobAsync(new(job.OperationId, workerId, ExecutionJobStatus.Failed,
                "Execution retry budget exhausted; no committed import outcome was found."), cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private Task<bool> TryRecoverOutcomeAsync(
        ExecutionJobRecord job, string workerId, CancellationToken cancellationToken)
        => TryRecoverAttemptOutcomeAsync(new(job, workerId, OutcomeRecoveryMode.Execution), cancellationToken);

    private Task<bool> TryRecoverTerminalOutcomeAsync(
        ExecutionJobRecord job, string workerId, CancellationToken cancellationToken)
        => TryRecoverAttemptOutcomeAsync(new(job, workerId, OutcomeRecoveryMode.ReceiptOnly), cancellationToken);

    private async Task<bool> TryRecoverAttemptOutcomeAsync(OutcomeRecoveryAttempt attempt, CancellationToken cancellationToken)
    {
        if (ResolveExecutor(attempt.Job.Spec.Kind, attempt.Job.Spec.RuntimeProfile) is not IJobOutcomeRecoveryExecutor recovery)
        {
            return false;
        }

        var licenseCancellation = licensePolicy?.OperationCancellation ?? CancellationToken.None;
        try
        {
            return await RecoverCommittedOutcomeAsync(attempt, recovery, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (licenseCancellation.IsCancellationRequested)
        {
            // License expiry cannot establish whether COMMIT landed; retain the receipt lookup.
            await RequeueOutcomeRecoveryAsync(attempt).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An unavailable receipt is an unknown outcome, never proof that no rows committed.
            Log.JobExecutionFailed(logger, attempt.Job.OperationId, ex);
            await RequeueOutcomeRecoveryAsync(attempt).ConfigureAwait(false);
        }

        return true;
    }

    private async Task<bool> RecoverCommittedOutcomeAsync(
        OutcomeRecoveryAttempt attempt, IJobOutcomeRecoveryExecutor recovery, CancellationToken cancellationToken)
    {
        var job = attempt.Job;
        var licenseCancellation = licensePolicy?.OperationCancellation ?? CancellationToken.None;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var recoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            budget.Token, cancellationToken, licenseCancellation);
        using var context = new JobExecutionContext(job.OperationId, attempt.WorkerId, jobStore, logStore,
            job.HeartbeatPolicy ?? JobHeartbeatPolicy.Default, null, logger, job.AttemptCount, licenseCancellation);
        licenseCancellation.ThrowIfCancellationRequested();
        var result = await recovery.RecoverOutcomeAsync(job, context, recoveryCancellation.Token).ConfigureAwait(false);
        licenseCancellation.ThrowIfCancellationRequested();
        if (result is null)
        {
            return false;
        }

        if (!await FinalizeJobAsync(job.OperationId, job.ClaimedBy!, result, null, null, recoveryCancellation.Token).ConfigureAwait(false))
        {
            await RequeueOutcomeRecoveryAsync(attempt).ConfigureAwait(false);
        }

        return true;
    }

    private sealed record OutcomeRecoveryAttempt(ExecutionJobRecord Job, string WorkerId, OutcomeRecoveryMode Mode);

    private enum OutcomeRecoveryMode
    {
        Execution,
        ReceiptOnly
    }

    private async Task RequeueOutcomeRecoveryAsync(OutcomeRecoveryAttempt attempt)
    {
        var original = attempt.Job;
        var workerId = attempt.WorkerId;
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
            RetryPolicy = attempt.Mode == OutcomeRecoveryMode.ReceiptOnly
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

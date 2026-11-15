// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

internal sealed partial class JobExecutionService
{
    private async Task<bool> TryRecoverOutcomeAsync(
        ExecutionJobRecord job, string workerId, CancellationToken cancellationToken)
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

            await FinalizeOutcomeRecoveryAsync(job, result, recoveryCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (licenseCancellation.IsCancellationRequested)
        {
            await TerminateJobAsync(job.OperationId, workerId, ExecutionJobStatus.Failed,
                "license expired", CancellationToken.None).ConfigureAwait(false);
        }
        // An unavailable receipt is an unknown outcome, never evidence that no rows committed.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.JobExecutionFailed(logger, job.OperationId, ex);
            await RequeueOutcomeRecoveryAsync(job, workerId).ConfigureAwait(false);
        }

        return true;
    }

    private async Task FinalizeOutcomeRecoveryAsync(
        ExecutionJobRecord job, JobExecutionResult result, CancellationToken cancellationToken)
    {
        var workerId = job.ClaimedBy!;
        if (!await FinalizeJobAsync(job.OperationId, workerId, result, null, null, cancellationToken).ConfigureAwait(false))
        {
            await RequeueOutcomeRecoveryAsync(job, workerId).ConfigureAwait(false);
        }
    }

    private async Task RequeueOutcomeRecoveryAsync(ExecutionJobRecord original, string workerId)
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
            CurrentPhase = "Recovering committed import outcome"
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

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

internal sealed partial class JobExecutionService
{
    internal const string CommittedCancellationWarning = "Cancellation requested after sink data committed; committed-effect receipts are retained.";

    private sealed record JobTermination(string OperationId, string WorkerId, ExecutionJobStatus Status, string Reason);

    private async Task TerminateJobAsync(JobTermination termination, CancellationToken cancellationToken)
    {
        var job = await GetTerminableJobAsync(termination, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return;
        }

        // A success admitted by GetTerminableJobAsync is owned by this worker and raced
        // license expiry. Its outcome is already known; scrub it without another lookup.
        if (job.Status != ExecutionJobStatus.Succeeded &&
            await TryRecoverTerminalOutcomeAsync(job, termination.WorkerId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await PersistTerminationAsync(job, termination, cancellationToken).ConfigureAwait(false);
    }

    private async Task TerminateUncommittedJobAsync(JobTermination termination, CancellationToken cancellationToken)
    {
        var job = await GetTerminableJobAsync(termination, cancellationToken).ConfigureAwait(false);
        if (job is not null)
        {
            await PersistTerminationAsync(job, termination, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ExecutionJobRecord?> GetTerminableJobAsync(JobTermination termination, CancellationToken cancellationToken)
    {
        var job = await jobStore.GetAsync(termination.OperationId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return null;
        }

        var lateExpiredSuccess = termination.Reason == "license expired" && job.Status == ExecutionJobStatus.Succeeded &&
            string.Equals(job.ClaimedBy, termination.WorkerId, StringComparison.Ordinal);
        if (IsTerminalOrNotOwnedBy(job, termination.WorkerId) && !lateExpiredSuccess)
        {
            Log.TerminalStateSkipped(logger, termination.OperationId, job.Status.ToString());
            return null;
        }

        return job;
    }

    private async Task PersistTerminationAsync(ExecutionJobRecord job, JobTermination termination, CancellationToken cancellationToken)
    {
        var terminal = CreateTerminatedJob(job, termination);
        if (!await jobStore.TrySetAsync(terminal, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            Log.TerminalStateSkipped(logger, job.OperationId, "CAS conflict on terminate");
            cancellationTokens.Remove(job.OperationId, termination.WorkerId);
            return;
        }

        ControlPlaneTelemetry.RecordExecutionTransition(job, terminal);
        cancellationTokens.Remove(job.OperationId, termination.WorkerId);
        await CompleteTerminationAsync(terminal, cancellationToken).ConfigureAwait(false);
    }

    private static ExecutionJobRecord CreateTerminatedJob(ExecutionJobRecord job, JobTermination termination)
    {
        var now = DateTimeOffset.UtcNow;
        return job with
        {
            Status = termination.Status,
            UpdatedAt = now,
            CompletedAt = now,
            ErrorMessage = termination.Status == ExecutionJobStatus.Cancelled && job.CommittedEffectReferences.Count > 0
                ? CommittedCancellationWarning : termination.Reason,
            Warnings = termination.Status == ExecutionJobStatus.Cancelled && job.CommittedEffectReferences.Count > 0
                ? [.. job.Warnings, CommittedCancellationWarning] : job.Warnings,
            ArtifactReferences = termination.Reason switch
            {
                "license expired" => [],
                DrainDeadlineFailureMessage => job.CommittedEffectReferences,
                _ => job.ArtifactReferences
            },
            CommittedEffectReferences = termination.Reason == "license expired" ? [] : job.CommittedEffectReferences,
            PercentComplete = termination.Reason is "license expired" or DrainDeadlineFailureMessage ? null : job.PercentComplete,
            CurrentPhase = termination.Status == ExecutionJobStatus.Cancelled
                ? job.CommittedEffectReferences.Count > 0 ? "Cancelled after committed effects" : "Cancelled"
                : "Failed"
        };
    }

    private async Task CompleteTerminationAsync(ExecutionJobRecord terminal, CancellationToken cancellationToken)
    {
        // Both catches below are deliberately broad — see FinalizeJobAsync above for the
        // same best-effort cleanup rationale.
        try
        {
            await jobQueue.RemoveAsync(terminal.OperationId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.QueueRemovalFailed(logger, terminal.OperationId, ex);
        }

        if (logStore != null)
        {
            try
            {
                await logStore.SetRetentionAsync(terminal.OperationId, LogRetention, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.LogRetentionFailed(logger, terminal.OperationId, ex);
            }
        }

        await NotifyTerminalAsync(terminal, cancellationToken).ConfigureAwait(false);
        Log.JobExecutionCompleted(logger, terminal.OperationId, terminal.Status.ToString());
    }
}

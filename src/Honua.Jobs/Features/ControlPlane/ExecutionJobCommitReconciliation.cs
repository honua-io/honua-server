// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

// Every state transition uses actual ownership/version and immutable intent. A
// replacement claim revokes the old attempt before the shared DB decision lock
// can prove absence; the import commit authority checks that claimed lineage.
internal static class ExecutionJobCommitReconciliation
{
    internal const string PendingPhase = "Reconciling committed import outcome";
    internal static bool IsPending(ExecutionJobRecord job) => job.CommitRecovery is not null;

    internal static async Task<ExecutionJobRecord> SetPendingAsync(IExecutionJobStore store, ExecutionJobRecord claimed,
        bool pending, CancellationToken cancellationToken, bool startExecution = false)
    {
        var intent = JobExecutionLeaseIntentFingerprint.Compute(claimed);
        for (var retry = 0; retry < 10; retry++)
        {
            var actual = await store.GetAsync(claimed.OperationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The claimed import disappeared during commit reconciliation.");
            RequireClaim(actual, claimed, intent);
            if (actual.CommitRecovery is { } old && old.IntentFingerprint != intent)
                throw new InvalidOperationException("The unresolved commit belongs to a changed immutable intent.");
            var state = pending ? (actual.CommitRecovery ?? new(intent, claimed.ClaimedBy!, claimed.AttemptCount, DateTimeOffset.UtcNow))
                with
            {
                WorkerId = claimed.ClaimedBy!,
                AttemptCount = claimed.AttemptCount,
                ExecutionStarted = startExecution || actual.CommitRecovery?.ExecutionStarted == true
            } : null;
            var starting = startExecution && !(actual.CommitRecovery is { ExecutionStarted: true } existing
                && existing.WorkerId == claimed.ClaimedBy && existing.AttemptCount == claimed.AttemptCount);
            var updated = actual with
            {
                CommitRecovery = state,
                ImportExecutionAttemptCount = starting ? checked(actual.ImportExecutionAttemptCount + 1) : actual.ImportExecutionAttemptCount,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            if (await store.TrySetAsync(updated, cancellationToken: cancellationToken).ConfigureAwait(false)) return updated;
        }
        throw new InvalidOperationException("Commit reconciliation lost its current claim CAS.");
    }

    internal static async Task<bool> RequeueAsync(IExecutionJobStore store, IJobQueue queue,
        ExecutionJobRecord expected, CancellationToken cancellationToken, ExecutionJobStatus? deferredStatus = null,
        bool retryAllowed = true, bool forceRetry = false, string? reason = null)
    {
        var current = await store.GetAsync(expected.OperationId, cancellationToken).ConfigureAwait(false);
        if (current is null || !IsPending(current) || current.Status is ExecutionJobStatus.Succeeded or ExecutionJobStatus.Failed or ExecutionJobStatus.Cancelled
            || current.ClaimedBy != expected.ClaimedBy || current.AttemptCount != expected.AttemptCount
            || JobExecutionLeaseIntentFingerprint.Compute(current) != JobExecutionLeaseIntentFingerprint.Compute(expected)) return false;
        var now = DateTimeOffset.UtcNow;
        var queued = current with
        {
            CommitRecovery = current.CommitRecovery! with
            {
                DeferredStatus = current.CommitRecovery.DeferredStatus ?? deferredStatus,
                RetryAllowed = current.CommitRecovery.RetryAllowed && retryAllowed,
                ForceRetry = current.CommitRecovery.ForceRetry || forceRetry,
                FailureMessage = current.CommitRecovery.FailureMessage ?? (reason is { Length: <= 2048 } ? reason : null)
            },
            Status = ExecutionJobStatus.Queued,
            ClaimedBy = null,
            ClaimedAt = null,
            LastHeartbeatAt = null,
            UpdatedAt = now,
            CompletedAt = null,
            CurrentPhase = PendingPhase,
            NextRetryAt = now.AddSeconds(5),
            ErrorMessage = "The database commit outcome has not yet been reconciled."
        };
        if (!await store.TrySetAsync(queued, cancellationToken: cancellationToken).ConfigureAwait(false)) return false;
        await queue.RequeueAsync(queued.OperationId, queued.Priority, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static void RequireClaim(ExecutionJobRecord actual, ExecutionJobRecord claimed, string intent)
    {
        if (actual.Spec.Kind != ExecutionJobKind.FileImport || actual.Audit.SubmitterSecurityContext?.ExecutionLease is null
            || string.IsNullOrWhiteSpace(claimed.ClaimedBy)
            || actual.ClaimedBy != claimed.ClaimedBy || actual.AttemptCount != claimed.AttemptCount
            || actual.Status is not (ExecutionJobStatus.Running or ExecutionJobStatus.Provisioning)
            || JobExecutionLeaseIntentFingerprint.Compute(actual) != intent
            || actual.Audit.SubmitterSecurityContext?.ExecutionLease != claimed.Audit.SubmitterSecurityContext?.ExecutionLease)
            throw new InvalidOperationException("The import commit reconciliation attempt is no longer current.");
    }
}

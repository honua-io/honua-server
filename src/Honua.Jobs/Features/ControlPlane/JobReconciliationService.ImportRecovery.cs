// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

internal sealed partial class JobReconciliationService
{
    private Task<bool> RequeueExpiredImportRecoveryAsync(
        ExecutionJobRecord current, DateTimeOffset now, CancellationToken cancellationToken)
        // Expired executions may recover a receipt, but must never execute another import.
        => RequeueImportRecoveryAsync(current with
        {
            RetryPolicy = (current.RetryPolicy ?? JobRetryPolicy.Default) with { MaxAttempts = current.AttemptCount }
        }, now, cancellationToken);

    private static readonly TimeSpan ImportRecoveryDelay = TimeSpan.FromSeconds(5);

    private async Task<bool> RequeueImportRecoveryAsync(
        ExecutionJobRecord current, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!await TryMarkImportRecoveryAsync(current, now, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await jobQueue.RequeueAsync(current.OperationId, current.Priority, ImportRecoveryDelay, cancellationToken).ConfigureAwait(false);
        return true;
    }

    // A heartbeat expiry lost the running attempt: record it as a worker-lost event, as the retry path does.
    private async Task<bool> RequeueLostImportRecoveryAsync(
        ExecutionJobRecord current, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!await TryMarkImportRecoveryAsync(current, now, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            await jobQueue.RequeueAsync(current.OperationId, current.Priority, ImportRecoveryDelay, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await RecordLostAttemptAsync(current, now, (current.RetryPolicy ?? JobRetryPolicy.Default).MaxAttempts).ConfigureAwait(false);
        }

        return true;
    }

    private async Task<bool> TryMarkImportRecoveryAsync(
        ExecutionJobRecord current, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var queued = current with
        {
            Status = ExecutionJobStatus.Queued,
            ClaimedBy = null,
            ClaimedAt = null,
            LastHeartbeatAt = null,
            UpdatedAt = now,
            CompletedAt = null,
            NextRetryAt = now.Add(ImportRecoveryDelay),
            CurrentPhase = "Recovering committed import outcome"
        };
        if (!await jobStore.TrySetAsync(queued, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        ControlPlaneTelemetry.RecordExecutionTransition(current, queued);
        cancellationTokens.Revoke(current.OperationId, current.ClaimedBy!);
        return true;
    }
}

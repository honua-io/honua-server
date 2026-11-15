// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

internal sealed partial class JobReconciliationService
{
    private async Task<bool> RequeueImportRecoveryAsync(
        ExecutionJobRecord current, DateTimeOffset now, CancellationToken cancellationToken,
        bool executionExpired = false)
    {
        var delay = TimeSpan.FromSeconds(5);
        var queued = current with
        {
            Status = ExecutionJobStatus.Queued,
            ClaimedBy = null,
            ClaimedAt = null,
            LastHeartbeatAt = null,
            UpdatedAt = now,
            CompletedAt = null,
            NextRetryAt = now.Add(delay),
            // An expired execution may recover a receipt, but must never run the import again.
            RetryPolicy = executionExpired
                ? (current.RetryPolicy ?? JobRetryPolicy.Default) with { MaxAttempts = current.AttemptCount }
                : current.RetryPolicy,
            CurrentPhase = "Recovering committed import outcome"
        };
        if (!await jobStore.TrySetAsync(queued, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        ControlPlaneTelemetry.RecordExecutionTransition(current, queued);
        cancellationTokens.Revoke(current.OperationId, current.ClaimedBy!);
        await jobQueue.RequeueAsync(current.OperationId, current.Priority, delay, cancellationToken).ConfigureAwait(false);
        return true;
    }
}

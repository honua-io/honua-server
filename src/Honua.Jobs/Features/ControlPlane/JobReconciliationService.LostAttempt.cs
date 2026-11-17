// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

// A worker-loss retry clears the claim fields, so each lost attempt is kept as one event in
// the job log. The record's warnings belong to the attempt that finishes and are replaced then.
internal sealed partial class JobReconciliationService
{
    private const string LostAttemptWorkerMetadataKey = "worker.id";

    private static string DescribeLostAttempt(ExecutionJobRecord lost, DateTimeOffset endedAt, int maxAttempts)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"Attempt {lost.AttemptCount} of {maxAttempts} lost: the worker stopped heartbeating (claimed {FormatTime(lost.ClaimedAt)}, last heartbeat {FormatTime(lost.LastHeartbeatAt)}, ended {FormatTime(endedAt)}).");

    private async Task RecordLostAttemptAsync(ExecutionJobRecord lost, DateTimeOffset endedAt, int maxAttempts)
    {
        if (logStore is null)
        {
            return;
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["attempt"] = lost.AttemptCount.ToString(CultureInfo.InvariantCulture),
            ["attempt.max"] = maxAttempts.ToString(CultureInfo.InvariantCulture),
            ["attempt.claimedAt"] = FormatTime(lost.ClaimedAt),
            ["attempt.lastHeartbeatAt"] = FormatTime(lost.LastHeartbeatAt),
            ["attempt.endedAt"] = FormatTime(endedAt),
            ["failure.classification"] = "worker_lost"
        };
        if (!string.IsNullOrWhiteSpace(lost.ClaimedBy))
        {
            metadata[LostAttemptWorkerMetadataKey] = lost.ClaimedBy;
        }

        // Best effort, after the queue work: the store transition has already committed, and its
        // compare-and-set lets only one sweep record a given lost attempt.
        try
        {
            await logStore.AppendAsync(lost.OperationId, new ExecutionLogEntry
            {
                Timestamp = endedAt,
                Level = ExecutionLogLevel.Warning,
                Message = DescribeLostAttempt(lost, endedAt, maxAttempts),
                Phase = "worker-lost",
                Metadata = metadata
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.LostAttemptLogFailed(logger, lost.OperationId, ex);
        }
    }

    private static string FormatTime(DateTimeOffset? value)
        => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "unknown";
}

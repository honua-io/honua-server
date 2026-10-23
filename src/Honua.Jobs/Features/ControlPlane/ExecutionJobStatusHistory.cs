// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

// Only store writes derive evidence. Callers cannot provide, replace or backfill it.
internal static class ExecutionJobStatusHistory
{
    internal const int MaximumEntries = 256;

    internal static ExecutionJobRecord PrepareWrite(ExecutionJobRecord? previous, ExecutionJobRecord proposed,
        long version)
    {
        if (string.IsNullOrWhiteSpace(proposed.OperationId) || proposed.OperationId.Length > 2048
            || !Enum.IsDefined(proposed.Status) || proposed.AttemptCount < 0 || version is < 1 or > 9007199254740991L)
            throw new InvalidOperationException("The job status write is invalid.");
        if (previous is null)
            return proposed with { Version = version, StatusTransitions = [Entry(null)], StatusTransitionsTruncated = false };
        if (previous.OperationId != proposed.OperationId || version != previous.Version + 1
            || proposed.AttemptCount < previous.AttemptCount)
            throw new InvalidOperationException("The job status write does not match its authoritative predecessor.");
        var original = RequireValid(previous);
        // Never manufacture an initial event or backfill an older record from its
        // current status. Mixed-version writes which lose evidence also stay empty.
        if (original.Length == 0 || previous.Status == proposed.Status)
            return proposed with { Version = version, StatusTransitions = original, StatusTransitionsTruncated = previous.StatusTransitionsTruncated };
        var overflow = original.Length == MaximumEntries;
        return proposed with
        {
            Version = version,
            StatusTransitions = [.. original.Skip(overflow ? 1 : 0), Entry(previous.Status)],
            StatusTransitionsTruncated = previous.StatusTransitionsTruncated || overflow
        };

        ExecutionJobStatusTransition Entry(ExecutionJobStatus? from) => new()
        {
            JobId = proposed.OperationId,
            Version = version,
            AttemptCount = proposed.AttemptCount,
            // Replaced only by Redis TIME after atomic write guards succeed.
            OccurredAtUnixMilliseconds = -1,
            FromStatus = from,
            ToStatus = proposed.Status
        };
    }

    internal static ExecutionJobStatusTransition[] RequireValid(ExecutionJobRecord job)
    {
        if (job.StatusTransitions is null || job.StatusTransitions.Count > MaximumEntries
            || job.StatusTransitionsTruncated && job.StatusTransitions.Count != MaximumEntries)
            throw new InvalidOperationException("The retained job status history is invalid.");
        var history = job.StatusTransitions.ToArray();
        ExecutionJobStatusTransition? prior = null;
        foreach (var entry in history)
        {
            if (entry is null || entry.JobId != job.OperationId || entry.Version < 1 || entry.Version > job.Version
                || entry.AttemptCount < 0 || entry.AttemptCount > job.AttemptCount
                || entry.OccurredAtUnixMilliseconds is < 0 or > 253402300799999L
                || !Enum.IsDefined(entry.ToStatus) || entry.FromStatus is { } from && !Enum.IsDefined(from)
                || prior is null && (job.StatusTransitionsTruncated
                    ? entry.Version <= 1 || entry.FromStatus is null
                    : entry.Version != 1 || entry.FromStatus is not null)
                || prior is not null && (entry.Version <= prior.Version || entry.FromStatus != prior.ToStatus
                    || entry.ToStatus == prior.ToStatus || entry.AttemptCount < prior.AttemptCount || entry.OccurredAt < prior.OccurredAt))
                throw new InvalidOperationException("The retained job status history does not match its authoritative job.");
            prior = entry;
        }
        if (prior is not null && prior.ToStatus != job.Status)
            throw new InvalidOperationException("The retained job status history does not match its current status.");
        return history;
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Infrastructure;

internal static class JobCancellationNotifierExtensions
{
    public static bool CancelAny(
        this IEnumerable<IJobCancellationNotifier> cancellationNotifiers,
        string jobId)
    {
        ArgumentNullException.ThrowIfNull(cancellationNotifiers);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        var workerOwnsTerminalState = false;

        // Every notifier must be signaled (side effect), so the predicate itself is the
        // cancellation call; Where() here filters to the notifiers that owned the transition
        // while still visiting all of them during enumeration.
        foreach (var _ in cancellationNotifiers.Where(n => n.Cancel(jobId)))
        {
            workerOwnsTerminalState = true;
        }

        return workerOwnsTerminalState;
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Infrastructure;

/// <summary>
/// Shared fan-out helpers for job-cancellation notifiers.
/// </summary>
internal static class JobCancellationNotifierExtensions
{
    /// <summary>
    /// Signals every registered notifier for <paramref name="jobId"/> and
    /// returns <c>true</c> when any worker confirms it owns the terminal-state
    /// transition.
    /// </summary>
    public static bool CancelAny(
        this IEnumerable<IJobCancellationNotifier> cancellationNotifiers,
        string jobId)
    {
        ArgumentNullException.ThrowIfNull(cancellationNotifiers);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        var workerOwnsTerminalState = false;

        foreach (var notifier in cancellationNotifiers)
        {
            if (notifier.Cancel(jobId))
            {
                workerOwnsTerminalState = true;
            }
        }

        return workerOwnsTerminalState;
    }
}

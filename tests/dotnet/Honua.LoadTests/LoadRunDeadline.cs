// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Honua.LoadTests;

internal static class LoadRunDeadline
{
    /// <summary>
    /// Bounds the entire NBomber session, including statistics and report teardown.
    /// The caller returns nonzero on timeout; no partial result becomes receipt input.
    /// </summary>
    internal static bool TryComplete<T>(Func<T> action, TimeSpan budget, [NotNullWhen(true)] out T? result)
        where T : class
    {
        // Keep the watchdog off NBomber's worker pool. LongRunning creates a background
        // thread, so a stuck session cannot keep the CLI alive after Main returns 124.
        var started = Stopwatch.GetTimestamp();
        var run = Task.Factory.StartNew(action, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var remaining = budget;
        // Task.Wait(TimeSpan) accepts at most Int32.MaxValue milliseconds. Chunk
        // longer sessions while retaining one monotonic wall-clock deadline.
        while (!run.Wait(remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining))
        {
            remaining = budget - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                result = null;
                return false;
            }
        }

        result = run.GetAwaiter().GetResult();
        return result is not null;
    }
}

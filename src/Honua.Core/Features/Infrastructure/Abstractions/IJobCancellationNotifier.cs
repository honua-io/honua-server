// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Abstractions;

/// <summary>
/// Allows cross-slice notification of job cancellation so that in-flight work can be aborted.
/// Implementations track per-job cancellation tokens and cancel them on demand.
/// </summary>
public interface IJobCancellationNotifier
{
    /// <summary>
    /// Signals cancellation for the specified job if it is currently tracked.
    /// Returns <c>true</c> when the job was found and signalled, indicating a
    /// background worker owns the terminal state transition; <c>false</c> when
    /// the job was not tracked (already finished, not yet dequeued, or unknown).
    /// </summary>
    bool Cancel(string jobId);
}

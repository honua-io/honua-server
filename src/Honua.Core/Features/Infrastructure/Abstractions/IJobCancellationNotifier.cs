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
    /// </summary>
    void Cancel(string jobId);
}

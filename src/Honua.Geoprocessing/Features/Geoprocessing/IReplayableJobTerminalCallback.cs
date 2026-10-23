// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Geoprocessing;

/// <summary>
/// A terminal callback with a non-destructive read-side replay mode (#6107). The result
/// read path replays terminal callbacks to heal jobs whose first transition never ran the
/// callback (for example jobs finalized by another process); that replay must never
/// rewrite a terminal job, so persistence failures surface to the reader instead.
/// </summary>
internal interface IReplayableJobTerminalCallback : IJobTerminalCallback
{
    /// <summary>
    /// Replays the terminal work for <paramref name="job"/> without changing its durable
    /// status. Throws <see cref="GeoprocessingStoreUnavailableException"/> when the replay
    /// could not complete and the reader should retry.
    /// </summary>
    ValueTask ReplayAsync(ExecutionJobRecord job, CancellationToken cancellationToken);
}

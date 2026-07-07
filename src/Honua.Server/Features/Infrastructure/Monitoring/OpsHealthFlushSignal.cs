// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// In-process event seam raised by the ops-health rollup sampler after each flush, carrying the freshly
/// cluster-aggregated ops-health snapshot DTO (the same shape as <c>GET ops-health</c>). It is a plain
/// event with no realtime/hub reference — the realtime broadcast layer (#2554) subscribes to it and pushes
/// on the <c>ops-health</c> hub group. Keeping the seam here means this ticket carries no SignalR dependency.
/// </summary>
internal sealed class OpsHealthFlushSignal
{
    /// <summary>
    /// Raised on every sampler flush (and immediately whenever the flush is produced) with the latest
    /// cluster-aggregated snapshot. Subscribers must not throw; the sampler swallows subscriber exceptions.
    /// </summary>
    public event EventHandler<OpsHealthFlushedEventArgs>? Flushed;

    /// <summary>
    /// Raises <see cref="Flushed"/> with the supplied snapshot. Subscriber exceptions are the caller's
    /// responsibility to isolate (the sampler runs this inside its fail-open guard).
    /// </summary>
    /// <param name="snapshot">The freshly cluster-aggregated ops-health snapshot.</param>
    internal void Raise(OpsHealthSnapshotResponse snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Flushed?.Invoke(this, new OpsHealthFlushedEventArgs(snapshot));
    }
}

/// <summary>Event payload for <see cref="OpsHealthFlushSignal.Flushed"/>.</summary>
internal sealed class OpsHealthFlushedEventArgs : EventArgs
{
    /// <summary>Initializes a new instance of the <see cref="OpsHealthFlushedEventArgs"/> class.</summary>
    /// <param name="snapshot">The cluster-aggregated ops-health snapshot for this flush.</param>
    public OpsHealthFlushedEventArgs(OpsHealthSnapshotResponse snapshot)
    {
        Snapshot = snapshot;
    }

    /// <summary>Gets the cluster-aggregated ops-health snapshot for this flush.</summary>
    public OpsHealthSnapshotResponse Snapshot { get; }
}

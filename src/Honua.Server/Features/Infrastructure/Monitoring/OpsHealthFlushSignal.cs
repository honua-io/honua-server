// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Monitoring;

internal sealed class OpsHealthFlushSignal
{
    // Subscribers must not throw, and the sampler swallows subscriber exceptions.
    public event EventHandler<OpsHealthFlushedEventArgs>? Flushed;

    internal void Raise(OpsHealthSnapshotResponse snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Flushed?.Invoke(this, new OpsHealthFlushedEventArgs(snapshot));
    }
}

internal sealed class OpsHealthFlushedEventArgs : EventArgs
{
    public OpsHealthFlushedEventArgs(OpsHealthSnapshotResponse snapshot)
    {
        Snapshot = snapshot;
    }

    public OpsHealthSnapshotResponse Snapshot { get; }
}

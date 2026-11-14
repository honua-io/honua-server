// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.Metrics;

namespace Honua.Server.Tests.Features.Admin;

internal sealed class TileQueueDepthObserver : IDisposable
{
    private readonly MeterListener _listener = new();
    private long _value;

    public TileQueueDepthObserver(string operation)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name == "honua.tile.jobs.queue_depth")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "operation" && Equals(tag.Value, operation))
                {
                    Interlocked.Add(ref _value, value);
                }
            }
        });
        _listener.Start();
    }

    public long Value => Interlocked.Read(ref _value);

    public void Dispose() => _listener.Dispose();
}

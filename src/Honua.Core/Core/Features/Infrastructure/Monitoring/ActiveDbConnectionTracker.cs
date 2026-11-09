// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Monitoring;

public interface IActiveDbConnectionTracker
{
    void Increment();

    void Decrement();

    int GetActiveCount();
}

internal sealed class ActiveDbConnectionTracker : IActiveDbConnectionTracker
{
    private int _activeConnections;

    public void Increment()
    {
        Interlocked.Increment(ref _activeConnections);
    }

    public void Decrement()
    {
        var updated = Interlocked.Decrement(ref _activeConnections);
        if (updated < 0)
        {
            Interlocked.Exchange(ref _activeConnections, 0);
        }
    }

    public int GetActiveCount()
    {
        return Math.Max(0, Volatile.Read(ref _activeConnections));
    }
}

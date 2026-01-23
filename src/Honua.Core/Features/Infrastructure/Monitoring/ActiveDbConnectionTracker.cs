// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Monitoring;

/// <summary>
/// Tracks active database connections for lightweight telemetry.
/// </summary>
public interface IActiveDbConnectionTracker
{
    /// <summary>
    /// Increments the active connection count.
    /// </summary>
    void Increment();

    /// <summary>
    /// Decrements the active connection count.
    /// </summary>
    void Decrement();

    /// <summary>
    /// Gets the current active connection count.
    /// </summary>
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

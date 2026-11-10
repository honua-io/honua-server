// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.ControlPlane;

// The clock the job store stamps and compares claims, heartbeats, retry times and leases with.
// Code that decides whether a stored timestamp has expired compares against this clock, never
// against the node's own.
internal interface IJobStoreClock
{
    Task<DateTimeOffset> GetUtcNowAsync(CancellationToken cancellationToken = default);
}

// The clock of a store that stamps with the node's time, such as an in-memory store.
internal sealed class NodeJobStoreClock : IJobStoreClock
{
    public static readonly NodeJobStoreClock Instance = new();

    public Task<DateTimeOffset> GetUtcNowAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(DateTimeOffset.UtcNow);
}

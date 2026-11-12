// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Realtime;

namespace Honua.Server.Features.Realtime;

// A family whose events are kept outside realtime_events, by the feature slice that owns them.
// Positions increase but may have gaps, so a missing next position never means lost events: the
// window alone says what is still resumable. Scope keys are the source's own (layer ids for features).
internal interface IRealtimeFamilySource
{
    RealtimeFamily Family { get; }

    Task<long> ReadHeadAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<RealtimeEvent>> ReadAfterAsync(
        long after,
        IReadOnlyCollection<string>? scopeKeys,
        int limit,
        CancellationToken cancellationToken);

    Task<RealtimeLogWindow> ReadWindowAsync(CancellationToken cancellationToken);
}

// Wakes this task's dispatcher after a local commit that wrote events, so its own subscribers do
// not wait for a notification or a poll.
internal interface IRealtimeWakeSignal
{
    void Wake();
}

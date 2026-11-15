// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Realtime;

public interface IRealtimeStreamLifecycle
{
    bool IsDraining { get; }

    TimeSpan HeartbeatInterval { get; }

    // Null means admission is closed; respond with 503 and Retry-After: 2.
    IRealtimeStreamSession? TryOpen(RealtimeStreamRegistration registration);

    void BeginDrain();

    Task DrainAsync(CancellationToken cancellationToken);
}

public sealed record RealtimeStreamRegistration(RealtimeFamily Family, string Transport, Action Abort);

public interface IRealtimeStreamSession : IDisposable
{
    // Send the terminal retry frame or WebSocket service-restart close, then release the session.
    CancellationToken DrainingToken { get; }

    int RetryMilliseconds { get; }

    // Call after the frame has been flushed, using the committed event timestamp.
    void RecordDelivery(DateTimeOffset committedAt);

    void RecordReset(string reason);

    void RecordAuthorizationFilter();

    void RecordRevocation();
}

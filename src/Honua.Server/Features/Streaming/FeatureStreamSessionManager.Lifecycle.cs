// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Realtime;

namespace Honua.Server.Features.Streaming;

// Per-session shared realtime lifecycle meters, reached by session id from the transports.
internal sealed partial class FeatureStreamSessionManager
{
    internal void AttachLifecycle(Guid sessionId, IRealtimeStreamSession lifecycle)
    {
        if (_sessions.TryGetValue(sessionId, out var entry))
        {
            entry.Lifecycle = lifecycle;
        }
    }

    internal void RecordDelivery(Guid sessionId, DateTimeOffset committedAt)
    {
        if (_sessions.TryGetValue(sessionId, out var entry))
        {
            entry.Lifecycle?.RecordDelivery(committedAt);
        }
    }

    internal void RecordReset(Guid sessionId, string reason)
    {
        if (_sessions.TryGetValue(sessionId, out var entry))
        {
            entry.Lifecycle?.RecordReset(reason);
        }
    }
}

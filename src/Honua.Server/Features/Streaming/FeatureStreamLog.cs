// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Streaming;

/// <summary>
/// Source-generated logger for feature streaming (AOT compatible).
/// </summary>
internal static partial class FeatureStreamLog
{
    [LoggerMessage(EventId = 5001, Level = LogLevel.Information,
        Message = "Feature stream session {SessionId} created (transport={Transport})")]
    public static partial void SessionCreated(ILogger logger, Guid sessionId, string transport);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Information,
        Message = "Feature stream session {SessionId} removed (reason={Reason})")]
    public static partial void SessionRemoved(ILogger logger, Guid sessionId, FeatureStreamDisconnectReason reason);

    [LoggerMessage(EventId = 5003, Level = LogLevel.Warning,
        Message = "Feature stream session {SessionId} dropped: slow consumer")]
    public static partial void SlowConsumerDropped(ILogger logger, Guid sessionId);

    [LoggerMessage(EventId = 5004, Level = LogLevel.Debug,
        Message = "Heartbeat sent to {Count} feature stream sessions")]
    public static partial void HeartbeatBroadcast(ILogger logger, int count);

    [LoggerMessage(EventId = 5005, Level = LogLevel.Information,
        Message = "Replaying {Count} events from cursor {Cursor} for session {SessionId}")]
    public static partial void ReplayStarted(ILogger logger, int count, long cursor, Guid sessionId);

    [LoggerMessage(EventId = 5006, Level = LogLevel.Debug,
        Message = "Feature stream event broadcast to {Count} sessions (cursor={Cursor})")]
    public static partial void EventBroadcast(ILogger logger, int count, long cursor);

    [LoggerMessage(EventId = 5007, Level = LogLevel.Warning,
        Message = "Feature stream broadcast failed for session {SessionId}")]
    public static partial void BroadcastFailed(ILogger logger, Exception exception, Guid sessionId);

    [LoggerMessage(EventId = 5008, Level = LogLevel.Information,
        Message = "Feature stream heartbeat service started (interval={Interval})")]
    public static partial void HeartbeatServiceStarted(ILogger logger, TimeSpan interval);

    [LoggerMessage(EventId = 5009, Level = LogLevel.Information,
        Message = "Feature stream heartbeat service stopped")]
    public static partial void HeartbeatServiceStopped(ILogger logger);

    [LoggerMessage(EventId = 5010, Level = LogLevel.Information,
        Message = "Feature stream session {SessionId} created with filter: {FilterSummary}")]
    public static partial void SessionCreatedWithFilter(ILogger logger, Guid sessionId, string filterSummary);

    [LoggerMessage(EventId = 5011, Level = LogLevel.Warning,
        Message = "Feature stream filter validation failed: {Error}")]
    public static partial void FilterValidationFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 5012, Level = LogLevel.Warning,
        Message = "Feature stream cross-node broadcast is unavailable; live delivery is local-only.")]
    public static partial void ClusterBroadcastUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 5013, Level = LogLevel.Warning,
        Message = "Feature stream cross-node broadcast failed; live delivery is continuing locally.")]
    public static partial void ClusterBroadcastFailed(ILogger logger, Exception exception);
}

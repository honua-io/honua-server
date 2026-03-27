// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Streaming;

/// <summary>
/// Envelope pushed to streaming clients. Contains the same data as <see cref="Infrastructure.Events.FeatureChangeEvent"/>
/// but serialized once and shared across transports.
/// </summary>
internal sealed record FeatureStreamEnvelope
{
    /// <summary>
    /// Unique event identifier.
    /// </summary>
    public required string EventId { get; init; }

    /// <summary>
    /// Monotonic cursor for ordering and replay.
    /// </summary>
    public required long Cursor { get; init; }

    /// <summary>
    /// When the event was recorded.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Originating service identifier.
    /// </summary>
    public required string ServiceId { get; init; }

    /// <summary>
    /// Layer the feature belongs to.
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Feature object identifier.
    /// </summary>
    public required long ObjectId { get; init; }

    /// <summary>
    /// Mutation type: create, update, or delete.
    /// </summary>
    public required string Operation { get; init; }

    /// <summary>
    /// Protocol that originated the change.
    /// </summary>
    public required string Protocol { get; init; }

    /// <summary>
    /// Correlation request identifier.
    /// </summary>
    public required string RequestId { get; init; }
}

/// <summary>
/// Metadata about an active feature-stream session for admin visibility.
/// </summary>
internal sealed record FeatureStreamSessionInfo(
    Guid SessionId,
    DateTimeOffset ConnectedAt,
    string? ClientLabel,
    string Transport,
    long LastQueuedCursor);

/// <summary>
/// Disconnect reason for feature-stream sessions.
/// </summary>
internal enum FeatureStreamDisconnectReason
{
    /// <summary>Client closed the connection gracefully.</summary>
    ClientClosed,

    /// <summary>Server shut down or request was aborted.</summary>
    ServerShutdown,

    /// <summary>Connection was force-disconnected by admin.</summary>
    AdminDisconnect,

    /// <summary>Client failed to keep up with the event stream.</summary>
    SlowConsumer
}

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
    /// Frame type identifier.
    /// </summary>
    public string Type { get; init; } = "feature-change";

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
    public string? SourceId { get; init; }

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
    public string FeatureId { get; init; } = string.Empty;

    /// <summary>
    /// Feature object identifier.
    /// </summary>
    public required long ObjectId { get; init; }

    /// <summary>
    /// Mutation type: insert, update, or delete.
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

    /// <summary>
    /// Server-assigned subscription identifier that matched this event.
    /// </summary>
    public string? SubscriptionId { get; init; }

    /// <summary>
    /// GeoJSON geometry for insert/update and delete tombstones when available.
    /// </summary>
    public System.Text.Json.JsonElement? Geometry { get; init; }

    /// <summary>
    /// CRS identifier for <see cref="Geometry"/>.
    /// </summary>
    public string? GeometryCrs { get; init; }

    /// <summary>
    /// Full attribute snapshot after insert/update, or before-image for deletes when available.
    /// </summary>
    public Dictionary<string, System.Text.Json.JsonElement>? Attributes { get; init; }

    /// <summary>
    /// Optional. Changed attribute values when available. Null when the originating
    /// protocol does not provide attribute-level change tracking, or for deletes.
    /// </summary>
    public Dictionary<string, object?>? ChangedAttributes { get; init; }

    /// <summary>
    /// Whether the feature's geometry was modified by this operation.
    /// Best-effort: may default to false when the originating protocol cannot determine this.
    /// </summary>
    public bool GeometryChanged { get; init; }
}

/// <summary>
/// Client WebSocket control frame for subscription lifecycle operations.
/// </summary>
internal sealed record FeatureStreamControlMessage
{
    /// <summary>Control frame type: subscribe, unsubscribe, or ping.</summary>
    public string? Type { get; init; }

    /// <summary>Subscription identifier for unsubscribe.</summary>
    public string? SubscriptionId { get; init; }

    /// <summary>Optional client label for diagnostics.</summary>
    public string? ClientLabel { get; init; }

    /// <summary>Service scope for subscribe.</summary>
    public string? ServiceId { get; init; }

    /// <summary>Single layer scope for subscribe.</summary>
    public int? LayerId { get; init; }

    /// <summary>Layer scopes for subscribe.</summary>
    public int[]? Layers { get; init; }

    /// <summary>Legacy layer scopes for subscribe.</summary>
    public int[]? LayerIds { get; init; }

    /// <summary>Optional bbox filter [minX,minY,maxX,maxY].</summary>
    public double[]? Bbox { get; init; }

    /// <summary>Optional bbox CRS. Defaults to EPSG:4326.</summary>
    public string? BboxCrs { get; init; }

    /// <summary>Optional CQL2 attribute filter.</summary>
    public string? Filter { get; init; }

    /// <summary>Optional filter language. Defaults to cql2-text.</summary>
    public string? FilterLang { get; init; }

    /// <summary>Optional OGC datetime/interval temporal filter.</summary>
    public string? Datetime { get; init; }

    /// <summary>Optional replay cursor for this subscription.</summary>
    public long? Cursor { get; init; }
}

/// <summary>
/// Server status frame sent over WebSocket and SSE.
/// </summary>
internal sealed record FeatureStreamStatusFrame
{
    /// <summary>Frame type identifier.</summary>
    public string Type { get; init; } = "status";

    /// <summary>Machine-readable status value.</summary>
    public required string Status { get; init; }

    /// <summary>Human-readable client-safe message.</summary>
    public required string Message { get; init; }

    /// <summary>Server timestamp.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Session identifier when available.</summary>
    public Guid? SessionId { get; init; }

    /// <summary>Subscription identifier when available.</summary>
    public string? SubscriptionId { get; init; }

    /// <summary>Current or replay cursor when available.</summary>
    public long? Cursor { get; init; }
}

/// <summary>
/// Client-safe WebSocket control error frame.
/// </summary>
internal sealed record FeatureStreamErrorFrame
{
    /// <summary>Frame type identifier.</summary>
    public string Type { get; init; } = "error";

    /// <summary>Stable error code.</summary>
    public required string Code { get; init; }

    /// <summary>Client-safe message.</summary>
    public required string Message { get; init; }

    /// <summary>Subscription identifier when the error is scoped to one subscription.</summary>
    public string? SubscriptionId { get; init; }
}

/// <summary>
/// Discoverable streaming capability metadata.
/// </summary>
internal sealed record FeatureStreamCapabilitiesResponse
{
    /// <summary>Whether streaming is enabled for the active edition.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Current platform edition.</summary>
    public required string Edition { get; init; }

    /// <summary>Minimum edition required for streaming.</summary>
    public required string MinimumEdition { get; init; }

    /// <summary>Available transports.</summary>
    public required string[] Transports { get; init; }

    /// <summary>Supported subscription filter families.</summary>
    public required string[] FilterFamilies { get; init; }

    /// <summary>Whether cursor replay is supported.</summary>
    public required bool ReplaySupported { get; init; }

    /// <summary>Cursor retention limit for the active store.</summary>
    public required int CursorRetentionLimit { get; init; }

    /// <summary>Heartbeat interval in seconds.</summary>
    public required double HeartbeatIntervalSeconds { get; init; }

    /// <summary>Maximum concurrent stream sessions.</summary>
    public required int MaxConcurrentSessions { get; init; }

    /// <summary>Whether delete before-images can be emitted when providers supply them.</summary>
    public required bool DeleteBeforeImages { get; init; }

    /// <summary>Per-layer capability summaries.</summary>
    public required FeatureStreamLayerCapability[] Layers { get; init; }
}

/// <summary>
/// Per-layer streaming capability metadata.
/// </summary>
internal sealed record FeatureStreamLayerCapability
{
    /// <summary>Layer identifier.</summary>
    public required int LayerId { get; init; }

    /// <summary>Layer name.</summary>
    public required string Name { get; init; }

    /// <summary>Whether the active caller can subscribe to this layer.</summary>
    public required bool CanSubscribe { get; init; }

    /// <summary>Whether spatial filters are supported.</summary>
    public required bool SupportsSpatialFilters { get; init; }

    /// <summary>Whether temporal filters are supported.</summary>
    public required bool SupportsTemporalFilters { get; init; }

    /// <summary>Layer time fields when time-aware.</summary>
    public string[]? TemporalFields { get; init; }

    /// <summary>Layer CRS identifier.</summary>
    public required string Crs { get; init; }
}

internal sealed record StreamTemporalFilter(
    string StartField,
    string? EndField,
    DateTimeOffset? Start,
    DateTimeOffset? End);

/// <summary>
/// Metadata about an active feature-stream session for admin visibility.
/// </summary>
internal sealed record FeatureStreamSessionInfo(
    Guid SessionId,
    DateTimeOffset ConnectedAt,
    string? ClientLabel,
    string Transport,
    long LastQueuedCursor,
    bool HasFilter,
    string? FilterSummary = null,
    string? ServiceIdFilter = null,
    int[]? LayerIdFilter = null);

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

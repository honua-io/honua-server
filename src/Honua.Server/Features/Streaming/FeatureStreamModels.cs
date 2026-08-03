// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Streaming;

/// <summary>
/// Envelope pushed to streaming clients. Contains the same data as <see cref="Honua.Infrastructure.Events.FeatureChangeEvent"/>
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
    /// Monotonic cursor for ordering and replay. This is the durable global event-store
    /// position and is deliberately NOT a per-subscription sequence: a filtered
    /// subscription legitimately skips cursor values that belong to events it does not
    /// admit. Use <see cref="Sequence"/> for continuity checks.
    /// </summary>
    public required long Cursor { get; init; }

    /// <summary>
    /// Subscription-local monotonic sequence. Starts at 0 for the first frame admitted by
    /// the subscription (the snapshot baseline when snapshot-then-delta mode is active) and
    /// advances by exactly one for every subsequent admitted snapshot or delta frame, so it
    /// is contiguous even where the global <see cref="Cursor"/> skips. Null on legacy
    /// delta-only paths that predate sequence assignment.
    /// </summary>
    public long? Sequence { get; init; }

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

    /// <summary>
    /// Optional subscription mode: <c>delta</c> (default, change-only) or <c>snapshot</c>
    /// (snapshot-then-delta — a complete baseline is emitted before live deltas).
    /// </summary>
    public string? Mode { get; init; }
}

/// <summary>
/// Delivery mode requested for a subscription.
/// </summary>
internal enum FeatureStreamSubscriptionMode
{
    /// <summary>Change-only delivery. No baseline is emitted.</summary>
    Delta,

    /// <summary>
    /// Snapshot-then-delta delivery: a complete baseline snapshot is emitted before any
    /// live mutation, and a replacement snapshot is emitted instead of deltas when a
    /// supplied replay cursor has fallen outside the retained window.
    /// </summary>
    Snapshot
}

/// <summary>
/// Reason a baseline snapshot was emitted.
/// </summary>
internal static class FeatureStreamSnapshotReasons
{
    /// <summary>First baseline for a newly opened snapshot-then-delta subscription.</summary>
    public const string Initial = "initial";

    /// <summary>
    /// Replacement baseline: the supplied replay cursor is older than the oldest retained
    /// event, so the deltas needed to reach the current state no longer exist.
    /// </summary>
    public const string CursorExpired = "cursor-expired";

    /// <summary>
    /// Replacement baseline: the supplied replay cursor is ahead of the durable store's
    /// current position (forked or fabricated cursor) and cannot be resumed from.
    /// </summary>
    public const string CursorInvalid = "cursor-invalid";
}

/// <summary>
/// Opening frame of a baseline snapshot.
/// </summary>
internal sealed record FeatureStreamSnapshotBeginFrame
{
    /// <summary>Frame type identifier.</summary>
    public string Type { get; init; } = "snapshot-begin";

    /// <summary>Identifier correlating every frame of this snapshot.</summary>
    public required string SnapshotId { get; init; }

    /// <summary>Subscription this snapshot belongs to.</summary>
    public required string SubscriptionId { get; init; }

    /// <summary>Subscription-local monotonic sequence.</summary>
    public required long Sequence { get; init; }

    /// <summary>
    /// Global event-store position captured before the baseline read began. Every delta
    /// with a cursor greater than this value is delivered after <c>snapshot-end</c>.
    /// </summary>
    public required long Cursor { get; init; }

    /// <summary>Why the snapshot was emitted. See <see cref="FeatureStreamSnapshotReasons"/>.</summary>
    public required string Reason { get; init; }

    /// <summary>Service scope of the snapshot, when the subscription is service-scoped.</summary>
    public string? ServiceId { get; init; }

    /// <summary>Layer scope of the snapshot.</summary>
    public required int[] LayerIds { get; init; }

    /// <summary>Server timestamp.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One feature of a baseline snapshot.
/// </summary>
internal sealed record FeatureStreamSnapshotFeatureFrame
{
    /// <summary>Frame type identifier.</summary>
    public string Type { get; init; } = "snapshot-feature";

    /// <summary>Identifier correlating every frame of this snapshot.</summary>
    public required string SnapshotId { get; init; }

    /// <summary>Subscription this frame belongs to.</summary>
    public required string SubscriptionId { get; init; }

    /// <summary>Subscription-local monotonic sequence.</summary>
    public required long Sequence { get; init; }

    /// <summary>Baseline cursor boundary (identical for every frame of the snapshot).</summary>
    public required long Cursor { get; init; }

    /// <summary>Service the feature belongs to.</summary>
    public required string ServiceId { get; init; }

    /// <summary>Layer the feature belongs to.</summary>
    public required int LayerId { get; init; }

    /// <summary>Feature object identifier in string form.</summary>
    public required string FeatureId { get; init; }

    /// <summary>Feature object identifier.</summary>
    public required long ObjectId { get; init; }

    /// <summary>GeoJSON geometry, when the feature has one with resolvable CRS.</summary>
    public System.Text.Json.JsonElement? Geometry { get; init; }

    /// <summary>CRS identifier for <see cref="Geometry"/>.</summary>
    public string? GeometryCrs { get; init; }

    /// <summary>Full attribute snapshot.</summary>
    public Dictionary<string, System.Text.Json.JsonElement>? Attributes { get; init; }
}

/// <summary>
/// Closing frame of a baseline snapshot.
/// </summary>
internal sealed record FeatureStreamSnapshotEndFrame
{
    /// <summary>Frame type identifier.</summary>
    public string Type { get; init; } = "snapshot-end";

    /// <summary>Identifier correlating every frame of this snapshot.</summary>
    public required string SnapshotId { get; init; }

    /// <summary>Subscription this snapshot belongs to.</summary>
    public required string SubscriptionId { get; init; }

    /// <summary>Subscription-local monotonic sequence.</summary>
    public required long Sequence { get; init; }

    /// <summary>Baseline cursor boundary. Deltas resume strictly after this position.</summary>
    public required long Cursor { get; init; }

    /// <summary>Number of <c>snapshot-feature</c> frames emitted.</summary>
    public required long FeatureCount { get; init; }

    /// <summary>
    /// Whether the baseline is complete. False when the configured snapshot feature or
    /// scan bound was reached first — the client must not treat a truncated baseline as
    /// authoritative state.
    /// </summary>
    public required bool Complete { get; init; }
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

    /// <summary>
    /// Supported subscription modes. <c>delta</c> is always available; <c>snapshot</c>
    /// indicates snapshot-then-delta subscriptions are supported.
    /// </summary>
    public required string[] Modes { get; init; }

    /// <summary>
    /// Whether every admitted snapshot/delta frame carries a subscription-local monotonic
    /// sequence that is contiguous independently of the global replay cursor.
    /// </summary>
    public required bool SubscriptionSequence { get; init; }

    /// <summary>Maximum number of features emitted in one baseline snapshot.</summary>
    public required int MaxSnapshotFeatures { get; init; }

    /// <summary>Maximum number of stored rows scanned while building one baseline snapshot.</summary>
    public required int MaxSnapshotScanRows { get; init; }

    /// <summary>
    /// Mutable server release version. Changes on releases and is NOT a deployment identity.
    /// </summary>
    public required string ServerVersion { get; init; }

    /// <summary>
    /// Immutable deployment revision: a 40-character commit SHA or a
    /// <c>sha256:&lt;64 hex&gt;</c> image digest. Null when the deployment carries no
    /// verifiable revision.
    /// </summary>
    public string? DeploymentRevision { get; init; }

    /// <summary>How <see cref="DeploymentRevision"/> was resolved.</summary>
    public string? DeploymentRevisionSource { get; init; }

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
/// Point-in-time view of the cross-node broadcast backlog, consumed by
/// <c>FeatureStreamHealthCheck</c>. <paramref name="Configured"/> is true when Redis is
/// wired (multi-node topology); <paramref name="Enabled"/> is true when the broadcast
/// subscription is currently active. <paramref name="BacklogDepth"/> is the number of
/// payloads buffered awaiting a publish retry; <paramref name="Dropped"/> is the
/// cumulative count of payloads shed on backlog overflow since startup.
/// </summary>
internal readonly record struct ClusterBroadcastBacklogSnapshot(
    bool Configured,
    bool Enabled,
    int BacklogDepth,
    long Dropped);

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
/// Result of <see cref="FeatureStreamSessionManager.TryAddSubscription"/>.
/// </summary>
internal enum AddSubscriptionResult
{
    /// <summary>Subscription was registered on the session.</summary>
    Added,

    /// <summary>Session no longer exists (e.g., disconnected mid-handshake).</summary>
    SessionGone,

    /// <summary>The per-session subscription cap is full.</summary>
    LimitReached
}

/// <summary>
/// Outcome of <see cref="FeatureStreamSessionManager.TryAddSubscription"/>: status plus the
/// generation assigned to the subscription. Generation is monotonically increasing per session
/// and changes on every add (including same-id replacement), so callers can pin the generation
/// they observed when issuing replay or send-time delivery claims.
/// </summary>
internal readonly record struct AddSubscriptionOutcome(AddSubscriptionResult Result, long Generation);

/// <summary>
/// Result of <see cref="FeatureStreamSessionManager.TryClaimSubscriptionDelivery"/>. The
/// writer distinguishes a stale-generation drop (which is observable telemetry — frames are
/// being orphaned by unsubscribe/replacement) from a benign dedup skip (the per-subscription
/// replay path already claimed and sent the frame for the same subscription generation).
/// </summary>
internal enum SubscriptionDeliveryClaim
{
    /// <summary>Caller owns the slot; send the frame.</summary>
    Claimed,

    /// <summary>Slot was already claimed by a concurrent send-time path; skip silently.</summary>
    AlreadyDelivered,

    /// <summary>Subscription was removed or replaced; drop the queued frame and emit telemetry.</summary>
    StaleGeneration
}

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

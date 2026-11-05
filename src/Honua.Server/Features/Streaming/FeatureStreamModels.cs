// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.Streaming;

internal sealed record FeatureStreamEnvelope
{
    public string Type { get; init; } = "feature-change";

    public required string EventId { get; init; }

    // The cursor is the store position, not a per-subscription sequence, because a filtered subscription skips values.
    public required long Cursor { get; init; }

    // Null when the path has no subscription sequence; absence is not sequence zero.
    public long? Sequence { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public string? SourceId { get; init; }

    public required string ServiceId { get; init; }

    public required int LayerId { get; init; }

    public string FeatureId { get; init; } = string.Empty;

    public required long ObjectId { get; init; }

    public required string Operation { get; init; }

    public required string Protocol { get; init; }

    public required string RequestId { get; init; }

    public string? OperationInstanceId { get; init; }
    public string? CorrelationId { get; init; }
    public string? AuditId { get; init; }
    public string? ProposalId { get; init; }

    public string? SubscriptionId { get; init; }

    public System.Text.Json.JsonElement? Geometry { get; init; }

    public string? GeometryCrs { get; init; }

    public Dictionary<string, System.Text.Json.JsonElement>? Attributes { get; init; }

    // Null when the originating protocol has no attribute-change tracking, or on a delete.
    public Dictionary<string, object?>? ChangedAttributes { get; init; }

    // False when the originating protocol cannot tell, so it is not proof that geometry is unchanged.
    public bool GeometryChanged { get; init; }
}

internal sealed record FeatureStreamControlMessage
{
    public string? Type { get; init; }

    public string? SubscriptionId { get; init; }

    public string? ClientLabel { get; init; }

    public string? ServiceId { get; init; }

    public int? LayerId { get; init; }

    public int[]? Layers { get; init; }

    public int[]? LayerIds { get; init; }

    public double[]? Bbox { get; init; }

    public string? BboxCrs { get; init; }

    public string? Filter { get; init; }

    public string? FilterLang { get; init; }

    public string? Datetime { get; init; }

    public long? Cursor { get; init; }

    public string? Mode { get; init; }
}

internal enum FeatureStreamSubscriptionMode
{
    Delta,

    Snapshot,

    SnapshotThenDelta
}

internal static class FeatureStreamSnapshotReasons
{
    public const string Initial = "initial";

    public const string CursorExpired = "cursor-expired";

    public const string CursorInvalid = "cursor-invalid";
}

internal sealed record FeatureStreamSnapshotBeginFrame
{
    public string Type { get; init; } = "snapshot-begin";

    public required string SnapshotId { get; init; }

    public required string SubscriptionId { get; init; }

    public required long Sequence { get; init; }

    public required long Cursor { get; init; }

    public required string Reason { get; init; }

    public string? ServiceId { get; init; }

    public required int[] LayerIds { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

internal sealed record FeatureStreamSnapshotFeatureFrame
{
    public string Type { get; init; } = "snapshot-feature";

    public required string SnapshotId { get; init; }

    public required string SubscriptionId { get; init; }

    public required long Sequence { get; init; }

    public required long Cursor { get; init; }

    public required string ServiceId { get; init; }

    public required int LayerId { get; init; }

    public required string FeatureId { get; init; }

    public required long ObjectId { get; init; }

    public System.Text.Json.JsonElement? Geometry { get; init; }

    public string? GeometryCrs { get; init; }

    public Dictionary<string, System.Text.Json.JsonElement>? Attributes { get; init; }
}

internal sealed record FeatureStreamSnapshotEndFrame
{
    public string Type { get; init; } = "snapshot-end";

    public required string SnapshotId { get; init; }

    public required string SubscriptionId { get; init; }

    public required long Sequence { get; init; }

    public required long Cursor { get; init; }

    public required long FeatureCount { get; init; }

    // The client must not treat a truncated baseline as authoritative state.
    public required bool Complete { get; init; }
}

internal sealed record FeatureStreamSnapshotFrame
{
    public string Type { get; init; } = "snapshot";

    public required string SnapshotId { get; init; }

    public required string SubscriptionId { get; init; }

    public required long Sequence { get; init; }

    public required long Cursor { get; init; }

    public required string Reason { get; init; }

    // A baseline replaces the client's set, and the flag is explicit so a client does not infer a merge.
    public bool Replace { get; init; } = true;

    public string? ServiceId { get; init; }

    public required int[] LayerIds { get; init; }

    public required long FeatureCount { get; init; }

    // The client must not treat a truncated baseline as authoritative state.
    public required bool Complete { get; init; }

    public required FeatureStreamSnapshotFeature[] Features { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

internal sealed record FeatureStreamSnapshotFeature
{
    public required string Id { get; init; }

    public required string SourceId { get; init; }

    public required int LayerId { get; init; }

    public string? GeometryCrs { get; init; }

    public required FeatureStreamSnapshotGeoJsonFeature Feature { get; init; }
}

internal sealed record FeatureStreamSnapshotGeoJsonFeature
{
    public string Type { get; init; } = "Feature";

    public required string Id { get; init; }

    // A null geometry is written, because omitting the member cannot be told from withheld geometry.
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public JsonElement? Geometry { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public Dictionary<string, JsonElement>? Properties { get; init; }
}

internal sealed record FeatureStreamStatusFrame
{
    public string Type { get; init; } = "status";

    public required string Status { get; init; }

    public required string Message { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public Guid? SessionId { get; init; }

    public string? SubscriptionId { get; init; }

    public long? Cursor { get; init; }
}

internal sealed record FeatureStreamErrorFrame
{
    public string Type { get; init; } = "error";

    public required string Code { get; init; }

    public required string Message { get; init; }

    public string? SubscriptionId { get; init; }
}

internal sealed record FeatureStreamCapabilitiesResponse
{
    public required bool Enabled { get; init; }

    public required string Edition { get; init; }

    public required string MinimumEdition { get; init; }

    public required string[] Transports { get; init; }

    public required string[] FilterFamilies { get; init; }

    public required bool ReplaySupported { get; init; }

    public required int CursorRetentionLimit { get; init; }

    public required double HeartbeatIntervalSeconds { get; init; }

    public required int MaxConcurrentSessions { get; init; }

    public required bool DeleteBeforeImages { get; init; }

    public required string[] Modes { get; init; }

    public required bool SubscriptionSequence { get; init; }

    public required int MaxSnapshotFeatures { get; init; }

    public required int MaxSnapshotScanRows { get; init; }

    public required int MaxSnapshotBytes { get; init; }

    public required string ServerVersion { get; init; }

    public string? DeploymentRevision { get; init; }

    public string? DeploymentRevisionSource { get; init; }

    // ServerRevision duplicates DeploymentRevision because existing realtime evidence clients read that name.
    public string? ServerRevision { get; init; }

    // The contract is always present and carries no credential, so a caller can tell that it is not configured.
    public required FeatureStreamConformanceCapability Conformance { get; init; }

    public required FeatureStreamLayerCapability[] Layers { get; init; }
}

// The advertisement identifies no run, caller, or credential.
internal sealed record FeatureStreamConformanceCapability
{
    public required bool Enabled { get; init; }

    public string? ServiceId { get; init; }

    public int? LayerId { get; init; }

    public string? RunIdField { get; init; }

    public int? MaxConcurrentRuns { get; init; }

    public int? ActiveRuns { get; init; }

    public int? RunTtlSeconds { get; init; }

    public int? MaxMutationsPerRun { get; init; }

    public int? MaxRecordsPerRun { get; init; }

    public string[]? Operations { get; init; }
}

internal sealed record FeatureStreamLayerCapability
{
    public required int LayerId { get; init; }

    public required string Name { get; init; }

    public required bool CanSubscribe { get; init; }

    public required bool SupportsSpatialFilters { get; init; }

    public required bool SupportsTemporalFilters { get; init; }

    public string[]? TemporalFields { get; init; }

    public required string Crs { get; init; }
}

internal sealed record StreamTemporalFilter(
    string StartField,
    string? EndField,
    DateTimeOffset? Start,
    DateTimeOffset? End);

internal readonly record struct ClusterBroadcastBacklogSnapshot(
    bool Configured,
    bool Enabled,
    int BacklogDepth,
    long Dropped);

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

internal enum AddSubscriptionResult
{
    Added,

    SessionGone,

    LimitReached
}

internal readonly record struct AddSubscriptionOutcome(AddSubscriptionResult Result, long Generation);

internal enum SubscriptionDeliveryClaim
{
    Claimed,

    AlreadyDelivered,

    StaleGeneration
}

internal enum FeatureStreamDisconnectReason
{
    ClientClosed,

    ServerShutdown,

    AdminDisconnect,

    SlowConsumer,

    // A revoked layer closes the subscription, and the client must re-subscribe.
    PolicyRevoked
}

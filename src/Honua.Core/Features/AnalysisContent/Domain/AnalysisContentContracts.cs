// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.NlQuery.Domain;

namespace Honua.Core.Features.AnalysisContent.Domain;

/// <summary>
/// Kind of durable analysis content item.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AnalysisContentKind>))]
public enum AnalysisContentKind
{
    /// <summary>
    /// A reusable feature-query definition.
    /// </summary>
    [JsonStringEnumMemberName("savedQuery")]
    SavedQuery,

    /// <summary>
    /// A reusable analysis plan package.
    /// </summary>
    [JsonStringEnumMemberName("analysisPackage")]
    AnalysisPackage
}

/// <summary>
/// Lifecycle state for analysis content records.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AnalysisContentLifecycle>))]
public enum AnalysisContentLifecycle
{
    /// <summary>
    /// Content is available for normal use.
    /// </summary>
    [JsonStringEnumMemberName("active")]
    Active,

    /// <summary>
    /// Content is retained but hidden from primary lists.
    /// </summary>
    [JsonStringEnumMemberName("archived")]
    Archived,

    /// <summary>
    /// Content has been soft-deleted.
    /// </summary>
    [JsonStringEnumMemberName("deleted")]
    Deleted
}

/// <summary>
/// Retention state for result artifacts created by analysis jobs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ResultArtifactRetentionState>))]
public enum ResultArtifactRetentionState
{
    /// <summary>
    /// Artifact is a short-lived preview.
    /// </summary>
    [JsonStringEnumMemberName("preview")]
    Preview,

    /// <summary>
    /// Artifact is retained under the job result package.
    /// </summary>
    [JsonStringEnumMemberName("retained")]
    Retained,

    /// <summary>
    /// Artifact has been promoted for longer-lived downstream use.
    /// </summary>
    [JsonStringEnumMemberName("promoted")]
    Promoted,

    /// <summary>
    /// Artifact retention has expired.
    /// </summary>
    [JsonStringEnumMemberName("expired")]
    Expired
}

/// <summary>
/// Promotion state for a result artifact.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ResultArtifactPromotionState>))]
public enum ResultArtifactPromotionState
{
    /// <summary>
    /// Artifact has not been promoted.
    /// </summary>
    [JsonStringEnumMemberName("none")]
    None,

    /// <summary>
    /// Artifact has been promoted.
    /// </summary>
    [JsonStringEnumMemberName("promoted")]
    Promoted
}

/// <summary>
/// Safe failure classes exposed to clients for failed analysis jobs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AnalysisJobFailureClassification>))]
public enum AnalysisJobFailureClassification
{
    /// <summary>
    /// Job failed during request or plan validation.
    /// </summary>
    [JsonStringEnumMemberName("validationFailed")]
    ValidationFailed,

    /// <summary>
    /// Job failed because the caller was not authorized.
    /// </summary>
    [JsonStringEnumMemberName("authorizationDenied")]
    AuthorizationDenied,

    /// <summary>
    /// Job was cancelled.
    /// </summary>
    [JsonStringEnumMemberName("cancelled")]
    Cancelled,

    /// <summary>
    /// Job exceeded its allowed runtime.
    /// </summary>
    [JsonStringEnumMemberName("timedOut")]
    TimedOut,

    /// <summary>
    /// Job failed while materializing output artifacts.
    /// </summary>
    [JsonStringEnumMemberName("artifactOutputFailed")]
    ArtifactOutputFailed,

    /// <summary>
    /// Job failed during execution.
    /// </summary>
    [JsonStringEnumMemberName("executionFailed")]
    ExecutionFailed,

    /// <summary>
    /// The backing job or log store was unavailable.
    /// </summary>
    [JsonStringEnumMemberName("storeUnavailable")]
    StoreUnavailable,

    /// <summary>
    /// Failure type could not be classified more specifically.
    /// </summary>
    [JsonStringEnumMemberName("unknown")]
    Unknown
}

/// <summary>
/// Durable analysis content item root. Versions carry immutable payloads.
/// </summary>
public sealed record AnalysisContentItem
{
    /// <summary>
    /// Stable item identifier used by downstream bindings.
    /// </summary>
    public required string ItemId { get; init; }

    /// <summary>
    /// Content item kind.
    /// </summary>
    public required AnalysisContentKind Kind { get; init; }

    /// <summary>
    /// Stable machine-readable name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Optional display title.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Optional owner identifier.
    /// </summary>
    public string? OwnerId { get; init; }

    /// <summary>
    /// Visibility scope such as personal, organization, or public.
    /// </summary>
    public string Visibility { get; init; } = "organization";

    /// <summary>
    /// Current version number.
    /// </summary>
    public required int CurrentVersion { get; init; }

    /// <summary>
    /// Current version identifier.
    /// </summary>
    public required string CurrentVersionId { get; init; }

    /// <summary>
    /// Item lifecycle state.
    /// </summary>
    public AnalysisContentLifecycle Lifecycle { get; init; } = AnalysisContentLifecycle.Active;

    /// <summary>
    /// UTC timestamp when the item was created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// UTC timestamp when the item was last updated.
    /// </summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// Principal that created the item, when known.
    /// </summary>
    public string? CreatedBy { get; init; }
}

/// <summary>
/// Immutable analysis content version payload.
/// </summary>
public sealed record AnalysisContentVersion
{
    /// <summary>
    /// Stable version identifier.
    /// </summary>
    public required string VersionId { get; init; }

    /// <summary>
    /// Parent content item identifier.
    /// </summary>
    public required string ItemId { get; init; }

    /// <summary>
    /// Monotonic item-scoped version number.
    /// </summary>
    public required int Version { get; init; }

    /// <summary>
    /// Version content kind.
    /// </summary>
    public required AnalysisContentKind Kind { get; init; }

    /// <summary>
    /// Saved-query content for <see cref="AnalysisContentKind.SavedQuery"/>.
    /// </summary>
    public SavedQueryContent? SavedQuery { get; init; }

    /// <summary>
    /// Analysis package content for <see cref="AnalysisContentKind.AnalysisPackage"/>.
    /// </summary>
    public AnalysisPackageContent? AnalysisPackage { get; init; }

    /// <summary>
    /// Stable SHA-256 content hash of the version payload.
    /// </summary>
    public required string ContentHash { get; init; }

    /// <summary>
    /// Optional parent version identifier when this version derives from another.
    /// </summary>
    public string? BasedOnVersionId { get; init; }

    /// <summary>
    /// Optional source job identifier when this version was created from a rerun or result.
    /// </summary>
    public string? CreatedFromJobId { get; init; }

    /// <summary>
    /// Source artifact identifiers when this version was created from job results.
    /// </summary>
    public IReadOnlyList<string> CreatedFromArtifactIds { get; init; } = [];

    /// <summary>
    /// UTC timestamp when this immutable version was created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Principal that created the version, when known.
    /// </summary>
    public string? CreatedBy { get; init; }
}

/// <summary>
/// Durable saved-query payload compiled through the canonical query pipeline.
/// </summary>
public sealed record SavedQueryContent
{
    /// <summary>
    /// Original natural-language query text, when available.
    /// </summary>
    public string? NaturalLanguageQuery { get; init; }

    /// <summary>
    /// Target layer identifier for preview and downstream query execution.
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Optional service name associated with the layer.
    /// </summary>
    public string? ServiceName { get; init; }

    /// <summary>
    /// Structured filter plan produced by natural-language query grounding.
    /// </summary>
    public FilterPlan? FilterPlan { get; init; }

    /// <summary>
    /// Attribute field projection. Empty means all fields.
    /// </summary>
    public IReadOnlyList<string> OutFields { get; init; } = [];

    /// <summary>
    /// Desired output spatial reference identifier.
    /// </summary>
    public int? OutputSrid { get; init; }

    /// <summary>
    /// Default preview limit for this saved query.
    /// </summary>
    public int? PreviewLimit { get; init; }

    /// <summary>
    /// Preferred output format for downstream consumers.
    /// </summary>
    public string? OutputFormat { get; init; }

    /// <summary>
    /// Unit assumptions attached to the query content.
    /// </summary>
    public string? Units { get; init; }

    /// <summary>
    /// Additional AOT-friendly metadata.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Durable analysis package payload submitted through the canonical geoprocessing runtime.
/// </summary>
public sealed record AnalysisPackageContent
{
    /// <summary>
    /// User intent that produced the plan, when captured.
    /// </summary>
    public AnalysisIntent? Intent { get; init; }

    /// <summary>
    /// Executable analysis plan.
    /// </summary>
    public required AnalysisPlan Plan { get; init; }

    /// <summary>
    /// Package parameters that can be overridden by reruns.
    /// </summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Requested output artifact kinds.
    /// </summary>
    public IReadOnlyList<ArtifactKind> RequestedArtifacts { get; init; } = [];

    /// <summary>
    /// Optional downstream binding hints for artifacts produced by this package.
    /// </summary>
    public IReadOnlyList<ArtifactBindingRef> BindingHints { get; init; } = [];

    /// <summary>
    /// Spatial reference assumption for the package.
    /// </summary>
    public int? SpatialReferenceId { get; init; }

    /// <summary>
    /// Unit assumption for the package.
    /// </summary>
    public string? Units { get; init; }

    /// <summary>
    /// Additional AOT-friendly metadata.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Stable downstream binding reference for an analysis artifact.
/// </summary>
public sealed record ArtifactBindingRef
{
    /// <summary>
    /// Stable artifact identifier.
    /// </summary>
    public required string ArtifactId { get; init; }

    /// <summary>
    /// Source content item identifier, when available.
    /// </summary>
    public string? SourceItemId { get; init; }

    /// <summary>
    /// Source item version number, when available.
    /// </summary>
    public int? SourceVersion { get; init; }

    /// <summary>
    /// Source content version identifier, when available.
    /// </summary>
    public string? SourceVersionId { get; init; }

    /// <summary>
    /// Binding role such as dataSource or preview.
    /// </summary>
    public string Role { get; init; } = "dataSource";

    /// <summary>
    /// Target content kind such as map, dashboard, report, app, or workflow.
    /// </summary>
    public string TargetKind { get; init; } = "content";

    /// <summary>
    /// Target slot name within the downstream content package.
    /// </summary>
    public string TargetSlot { get; init; } = "default";
}

/// <summary>
/// Durable metadata for an artifact produced by a saved query preview or analysis job.
/// </summary>
public sealed record ResultArtifactRecord
{
    /// <summary>
    /// Stable artifact identifier.
    /// </summary>
    public required string ArtifactId { get; init; }

    /// <summary>
    /// Result package identifier that contains this artifact.
    /// </summary>
    public required string ResultPackageId { get; init; }

    /// <summary>
    /// Source execution job identifier.
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// Source content item identifier.
    /// </summary>
    public required string SourceItemId { get; init; }

    /// <summary>
    /// Source content item version number.
    /// </summary>
    public required int SourceVersion { get; init; }

    /// <summary>
    /// Source content version identifier.
    /// </summary>
    public required string SourceVersionId { get; init; }

    /// <summary>
    /// Artifact kind.
    /// </summary>
    public required ArtifactKind Kind { get; init; }

    /// <summary>
    /// Human-readable artifact label.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Optional artifact URI.
    /// </summary>
    public string? Uri { get; init; }

    /// <summary>
    /// Optional artifact media type.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// Optional artifact size in bytes.
    /// </summary>
    public long? ByteSize { get; init; }

    /// <summary>
    /// AOT-friendly artifact metadata.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// AOT-friendly artifact provenance metadata.
    /// </summary>
    public IReadOnlyDictionary<string, string> Provenance { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Retention state.
    /// </summary>
    public ResultArtifactRetentionState RetentionState { get; init; } = ResultArtifactRetentionState.Retained;

    /// <summary>
    /// Promotion state.
    /// </summary>
    public ResultArtifactPromotionState PromotionState { get; init; } = ResultArtifactPromotionState.None;

    /// <summary>
    /// UTC timestamp when the artifact record was created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Optional UTC expiry timestamp.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>
/// Bounded feature preview returned for saved-query previews.
/// </summary>
public sealed record SavedQueryPreviewResult
{
    /// <summary>
    /// Preview artifact identifier.
    /// </summary>
    public required string PreviewArtifactId { get; init; }

    /// <summary>
    /// Source content item identifier.
    /// </summary>
    public required string ItemId { get; init; }

    /// <summary>
    /// Source version number.
    /// </summary>
    public required int Version { get; init; }

    /// <summary>
    /// Target layer identifier.
    /// </summary>
    public required int LayerId { get; init; }

    /// <summary>
    /// Preview feature items.
    /// </summary>
    public IReadOnlyList<SavedQueryPreviewFeature> Features { get; init; } = [];

    /// <summary>
    /// Result count reported by the feature store, when available.
    /// </summary>
    public long? TotalCount { get; init; }

    /// <summary>
    /// True when the result set may contain more rows than the preview limit.
    /// </summary>
    public bool ExceededPreviewLimit { get; init; }

    /// <summary>
    /// Stable binding metadata for downstream content packages.
    /// </summary>
    public required ArtifactBindingRef Binding { get; init; }
}

/// <summary>
/// Compact feature shape used by saved-query previews.
/// </summary>
public sealed record SavedQueryPreviewFeature
{
    /// <summary>
    /// Feature identifier.
    /// </summary>
    public required long Id { get; init; }

    /// <summary>
    /// Feature attributes.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Attributes { get; init; } =
        ImmutableDictionary<string, object?>.Empty;

    /// <summary>
    /// True when the feature has a geometry payload.
    /// </summary>
    public bool HasGeometry { get; init; }

    /// <summary>
    /// Creates a preview feature from a canonical feature.
    /// </summary>
    /// <param name="feature">Canonical feature.</param>
    /// <returns>Preview feature.</returns>
    public static SavedQueryPreviewFeature FromFeature(Feature feature)
        => new()
        {
            Id = feature.Id,
            Attributes = feature.Attributes,
            HasGeometry = feature.Geometry is { Length: > 0 }
        };
}

/// <summary>
/// Safe failed-job diagnostic summary.
/// </summary>
public sealed record AnalysisJobFailure
{
    /// <summary>
    /// Job identifier.
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// Safe failure classification.
    /// </summary>
    public required AnalysisJobFailureClassification Classification { get; init; }

    /// <summary>
    /// Safe client-facing failure message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// True when the job is terminal.
    /// </summary>
    public required bool IsTerminal { get; init; }

    /// <summary>
    /// Failure timestamp, when available.
    /// </summary>
    public DateTimeOffset? FailedAt { get; init; }
}

/// <summary>
/// Bounded structured logs for an analysis job.
/// </summary>
public sealed record AnalysisJobLogs
{
    /// <summary>
    /// Job identifier.
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// Safe structured log entries.
    /// </summary>
    public IReadOnlyList<AnalysisJobLogEntry> Entries { get; init; } = [];

    /// <summary>
    /// Number of source entries before truncation.
    /// </summary>
    public required int TotalCount { get; init; }

    /// <summary>
    /// True when entries were truncated to the requested bound.
    /// </summary>
    public required bool Truncated { get; init; }
}

/// <summary>
/// Safe structured log entry for an analysis job.
/// </summary>
public sealed record AnalysisJobLogEntry
{
    /// <summary>
    /// UTC timestamp.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Log level.
    /// </summary>
    public required ExecutionLogLevel Level { get; init; }

    /// <summary>
    /// Safe log message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Optional execution phase.
    /// </summary>
    public string? Phase { get; init; }

    /// <summary>
    /// Safe bounded metadata values.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

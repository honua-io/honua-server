// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.NlQuery.Domain;

namespace Honua.Core.Features.AnalysisContent.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<AnalysisContentKind>))]
public enum AnalysisContentKind
{
    [JsonStringEnumMemberName("savedQuery")]
    SavedQuery,

    [JsonStringEnumMemberName("analysisPackage")]
    AnalysisPackage
}

[JsonConverter(typeof(JsonStringEnumConverter<AnalysisContentLifecycle>))]
public enum AnalysisContentLifecycle
{
    [JsonStringEnumMemberName("active")]
    Active,

    [JsonStringEnumMemberName("archived")]
    Archived,

    [JsonStringEnumMemberName("deleted")]
    Deleted
}

[JsonConverter(typeof(JsonStringEnumConverter<ResultArtifactRetentionState>))]
public enum ResultArtifactRetentionState
{
    [JsonStringEnumMemberName("preview")]
    Preview,

    [JsonStringEnumMemberName("retained")]
    Retained,

    [JsonStringEnumMemberName("promoted")]
    Promoted,

    [JsonStringEnumMemberName("expired")]
    Expired
}

[JsonConverter(typeof(JsonStringEnumConverter<ResultArtifactPromotionState>))]
public enum ResultArtifactPromotionState
{
    [JsonStringEnumMemberName("none")]
    None,

    [JsonStringEnumMemberName("promoted")]
    Promoted
}

[JsonConverter(typeof(JsonStringEnumConverter<AnalysisJobFailureClassification>))]
public enum AnalysisJobFailureClassification
{
    [JsonStringEnumMemberName("validationFailed")]
    ValidationFailed,

    [JsonStringEnumMemberName("authorizationDenied")]
    AuthorizationDenied,

    [JsonStringEnumMemberName("cancelled")]
    Cancelled,

    [JsonStringEnumMemberName("timedOut")]
    TimedOut,

    [JsonStringEnumMemberName("artifactOutputFailed")]
    ArtifactOutputFailed,

    [JsonStringEnumMemberName("executionFailed")]
    ExecutionFailed,

    [JsonStringEnumMemberName("storeUnavailable")]
    StoreUnavailable,

    [JsonStringEnumMemberName("unknown")]
    Unknown
}

public sealed record AnalysisContentItem
{
    public required string ItemId { get; init; }

    public required AnalysisContentKind Kind { get; init; }

    public required string Name { get; init; }

    public string? Title { get; init; }

    public string? OwnerId { get; init; }

    public string Visibility { get; init; } = "organization";

    public required int CurrentVersion { get; init; }

    public required string CurrentVersionId { get; init; }

    public AnalysisContentLifecycle Lifecycle { get; init; } = AnalysisContentLifecycle.Active;

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public string? CreatedBy { get; init; }
}

public sealed record AnalysisContentItemQuery
{
    public AnalysisContentKind? Kind { get; init; }

    public AnalysisContentLifecycle? Lifecycle { get; init; }

    public required int Limit { get; init; }

    public required int Offset { get; init; }
}

public sealed record AnalysisContentItemPage
{
    public IReadOnlyList<AnalysisContentItem> Items { get; init; } = [];

    public required long TotalCount { get; init; }
}

public sealed record AnalysisContentVersion
{
    public required string VersionId { get; init; }

    public required string ItemId { get; init; }

    public required int Version { get; init; }

    public required AnalysisContentKind Kind { get; init; }

    public SavedQueryContent? SavedQuery { get; init; }

    public AnalysisPackageContent? AnalysisPackage { get; init; }

    public required string ContentHash { get; init; }

    public string? BasedOnVersionId { get; init; }

    public string? CreatedFromJobId { get; init; }

    public IReadOnlyList<string> CreatedFromArtifactIds { get; init; } = [];

    public required DateTimeOffset CreatedAt { get; init; }

    public string? CreatedBy { get; init; }
}

public sealed record SavedQueryContent
{
    public string? NaturalLanguageQuery { get; init; }

    public required int LayerId { get; init; }

    public string? ServiceName { get; init; }

    public FilterPlan? FilterPlan { get; init; }

    public IReadOnlyList<string> OutFields { get; init; } = [];

    public int? OutputSrid { get; init; }

    public int? PreviewLimit { get; init; }

    public string? OutputFormat { get; init; }

    public string? Units { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

public sealed record AnalysisPackageContent
{
    public AnalysisIntent? Intent { get; init; }

    public required AnalysisPlan Plan { get; init; }

    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<ArtifactKind> RequestedArtifacts { get; init; } = [];

    public IReadOnlyList<ArtifactBindingRef> BindingHints { get; init; } = [];

    public int? SpatialReferenceId { get; init; }

    public string? Units { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

public sealed record ArtifactBindingRef
{
    public required string ArtifactId { get; init; }

    public string? SourceItemId { get; init; }

    public int? SourceVersion { get; init; }

    public string? SourceVersionId { get; init; }

    public string Role { get; init; } = "dataSource";

    public string TargetKind { get; init; } = "content";

    public string TargetSlot { get; init; } = "default";
}

public sealed record ResultArtifactRecord
{
    public required string ArtifactId { get; init; }

    public required string ResultPackageId { get; init; }

    public required string JobId { get; init; }

    public required string SourceItemId { get; init; }

    public required int SourceVersion { get; init; }

    public required string SourceVersionId { get; init; }

    public required ArtifactKind Kind { get; init; }

    public required string Label { get; init; }

    public string? Uri { get; init; }

    public string? ContentType { get; init; }

    public long? ByteSize { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> Provenance { get; init; } = new Dictionary<string, string>();

    public ResultArtifactRetentionState RetentionState { get; init; } = ResultArtifactRetentionState.Retained;

    public ResultArtifactPromotionState PromotionState { get; init; } = ResultArtifactPromotionState.None;

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed record AnalysisContentEstimate
{
    public required int StepCount { get; init; }

    public required int QueryStepCount { get; init; }

    public required int GeoprocessStepCount { get; init; }

    public long? EstimatedInputFeatureCount { get; init; }

    public required double EstimatedComputeUnits { get; init; }

    public required decimal EstimatedCostUsd { get; init; }

    public required double EstimatedRuntimeSeconds { get; init; }

    public required bool IsLowerBound { get; init; }

    public IReadOnlyList<AnalysisContentLayerEstimate> Inputs { get; init; } = [];
}

public sealed record AnalysisContentLayerEstimate
{
    public required string StepId { get; init; }

    public required int LayerId { get; init; }

    public long? EstimatedFeatureCount { get; init; }

    public required bool Unresolved { get; init; }
}

public sealed record SavedQueryPreviewResult
{
    public required string PreviewArtifactId { get; init; }

    public required string ItemId { get; init; }

    public required int Version { get; init; }

    public required int LayerId { get; init; }

    public IReadOnlyList<SavedQueryPreviewFeature> Features { get; init; } = [];

    public long? TotalCount { get; init; }

    public bool ExceededPreviewLimit { get; init; }

    public required ArtifactBindingRef Binding { get; init; }
}

public sealed record SavedQueryPreviewFeature
{
    public required long Id { get; init; }

    public IReadOnlyDictionary<string, object?> Attributes { get; init; } =
        ImmutableDictionary<string, object?>.Empty;

    public bool HasGeometry { get; init; }

    public static SavedQueryPreviewFeature FromFeature(Feature feature)
        => new()
        {
            Id = feature.Id,
            Attributes = feature.Attributes,
            HasGeometry = feature.Geometry is { Length: > 0 }
        };
}

public sealed record AnalysisJobFailure
{
    public required string JobId { get; init; }

    public required AnalysisJobFailureClassification Classification { get; init; }

    public required string Message { get; init; }

    public required bool IsTerminal { get; init; }

    public DateTimeOffset? FailedAt { get; init; }
}

public sealed record AnalysisJobLogs
{
    public required string JobId { get; init; }

    public IReadOnlyList<AnalysisJobLogEntry> Entries { get; init; } = [];

    public required int TotalCount { get; init; }

    public required bool Truncated { get; init; }
}

public sealed record AnalysisJobLogEntry
{
    public required DateTimeOffset Timestamp { get; init; }

    public required ExecutionLogLevel Level { get; init; }

    public required string Message { get; init; }

    public string? Phase { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

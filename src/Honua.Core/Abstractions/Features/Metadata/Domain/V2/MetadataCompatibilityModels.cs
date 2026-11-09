// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

public sealed record MetadataCompatibilityPrevalidationRequest
{
    [JsonPropertyName("releasePackageId")]
    public Guid? ReleasePackageId { get; init; }

    [JsonPropertyName("releasePackage")]
    public MetadataReleasePackage? ReleasePackage { get; init; }

    [JsonPropertyName("targetEnvironment")]
    public required string TargetEnvironment { get; init; }

    [JsonPropertyName("dataScripts")]
    public IReadOnlyList<MetadataDataScriptEntry> DataScripts { get; init; } = Array.Empty<MetadataDataScriptEntry>();
}

public sealed record MetadataCompatibilityReport
{
    [JsonPropertyName("targetEnvironment")]
    public required string TargetEnvironment { get; init; }

    [JsonPropertyName("releasePackageId")]
    public Guid? ReleasePackageId { get; init; }

    [JsonPropertyName("packageKey")]
    public string? PackageKey { get; init; }

    [JsonPropertyName("sourceEnvironment")]
    public string? SourceEnvironment { get; init; }

    [JsonPropertyName("sourceRevision")]
    public long? SourceRevision { get; init; }

    [JsonPropertyName("sourceEtag")]
    public string? SourceEtag { get; init; }

    [JsonPropertyName("targetRevision")]
    public long? TargetRevision { get; init; }

    [JsonPropertyName("targetEtag")]
    public string? TargetEtag { get; init; }

    [JsonPropertyName("generatedAt")]
    public required DateTimeOffset GeneratedAt { get; init; }

    [JsonPropertyName("status")]
    public required MetadataCompatibilityStatus Status { get; init; }

    [JsonPropertyName("canCreatePullRequest")]
    public bool CanCreatePullRequest { get; init; }

    [JsonPropertyName("canPromote")]
    public bool CanPromote { get; init; }

    [JsonPropertyName("uncoveredErrorCount")]
    public int UncoveredErrorCount { get; init; }

    [JsonPropertyName("coveredErrorCount")]
    public int CoveredErrorCount { get; init; }

    [JsonPropertyName("warningCount")]
    public int WarningCount { get; init; }

    [JsonPropertyName("scriptCount")]
    public int ScriptCount { get; init; }

    [JsonPropertyName("findings")]
    public IReadOnlyList<MetadataCompatibilityFinding> Findings { get; init; } =
        Array.Empty<MetadataCompatibilityFinding>();

    [JsonPropertyName("affectedDependents")]
    public IReadOnlyList<MetadataAffectedDependent> AffectedDependents { get; init; } =
        Array.Empty<MetadataAffectedDependent>();

    [JsonPropertyName("rollbackReadiness")]
    public required MetadataRollbackReadiness RollbackReadiness { get; init; }
}

public sealed record MetadataCompatibilityFinding
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("severity")]
    public required MetadataCompatibilityFindingSeverity Severity { get; init; }

    [JsonPropertyName("kind")]
    public required MetadataCompatibilityFindingKind Kind { get; init; }

    [JsonPropertyName("affectedSemanticId")]
    public required string AffectedSemanticId { get; init; }

    [JsonPropertyName("affectedSemanticKind")]
    public required MetadataSemanticArtifactKind AffectedSemanticKind { get; init; }

    [JsonPropertyName("affectedParentSemanticId")]
    public string? AffectedParentSemanticId { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("expected")]
    public required MetadataCompatibilityValue Expected { get; init; }

    [JsonPropertyName("actual")]
    public required MetadataCompatibilityValue Actual { get; init; }

    [JsonPropertyName("requiredAction")]
    public required MetadataCompatibilityRequiredAction RequiredAction { get; init; }

    [JsonPropertyName("coverageState")]
    public MetadataCompatibilityCoverageState CoverageState { get; init; } =
        MetadataCompatibilityCoverageState.NotApplicable;

    [JsonPropertyName("coveringScriptId")]
    public string? CoveringScriptId { get; init; }

    [JsonPropertyName("coverageNotes")]
    public string? CoverageNotes { get; init; }
}

public sealed record MetadataCompatibilityValue
{
    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("details")]
    public IReadOnlyDictionary<string, string> Details { get; init; } = new Dictionary<string, string>();
}

public sealed record MetadataAffectedDependent
{
    [JsonPropertyName("semanticId")]
    public required string SemanticId { get; init; }

    [JsonPropertyName("semanticKind")]
    public required MetadataSemanticArtifactKind SemanticKind { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("impactSeverity")]
    public required MetadataCompatibilityFindingSeverity ImpactSeverity { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("viaSemanticId")]
    public string? ViaSemanticId { get; init; }

    [JsonPropertyName("relationshipKind")]
    public string? RelationshipKind { get; init; }
}

public sealed record MetadataRollbackReadiness
{
    [JsonPropertyName("classification")]
    public required MetadataRollbackReadinessClassification Classification { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("requiresSnapshot")]
    public bool RequiresSnapshot { get; init; }

    [JsonPropertyName("requiresManualAction")]
    public bool RequiresManualAction { get; init; }

    [JsonPropertyName("scriptIds")]
    public IReadOnlyList<string> ScriptIds { get; init; } = Array.Empty<string>();
}

public sealed record MetadataDataScriptEntry
{
    [JsonPropertyName("scriptId")]
    public required string ScriptId { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("targetEnvironment")]
    public string? TargetEnvironment { get; init; }

    [JsonPropertyName("reversible")]
    public bool Reversible { get; init; }

    [JsonPropertyName("beforeContract")]
    public MetadataDataScriptContract? BeforeContract { get; init; }

    [JsonPropertyName("afterContract")]
    public MetadataDataScriptContract? AfterContract { get; init; }

    [JsonPropertyName("declaredOperations")]
    public IReadOnlyList<string> DeclaredOperations { get; init; } = Array.Empty<string>();

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }
}

public sealed record MetadataDataScriptContract
{
    [JsonPropertyName("resources")]
    public IReadOnlyList<MetadataScriptResourceContract> Resources { get; init; } =
        Array.Empty<MetadataScriptResourceContract>();
}

public sealed record MetadataScriptResourceContract
{
    [JsonPropertyName("semanticId")]
    public required string SemanticId { get; init; }

    [JsonPropertyName("semanticKind")]
    public MetadataSemanticArtifactKind SemanticKind { get; init; } = MetadataSemanticArtifactKind.Resource;

    [JsonPropertyName("resourceId")]
    public string? ResourceId { get; init; }

    [JsonPropertyName("serviceId")]
    public string? ServiceId { get; init; }

    [JsonPropertyName("exists")]
    public bool? Exists { get; init; }

    [JsonPropertyName("resourceType")]
    public MetadataV2ResourceType? ResourceType { get; init; }

    [JsonPropertyName("serviceType")]
    public MetadataV2ServiceType? ServiceType { get; init; }

    [JsonPropertyName("publicationType")]
    public MetadataV2PublicationType? PublicationType { get; init; }

    [JsonPropertyName("route")]
    public string? Route { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("serviceLocalId")]
    public string? ServiceLocalId { get; init; }

    [JsonPropertyName("layerIndex")]
    public int? LayerIndex { get; init; }

    [JsonPropertyName("fields")]
    public IReadOnlyList<MetadataScriptFieldContract> Fields { get; init; } =
        Array.Empty<MetadataScriptFieldContract>();

    [JsonPropertyName("requiredIdentifiers")]
    public IReadOnlyList<string> RequiredIdentifiers { get; init; } = Array.Empty<string>();

    [JsonPropertyName("domains")]
    public IReadOnlyList<string> Domains { get; init; } = Array.Empty<string>();

    [JsonPropertyName("indexes")]
    public IReadOnlyList<string> Indexes { get; init; } = Array.Empty<string>();

    [JsonPropertyName("spatial")]
    public MetadataScriptSpatialContract? Spatial { get; init; }

    [JsonPropertyName("temporal")]
    public MetadataScriptTemporalContract? Temporal { get; init; }

    [JsonPropertyName("storage")]
    public MetadataScriptStorageContract? Storage { get; init; }

    [JsonPropertyName("capabilities")]
    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();

    [JsonPropertyName("supportedFormats")]
    public IReadOnlyList<string> SupportedFormats { get; init; } = Array.Empty<string>();
}

public sealed record MetadataScriptFieldContract
{
    [JsonPropertyName("semanticId")]
    public string? SemanticId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("exists")]
    public bool? Exists { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("nullable")]
    public bool? Nullable { get; init; }

    [JsonPropertyName("semanticRoles")]
    public IReadOnlyList<string> SemanticRoles { get; init; } = Array.Empty<string>();

    [JsonPropertyName("domains")]
    public IReadOnlyList<string> Domains { get; init; } = Array.Empty<string>();

    [JsonPropertyName("indexes")]
    public IReadOnlyList<string> Indexes { get; init; } = Array.Empty<string>();
}

public sealed record MetadataScriptSpatialContract
{
    [JsonPropertyName("geometryType")]
    public string? GeometryType { get; init; }

    [JsonPropertyName("srid")]
    public int? Srid { get; init; }
}

public sealed record MetadataScriptTemporalContract
{
    [JsonPropertyName("startTimeField")]
    public string? StartTimeField { get; init; }

    [JsonPropertyName("endTimeField")]
    public string? EndTimeField { get; init; }

    [JsonPropertyName("trackIdField")]
    public string? TrackIdField { get; init; }
}

public sealed record MetadataScriptStorageContract
{
    [JsonPropertyName("storageBindingId")]
    public string? StorageBindingId { get; init; }

    [JsonPropertyName("storageType")]
    public MetadataV2StorageType? StorageType { get; init; }

    [JsonPropertyName("capabilities")]
    public IReadOnlyList<MetadataV2StorageBindingCapability> Capabilities { get; init; } =
        Array.Empty<MetadataV2StorageBindingCapability>();
}

[JsonConverter(typeof(JsonStringEnumConverter<MetadataCompatibilityStatus>))]
public enum MetadataCompatibilityStatus
{
    [JsonStringEnumMemberName("ready")]
    Ready,

    [JsonStringEnumMemberName("warning")]
    Warning,

    [JsonStringEnumMemberName("blocked")]
    Blocked,

    [JsonStringEnumMemberName("unknown")]
    Unknown,
}

[JsonConverter(typeof(JsonStringEnumConverter<MetadataCompatibilityFindingSeverity>))]
public enum MetadataCompatibilityFindingSeverity
{
    [JsonStringEnumMemberName("info")]
    Info,

    [JsonStringEnumMemberName("warning")]
    Warning,

    [JsonStringEnumMemberName("error")]
    Error,
}

[JsonConverter(typeof(JsonStringEnumConverter<MetadataCompatibilityFindingKind>))]
public enum MetadataCompatibilityFindingKind
{
    [JsonStringEnumMemberName("state")]
    State,

    [JsonStringEnumMemberName("resource")]
    Resource,

    [JsonStringEnumMemberName("field")]
    Field,

    [JsonStringEnumMemberName("identifier")]
    Identifier,

    [JsonStringEnumMemberName("spatial")]
    Spatial,

    [JsonStringEnumMemberName("temporal")]
    Temporal,

    [JsonStringEnumMemberName("storage")]
    Storage,

    [JsonStringEnumMemberName("service")]
    Service,

    [JsonStringEnumMemberName("publication")]
    Publication,

    [JsonStringEnumMemberName("projection")]
    Projection,

    [JsonStringEnumMemberName("policy")]
    Policy,

    [JsonStringEnumMemberName("script")]
    Script,
}

[JsonConverter(typeof(JsonStringEnumConverter<MetadataCompatibilityCoverageState>))]
public enum MetadataCompatibilityCoverageState
{
    [JsonStringEnumMemberName("not-applicable")]
    NotApplicable,

    [JsonStringEnumMemberName("uncovered")]
    Uncovered,

    [JsonStringEnumMemberName("covered-by-script")]
    CoveredByScript,

    [JsonStringEnumMemberName("unknown")]
    Unknown,
}

[JsonConverter(typeof(JsonStringEnumConverter<MetadataCompatibilityRequiredAction>))]
public enum MetadataCompatibilityRequiredAction
{
    [JsonStringEnumMemberName("none")]
    None,

    [JsonStringEnumMemberName("resolve-state")]
    ResolveState,

    [JsonStringEnumMemberName("add-resource")]
    AddResource,

    [JsonStringEnumMemberName("update-resource")]
    UpdateResource,

    [JsonStringEnumMemberName("add-field")]
    AddField,

    [JsonStringEnumMemberName("update-field")]
    UpdateField,

    [JsonStringEnumMemberName("update-identifier")]
    UpdateIdentifier,

    [JsonStringEnumMemberName("update-spatial")]
    UpdateSpatial,

    [JsonStringEnumMemberName("update-temporal")]
    UpdateTemporal,

    [JsonStringEnumMemberName("update-storage")]
    UpdateStorage,

    [JsonStringEnumMemberName("add-service")]
    AddService,

    [JsonStringEnumMemberName("update-service")]
    UpdateService,

    [JsonStringEnumMemberName("add-publication")]
    AddPublication,

    [JsonStringEnumMemberName("update-publication")]
    UpdatePublication,

    [JsonStringEnumMemberName("run-data-script")]
    RunDataScript,

    [JsonStringEnumMemberName("manual-review")]
    ManualReview,
}

[JsonConverter(typeof(JsonStringEnumConverter<MetadataRollbackReadinessClassification>))]
public enum MetadataRollbackReadinessClassification
{
    [JsonStringEnumMemberName("metadata-only")]
    MetadataOnly,

    [JsonStringEnumMemberName("service-revision")]
    ServiceRevision,

    [JsonStringEnumMemberName("script-reversible")]
    ScriptReversible,

    [JsonStringEnumMemberName("snapshot-required")]
    SnapshotRequired,

    [JsonStringEnumMemberName("manual")]
    Manual,
}

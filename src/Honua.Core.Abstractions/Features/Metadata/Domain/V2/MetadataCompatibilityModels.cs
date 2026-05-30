// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Request for Metadata v2 release-package compatibility prevalidation.
/// </summary>
public sealed record MetadataCompatibilityPrevalidationRequest
{
    /// <summary>Persisted release package identifier to resolve before analysis.</summary>
    [JsonPropertyName("releasePackageId")]
    public Guid? ReleasePackageId { get; init; }

    /// <summary>Inline release package payload for pre-PR compatibility checks.</summary>
    [JsonPropertyName("releasePackage")]
    public MetadataReleasePackage? ReleasePackage { get; init; }

    /// <summary>Target environment whose current graph snapshot is compared.</summary>
    [JsonPropertyName("targetEnvironment")]
    public required string TargetEnvironment { get; init; }

    /// <summary>Optional declared data scripts that may cover compatibility gaps.</summary>
    [JsonPropertyName("dataScripts")]
    public IReadOnlyList<MetadataDataScriptEntry> DataScripts { get; init; } = Array.Empty<MetadataDataScriptEntry>();
}

/// <summary>
/// Environment-scoped compatibility report for a release package.
/// </summary>
public sealed record MetadataCompatibilityReport
{
    /// <summary>Target environment analyzed.</summary>
    [JsonPropertyName("targetEnvironment")]
    public required string TargetEnvironment { get; init; }

    /// <summary>Release package identifier, when known.</summary>
    [JsonPropertyName("releasePackageId")]
    public Guid? ReleasePackageId { get; init; }

    /// <summary>Release package key, when known.</summary>
    [JsonPropertyName("packageKey")]
    public string? PackageKey { get; init; }

    /// <summary>Source environment declared by the release package.</summary>
    [JsonPropertyName("sourceEnvironment")]
    public string? SourceEnvironment { get; init; }

    /// <summary>Source Metadata v2 revision used for desired state.</summary>
    [JsonPropertyName("sourceRevision")]
    public long? SourceRevision { get; init; }

    /// <summary>Source graph entity tag used for desired state.</summary>
    [JsonPropertyName("sourceEtag")]
    public string? SourceEtag { get; init; }

    /// <summary>Target Metadata v2 revision observed for comparison.</summary>
    [JsonPropertyName("targetRevision")]
    public long? TargetRevision { get; init; }

    /// <summary>Target graph entity tag observed for comparison.</summary>
    [JsonPropertyName("targetEtag")]
    public string? TargetEtag { get; init; }

    /// <summary>Timestamp when the report was generated.</summary>
    [JsonPropertyName("generatedAt")]
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Overall compatibility status.</summary>
    [JsonPropertyName("status")]
    public required MetadataCompatibilityStatus Status { get; init; }

    /// <summary>True when the report has no uncovered blockers and is not unknown.</summary>
    [JsonPropertyName("canCreatePullRequest")]
    public bool CanCreatePullRequest { get; init; }

    /// <summary>True when the report has no uncovered blockers and is not unknown.</summary>
    [JsonPropertyName("canPromote")]
    public bool CanPromote { get; init; }

    /// <summary>Number of error findings that are not covered by a declared data script.</summary>
    [JsonPropertyName("uncoveredErrorCount")]
    public int UncoveredErrorCount { get; init; }

    /// <summary>Number of error findings covered by a declared data script.</summary>
    [JsonPropertyName("coveredErrorCount")]
    public int CoveredErrorCount { get; init; }

    /// <summary>Number of warning findings.</summary>
    [JsonPropertyName("warningCount")]
    public int WarningCount { get; init; }

    /// <summary>Number of data scripts supplied with the request.</summary>
    [JsonPropertyName("scriptCount")]
    public int ScriptCount { get; init; }

    /// <summary>Compatibility findings in deterministic order.</summary>
    [JsonPropertyName("findings")]
    public IReadOnlyList<MetadataCompatibilityFinding> Findings { get; init; } =
        Array.Empty<MetadataCompatibilityFinding>();

    /// <summary>Affected dependent artifacts for Console blast-radius visualization.</summary>
    [JsonPropertyName("affectedDependents")]
    public IReadOnlyList<MetadataAffectedDependent> AffectedDependents { get; init; } =
        Array.Empty<MetadataAffectedDependent>();

    /// <summary>Rollback readiness classification for the proposed package.</summary>
    [JsonPropertyName("rollbackReadiness")]
    public required MetadataRollbackReadiness RollbackReadiness { get; init; }
}

/// <summary>
/// One compatibility finding in a prevalidation report.
/// </summary>
public sealed record MetadataCompatibilityFinding
{
    /// <summary>Stable machine-readable compatibility code.</summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    /// <summary>Finding severity.</summary>
    [JsonPropertyName("severity")]
    public required MetadataCompatibilityFindingSeverity Severity { get; init; }

    /// <summary>Finding category.</summary>
    [JsonPropertyName("kind")]
    public required MetadataCompatibilityFindingKind Kind { get; init; }

    /// <summary>Affected semantic artifact identifier.</summary>
    [JsonPropertyName("affectedSemanticId")]
    public required string AffectedSemanticId { get; init; }

    /// <summary>Affected semantic artifact kind.</summary>
    [JsonPropertyName("affectedSemanticKind")]
    public required MetadataSemanticArtifactKind AffectedSemanticKind { get; init; }

    /// <summary>Parent semantic identifier for field or publication findings.</summary>
    [JsonPropertyName("affectedParentSemanticId")]
    public string? AffectedParentSemanticId { get; init; }

    /// <summary>Optional human-readable affected artifact name.</summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    /// <summary>Short safe message describing the compatibility issue.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>Expected secret-safe value or details.</summary>
    [JsonPropertyName("expected")]
    public required MetadataCompatibilityValue Expected { get; init; }

    /// <summary>Actual secret-safe value or details.</summary>
    [JsonPropertyName("actual")]
    public required MetadataCompatibilityValue Actual { get; init; }

    /// <summary>Required action for a human or automation workflow.</summary>
    [JsonPropertyName("requiredAction")]
    public required MetadataCompatibilityRequiredAction RequiredAction { get; init; }

    /// <summary>Coverage state after declared data scripts are evaluated.</summary>
    [JsonPropertyName("coverageState")]
    public MetadataCompatibilityCoverageState CoverageState { get; init; } =
        MetadataCompatibilityCoverageState.NotApplicable;

    /// <summary>Identifier of the data script that covers this finding, when any.</summary>
    [JsonPropertyName("coveringScriptId")]
    public string? CoveringScriptId { get; init; }

    /// <summary>Optional notes about script coverage or applicability.</summary>
    [JsonPropertyName("coverageNotes")]
    public string? CoverageNotes { get; init; }
}

/// <summary>
/// Secret-safe expected or actual value carried by a compatibility finding.
/// </summary>
public sealed record MetadataCompatibilityValue
{
    /// <summary>Value label, such as field, type, or CRS.</summary>
    [JsonPropertyName("label")]
    public required string Label { get; init; }

    /// <summary>Primary value rendered as a safe string.</summary>
    [JsonPropertyName("value")]
    public string? Value { get; init; }

    /// <summary>Additional safe key/value details.</summary>
    [JsonPropertyName("details")]
    public IReadOnlyDictionary<string, string> Details { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Dependent artifact affected by one or more compatibility findings.
/// </summary>
public sealed record MetadataAffectedDependent
{
    /// <summary>Dependent semantic identifier.</summary>
    [JsonPropertyName("semanticId")]
    public required string SemanticId { get; init; }

    /// <summary>Dependent semantic kind.</summary>
    [JsonPropertyName("semanticKind")]
    public required MetadataSemanticArtifactKind SemanticKind { get; init; }

    /// <summary>Optional display name.</summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    /// <summary>Impact severity for Console visualization.</summary>
    [JsonPropertyName("impactSeverity")]
    public required MetadataCompatibilityFindingSeverity ImpactSeverity { get; init; }

    /// <summary>Safe reason for the dependent impact.</summary>
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    /// <summary>Semantic identifier through which the dependency is reached.</summary>
    [JsonPropertyName("viaSemanticId")]
    public string? ViaSemanticId { get; init; }

    /// <summary>Relationship kind or path label.</summary>
    [JsonPropertyName("relationshipKind")]
    public string? RelationshipKind { get; init; }
}

/// <summary>
/// Rollback readiness for a compatibility report.
/// </summary>
public sealed record MetadataRollbackReadiness
{
    /// <summary>Rollback classification.</summary>
    [JsonPropertyName("classification")]
    public required MetadataRollbackReadinessClassification Classification { get; init; }

    /// <summary>Safe reason for the classification.</summary>
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    /// <summary>True when rollback requires an external data snapshot.</summary>
    [JsonPropertyName("requiresSnapshot")]
    public bool RequiresSnapshot { get; init; }

    /// <summary>True when rollback requires manual operator intervention.</summary>
    [JsonPropertyName("requiresManualAction")]
    public bool RequiresManualAction { get; init; }

    /// <summary>Data scripts involved in the rollback classification.</summary>
    [JsonPropertyName("scriptIds")]
    public IReadOnlyList<string> ScriptIds { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Data script declaration used only for compatibility coverage analysis.
/// </summary>
public sealed record MetadataDataScriptEntry
{
    /// <summary>Stable script identifier.</summary>
    [JsonPropertyName("scriptId")]
    public required string ScriptId { get; init; }

    /// <summary>Optional display title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Script kind or implementation family.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary>Optional target environment. Empty means the script is environment-agnostic.</summary>
    [JsonPropertyName("targetEnvironment")]
    public string? TargetEnvironment { get; init; }

    /// <summary>True when the script declares a reversible data operation.</summary>
    [JsonPropertyName("reversible")]
    public bool Reversible { get; init; }

    /// <summary>Declared state that must match the target before the script applies.</summary>
    [JsonPropertyName("beforeContract")]
    public MetadataDataScriptContract? BeforeContract { get; init; }

    /// <summary>Declared state expected after the script applies.</summary>
    [JsonPropertyName("afterContract")]
    public MetadataDataScriptContract? AfterContract { get; init; }

    /// <summary>Declared operation labels, such as add-field or alter-type.</summary>
    [JsonPropertyName("declaredOperations")]
    public IReadOnlyList<string> DeclaredOperations { get; init; } = Array.Empty<string>();

    /// <summary>Optional script notes.</summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; init; }
}

/// <summary>
/// Declared semantic state before or after a data script.
/// </summary>
public sealed record MetadataDataScriptContract
{
    /// <summary>Semantic artifact contracts included in the data script declaration.</summary>
    [JsonPropertyName("resources")]
    public IReadOnlyList<MetadataScriptResourceContract> Resources { get; init; } =
        Array.Empty<MetadataScriptResourceContract>();
}

/// <summary>
/// Declared script contract for a Metadata v2 semantic artifact.
/// </summary>
public sealed record MetadataScriptResourceContract
{
    /// <summary>Semantic artifact identifier represented by this contract.</summary>
    [JsonPropertyName("semanticId")]
    public required string SemanticId { get; init; }

    /// <summary>Semantic artifact kind.</summary>
    [JsonPropertyName("semanticKind")]
    public MetadataSemanticArtifactKind SemanticKind { get; init; } = MetadataSemanticArtifactKind.Resource;

    /// <summary>Parent resource identifier for field or publication contracts.</summary>
    [JsonPropertyName("resourceId")]
    public string? ResourceId { get; init; }

    /// <summary>Parent service identifier for publication contracts.</summary>
    [JsonPropertyName("serviceId")]
    public string? ServiceId { get; init; }

    /// <summary>Whether the artifact is expected to exist.</summary>
    [JsonPropertyName("exists")]
    public bool? Exists { get; init; }

    /// <summary>Resource type expected after the script.</summary>
    [JsonPropertyName("resourceType")]
    public MetadataV2ResourceType? ResourceType { get; init; }

    /// <summary>Service type expected after the script.</summary>
    [JsonPropertyName("serviceType")]
    public MetadataV2ServiceType? ServiceType { get; init; }

    /// <summary>Publication type expected after the script.</summary>
    [JsonPropertyName("publicationType")]
    public MetadataV2PublicationType? PublicationType { get; init; }

    /// <summary>Service route expected after the script.</summary>
    [JsonPropertyName("route")]
    public string? Route { get; init; }

    /// <summary>Publication path expected after the script.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>Publication service-local identifier expected after the script.</summary>
    [JsonPropertyName("serviceLocalId")]
    public string? ServiceLocalId { get; init; }

    /// <summary>Publication layer index expected after the script.</summary>
    [JsonPropertyName("layerIndex")]
    public int? LayerIndex { get; init; }

    /// <summary>Fields expected before or after the script.</summary>
    [JsonPropertyName("fields")]
    public IReadOnlyList<MetadataScriptFieldContract> Fields { get; init; } =
        Array.Empty<MetadataScriptFieldContract>();

    /// <summary>Required field identifiers expected before or after the script.</summary>
    [JsonPropertyName("requiredIdentifiers")]
    public IReadOnlyList<string> RequiredIdentifiers { get; init; } = Array.Empty<string>();

    /// <summary>Canonical domain identifiers expected before or after the script.</summary>
    [JsonPropertyName("domains")]
    public IReadOnlyList<string> Domains { get; init; } = Array.Empty<string>();

    /// <summary>Canonical index identifiers expected before or after the script.</summary>
    [JsonPropertyName("indexes")]
    public IReadOnlyList<string> Indexes { get; init; } = Array.Empty<string>();

    /// <summary>Spatial metadata expected before or after the script.</summary>
    [JsonPropertyName("spatial")]
    public MetadataScriptSpatialContract? Spatial { get; init; }

    /// <summary>Temporal metadata expected before or after the script.</summary>
    [JsonPropertyName("temporal")]
    public MetadataScriptTemporalContract? Temporal { get; init; }

    /// <summary>Storage metadata expected before or after the script.</summary>
    [JsonPropertyName("storage")]
    public MetadataScriptStorageContract? Storage { get; init; }

    /// <summary>Publication capabilities expected before or after the script.</summary>
    [JsonPropertyName("capabilities")]
    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();

    /// <summary>Publication formats expected before or after the script.</summary>
    [JsonPropertyName("supportedFormats")]
    public IReadOnlyList<string> SupportedFormats { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Field-level before or after contract for a data script.
/// </summary>
public sealed record MetadataScriptFieldContract
{
    /// <summary>Field semantic identifier, when available.</summary>
    [JsonPropertyName("semanticId")]
    public string? SemanticId { get; init; }

    /// <summary>Field name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Whether the field is expected to exist.</summary>
    [JsonPropertyName("exists")]
    public bool? Exists { get; init; }

    /// <summary>Field type.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>Field nullability.</summary>
    [JsonPropertyName("nullable")]
    public bool? Nullable { get; init; }

    /// <summary>Semantic roles expected on the field.</summary>
    [JsonPropertyName("semanticRoles")]
    public IReadOnlyList<string> SemanticRoles { get; init; } = Array.Empty<string>();

    /// <summary>Canonical domain identifiers expected on the field.</summary>
    [JsonPropertyName("domains")]
    public IReadOnlyList<string> Domains { get; init; } = Array.Empty<string>();

    /// <summary>Canonical index identifiers expected on the field.</summary>
    [JsonPropertyName("indexes")]
    public IReadOnlyList<string> Indexes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Spatial before or after contract for a data script.
/// </summary>
public sealed record MetadataScriptSpatialContract
{
    /// <summary>Geometry type.</summary>
    [JsonPropertyName("geometryType")]
    public string? GeometryType { get; init; }

    /// <summary>Spatial reference identifier.</summary>
    [JsonPropertyName("srid")]
    public int? Srid { get; init; }
}

/// <summary>
/// Temporal before or after contract for a data script.
/// </summary>
public sealed record MetadataScriptTemporalContract
{
    /// <summary>Start-time field.</summary>
    [JsonPropertyName("startTimeField")]
    public string? StartTimeField { get; init; }

    /// <summary>End-time field.</summary>
    [JsonPropertyName("endTimeField")]
    public string? EndTimeField { get; init; }

    /// <summary>Track identifier field.</summary>
    [JsonPropertyName("trackIdField")]
    public string? TrackIdField { get; init; }
}

/// <summary>
/// Storage before or after contract for a data script.
/// </summary>
public sealed record MetadataScriptStorageContract
{
    /// <summary>Storage binding semantic identifier expected before or after the script.</summary>
    [JsonPropertyName("storageBindingId")]
    public string? StorageBindingId { get; init; }

    /// <summary>Storage type.</summary>
    [JsonPropertyName("storageType")]
    public MetadataV2StorageType? StorageType { get; init; }

    /// <summary>Storage binding capabilities.</summary>
    [JsonPropertyName("capabilities")]
    public IReadOnlyList<MetadataV2StorageBindingCapability> Capabilities { get; init; } =
        Array.Empty<MetadataV2StorageBindingCapability>();
}

/// <summary>
/// Overall compatibility status.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MetadataCompatibilityStatus>))]
public enum MetadataCompatibilityStatus
{
    /// <summary>All declared requirements are satisfied.</summary>
    [JsonStringEnumMemberName("ready")]
    Ready,

    /// <summary>No uncovered blockers exist, but warnings or script-covered blockers remain.</summary>
    [JsonStringEnumMemberName("warning")]
    Warning,

    /// <summary>At least one error finding is not covered by a declared data script.</summary>
    [JsonStringEnumMemberName("blocked")]
    Blocked,

    /// <summary>Required source, target, or comparison metadata is unavailable.</summary>
    [JsonStringEnumMemberName("unknown")]
    Unknown,
}

/// <summary>
/// Finding severity.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MetadataCompatibilityFindingSeverity>))]
public enum MetadataCompatibilityFindingSeverity
{
    /// <summary>Informational finding.</summary>
    [JsonStringEnumMemberName("info")]
    Info,

    /// <summary>Warning finding.</summary>
    [JsonStringEnumMemberName("warning")]
    Warning,

    /// <summary>Error finding.</summary>
    [JsonStringEnumMemberName("error")]
    Error,
}

/// <summary>
/// Finding kind.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MetadataCompatibilityFindingKind>))]
public enum MetadataCompatibilityFindingKind
{
    /// <summary>Source or target state availability.</summary>
    [JsonStringEnumMemberName("state")]
    State,

    /// <summary>Resource compatibility.</summary>
    [JsonStringEnumMemberName("resource")]
    Resource,

    /// <summary>Field compatibility.</summary>
    [JsonStringEnumMemberName("field")]
    Field,

    /// <summary>Identifier compatibility.</summary>
    [JsonStringEnumMemberName("identifier")]
    Identifier,

    /// <summary>Spatial metadata compatibility.</summary>
    [JsonStringEnumMemberName("spatial")]
    Spatial,

    /// <summary>Temporal metadata compatibility.</summary>
    [JsonStringEnumMemberName("temporal")]
    Temporal,

    /// <summary>Storage binding compatibility.</summary>
    [JsonStringEnumMemberName("storage")]
    Storage,

    /// <summary>Service compatibility.</summary>
    [JsonStringEnumMemberName("service")]
    Service,

    /// <summary>Publication compatibility.</summary>
    [JsonStringEnumMemberName("publication")]
    Publication,

    /// <summary>Projection profile compatibility.</summary>
    [JsonStringEnumMemberName("projection")]
    Projection,

    /// <summary>Policy or role reference compatibility.</summary>
    [JsonStringEnumMemberName("policy")]
    Policy,

    /// <summary>Data script applicability or coverage.</summary>
    [JsonStringEnumMemberName("script")]
    Script,
}

/// <summary>
/// Data script coverage state for a finding.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MetadataCompatibilityCoverageState>))]
public enum MetadataCompatibilityCoverageState
{
    /// <summary>The finding does not need data-script coverage.</summary>
    [JsonStringEnumMemberName("not-applicable")]
    NotApplicable,

    /// <summary>The finding is not covered by any applicable script.</summary>
    [JsonStringEnumMemberName("uncovered")]
    Uncovered,

    /// <summary>The finding is covered by a declared script.</summary>
    [JsonStringEnumMemberName("covered-by-script")]
    CoveredByScript,

    /// <summary>Coverage could not be established from declared metadata.</summary>
    [JsonStringEnumMemberName("unknown")]
    Unknown,
}

/// <summary>
/// Required action for a compatibility finding.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MetadataCompatibilityRequiredAction>))]
public enum MetadataCompatibilityRequiredAction
{
    /// <summary>No action is required.</summary>
    [JsonStringEnumMemberName("none")]
    None,

    /// <summary>Resolve source or target Metadata v2 state.</summary>
    [JsonStringEnumMemberName("resolve-state")]
    ResolveState,

    /// <summary>Add or restore a Metadata v2 resource.</summary>
    [JsonStringEnumMemberName("add-resource")]
    AddResource,

    /// <summary>Update a Metadata v2 resource.</summary>
    [JsonStringEnumMemberName("update-resource")]
    UpdateResource,

    /// <summary>Add a missing field.</summary>
    [JsonStringEnumMemberName("add-field")]
    AddField,

    /// <summary>Update field metadata.</summary>
    [JsonStringEnumMemberName("update-field")]
    UpdateField,

    /// <summary>Update identifier metadata.</summary>
    [JsonStringEnumMemberName("update-identifier")]
    UpdateIdentifier,

    /// <summary>Update spatial metadata.</summary>
    [JsonStringEnumMemberName("update-spatial")]
    UpdateSpatial,

    /// <summary>Update temporal metadata.</summary>
    [JsonStringEnumMemberName("update-temporal")]
    UpdateTemporal,

    /// <summary>Update storage binding metadata.</summary>
    [JsonStringEnumMemberName("update-storage")]
    UpdateStorage,

    /// <summary>Add or restore a service.</summary>
    [JsonStringEnumMemberName("add-service")]
    AddService,

    /// <summary>Update service metadata.</summary>
    [JsonStringEnumMemberName("update-service")]
    UpdateService,

    /// <summary>Add or restore a publication.</summary>
    [JsonStringEnumMemberName("add-publication")]
    AddPublication,

    /// <summary>Update publication metadata.</summary>
    [JsonStringEnumMemberName("update-publication")]
    UpdatePublication,

    /// <summary>Run and review the covering data script.</summary>
    [JsonStringEnumMemberName("run-data-script")]
    RunDataScript,

    /// <summary>Review metadata manually.</summary>
    [JsonStringEnumMemberName("manual-review")]
    ManualReview,
}

/// <summary>
/// Rollback readiness classification.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<MetadataRollbackReadinessClassification>))]
public enum MetadataRollbackReadinessClassification
{
    /// <summary>Only Metadata v2 graph/package state needs rollback.</summary>
    [JsonStringEnumMemberName("metadata-only")]
    MetadataOnly,

    /// <summary>Service or publication revisions are needed for rollback.</summary>
    [JsonStringEnumMemberName("service-revision")]
    ServiceRevision,

    /// <summary>All data-impacting changes are covered by reversible scripts.</summary>
    [JsonStringEnumMemberName("script-reversible")]
    ScriptReversible,

    /// <summary>Rollback requires an external data snapshot.</summary>
    [JsonStringEnumMemberName("snapshot-required")]
    SnapshotRequired,

    /// <summary>Rollback readiness cannot be automated from declared metadata.</summary>
    [JsonStringEnumMemberName("manual")]
    Manual,
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Studio.Domain;

/// <summary>
/// Shared package envelope for every Studio-authored artifact family.
/// </summary>
public sealed record StudioPackageEnvelope
{
    /// <summary>Package family.</summary>
    [JsonPropertyName("family")]
    public required StudioPackageFamily Family { get; init; }

    /// <summary>Schema version for the family envelope.</summary>
    [JsonPropertyName("schemaVersion")]
    public required string SchemaVersion { get; init; }

    /// <summary>Family-specific package format advertised by package-family capabilities.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>Data, service, content, field, CRS, unit, or permission bindings.</summary>
    [JsonPropertyName("bindings")]
    public IReadOnlyList<StudioPackageBinding> Bindings { get; init; } = Array.Empty<StudioPackageBinding>();

    /// <summary>Lineage, runtime, item, version, job, or artifact dependencies.</summary>
    [JsonPropertyName("dependencies")]
    public IReadOnlyList<StudioPackageDependency> Dependencies { get; init; } = Array.Empty<StudioPackageDependency>();

    /// <summary>Last validation summary known for the package envelope.</summary>
    [JsonPropertyName("validation")]
    public StudioValidationSummary Validation { get; init; } = StudioValidationSummary.NotValidated;

    /// <summary>Publication route, visibility, embed, service, schedule, or job intent.</summary>
    [JsonPropertyName("publicationIntent")]
    public StudioPublicationIntent? PublicationIntent { get; init; }

    /// <summary>Prompt, tool, source, audit, or generated-artifact provenance references.</summary>
    [JsonPropertyName("provenance")]
    public IReadOnlyList<StudioProvenanceRef> Provenance { get; init; } = Array.Empty<StudioProvenanceRef>();

    /// <summary>Family-specific JSON payload.</summary>
    [JsonPropertyName("body")]
    public JsonElement? Body { get; init; }
}

/// <summary>
/// One binding declared by a Studio package.
/// </summary>
public sealed record StudioPackageBinding
{
    /// <summary>Stable binding key within the package.</summary>
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    /// <summary>Binding kind, such as data-source, content-item, layer, field, route, or permission.</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>Referenced item, resource, field, route, or permission identifier.</summary>
    [JsonPropertyName("ref")]
    public required string Ref { get; init; }

    /// <summary>Optional CRS identifier, such as EPSG:4326.</summary>
    [JsonPropertyName("crs")]
    public string? Crs { get; init; }

    /// <summary>Optional integer spatial reference identifier.</summary>
    [JsonPropertyName("srid")]
    public int? Srid { get; init; }

    /// <summary>Optional units associated with this binding.</summary>
    [JsonPropertyName("units")]
    public string? Units { get; init; }

    /// <summary>Permissions required to use this binding.</summary>
    [JsonPropertyName("requiredPermissions")]
    public IReadOnlyList<string> RequiredPermissions { get; init; } = Array.Empty<string>();

    /// <summary>Additional binding metadata.</summary>
    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; init; }
}

/// <summary>
/// One dependency declared by a Studio package.
/// </summary>
public sealed record StudioPackageDependency
{
    /// <summary>Referenced dependency identifier.</summary>
    [JsonPropertyName("ref")]
    public required string Ref { get; init; }

    /// <summary>Dependency kind, such as content-item, content-version, service, job, or artifact.</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>Optional immutable dependency version identifier.</summary>
    [JsonPropertyName("versionId")]
    public string? VersionId { get; init; }

    /// <summary>True when the dependency is required at runtime.</summary>
    [JsonPropertyName("required")]
    public bool Required { get; init; } = true;

    /// <summary>Additional dependency metadata.</summary>
    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; init; }
}

/// <summary>
/// Validation summary attached to drafts, versions, and publication requests.
/// </summary>
public sealed record StudioValidationSummary
{
    /// <summary>Shared not-validated summary.</summary>
    public static StudioValidationSummary NotValidated { get; } = new()
    {
        Status = StudioPackageValidationStatus.NotValidated,
        GeneratedAt = null,
    };

    /// <summary>Validation status.</summary>
    [JsonPropertyName("status")]
    public required StudioPackageValidationStatus Status { get; init; }

    /// <summary>Validation diagnostics.</summary>
    [JsonPropertyName("diagnostics")]
    public IReadOnlyList<StudioValidationDiagnostic> Diagnostics { get; init; } = Array.Empty<StudioValidationDiagnostic>();

    /// <summary>Capabilities that are unsupported or limited for the package.</summary>
    [JsonPropertyName("unsupportedCapabilities")]
    public IReadOnlyList<string> UnsupportedCapabilities { get; init; } = Array.Empty<string>();

    /// <summary>Timestamp when the summary was generated.</summary>
    [JsonPropertyName("generatedAt")]
    public DateTimeOffset? GeneratedAt { get; init; }
}

/// <summary>
/// One validation diagnostic produced for a package envelope.
/// </summary>
public sealed record StudioValidationDiagnostic
{
    /// <summary>Machine-friendly diagnostic code.</summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    /// <summary>Diagnostic severity.</summary>
    [JsonPropertyName("severity")]
    public required StudioPackageDiagnosticSeverity Severity { get; init; }

    /// <summary>JSON pointer or field path associated with the diagnostic.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>Human-readable diagnostic message.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

/// <summary>
/// Publication intent declared by a Studio package.
/// </summary>
public sealed record StudioPublicationIntent
{
    /// <summary>Optional target route key.</summary>
    [JsonPropertyName("route")]
    public string? Route { get; init; }

    /// <summary>Optional visibility target.</summary>
    [JsonPropertyName("visibility")]
    public string? Visibility { get; init; }

    /// <summary>True when embedding should be enabled for the published package.</summary>
    [JsonPropertyName("embed")]
    public bool? Embed { get; init; }

    /// <summary>Optional service publication hint.</summary>
    [JsonPropertyName("service")]
    public string? Service { get; init; }

    /// <summary>Optional schedule expression or key.</summary>
    [JsonPropertyName("schedule")]
    public string? Schedule { get; init; }

    /// <summary>Optional job publication hint.</summary>
    [JsonPropertyName("job")]
    public string? Job { get; init; }

    /// <summary>Additional publication metadata.</summary>
    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; init; }
}

/// <summary>
/// Provenance reference attached to a Studio package.
/// </summary>
public sealed record StudioProvenanceRef
{
    /// <summary>Provenance kind, such as prompt, tool, source, audit, or generated-by.</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>Referenced provenance identifier.</summary>
    [JsonPropertyName("ref")]
    public required string Ref { get; init; }

    /// <summary>Relationship to the package.</summary>
    [JsonPropertyName("rel")]
    public required string Rel { get; init; }

    /// <summary>Actor associated with the provenance reference.</summary>
    [JsonPropertyName("actorId")]
    public string? ActorId { get; init; }

    /// <summary>Timestamp associated with the provenance reference.</summary>
    [JsonPropertyName("timestamp")]
    public DateTimeOffset? Timestamp { get; init; }
}

/// <summary>
/// Capability descriptor for one Studio package family.
/// </summary>
public sealed record StudioPackageFamilyDescriptor
{
    /// <summary>Package family.</summary>
    [JsonPropertyName("family")]
    public required StudioPackageFamily Family { get; init; }

    /// <summary>Current schema version for the family.</summary>
    [JsonPropertyName("currentSchemaVersion")]
    public required string CurrentSchemaVersion { get; init; }

    /// <summary>Canonical family-specific format identifier.</summary>
    [JsonPropertyName("format")]
    public required string Format { get; init; }

    /// <summary>Support level in the current deployment context.</summary>
    [JsonPropertyName("supportLevel")]
    public required StudioPackageSupportLevel SupportLevel { get; init; }

    /// <summary>Operations supported by this family.</summary>
    [JsonPropertyName("supportedOperations")]
    public IReadOnlyList<StudioPackageOperation> SupportedOperations { get; init; } = Array.Empty<StudioPackageOperation>();

    /// <summary>Validation depth advertised for this family.</summary>
    [JsonPropertyName("validationDepth")]
    public required string ValidationDepth { get; init; }

    /// <summary>Limitations that clients should surface as disabled or limited states.</summary>
    [JsonPropertyName("limitations")]
    public IReadOnlyList<string> Limitations { get; init; } = Array.Empty<string>();

    /// <summary>Maximum serialized package size in bytes.</summary>
    [JsonPropertyName("maxPackageBytes")]
    public int MaxPackageBytes { get; init; }

    /// <summary>True when preview planning is available.</summary>
    [JsonPropertyName("previewSupported")]
    public bool PreviewSupported { get; init; }

    /// <summary>True when publication requests are available.</summary>
    [JsonPropertyName("publishSupported")]
    public bool PublishSupported { get; init; }
}

/// <summary>
/// Response payload for package family capability discovery.
/// </summary>
public sealed record StudioPackageFamilyCapabilities
{
    /// <summary>Persistence mode backing package lifecycle operations.</summary>
    [JsonPropertyName("persistenceMode")]
    public required StudioPackagePersistenceMode PersistenceMode { get; init; }

    /// <summary>True when the backing store is durable.</summary>
    [JsonPropertyName("durable")]
    public required bool Durable { get; init; }

    /// <summary>Package families, including unsupported or limited families.</summary>
    [JsonPropertyName("families")]
    public IReadOnlyList<StudioPackageFamilyDescriptor> Families { get; init; } = Array.Empty<StudioPackageFamilyDescriptor>();
}

/// <summary>
/// Mutable Studio package draft.
/// </summary>
public sealed record StudioPackageDraft
{
    /// <summary>Draft identifier.</summary>
    [JsonPropertyName("draftId")]
    public required Guid DraftId { get; init; }

    /// <summary>Content item identifier owned by the Studio lifecycle.</summary>
    [JsonPropertyName("itemId")]
    public required Guid ItemId { get; init; }

    /// <summary>Machine-friendly package key.</summary>
    [JsonPropertyName("packageKey")]
    public required string PackageKey { get; init; }

    /// <summary>Workspace identifier.</summary>
    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; init; }

    /// <summary>Owner principal identifier.</summary>
    [JsonPropertyName("ownerId")]
    public string? OwnerId { get; init; }

    /// <summary>Package family.</summary>
    [JsonPropertyName("family")]
    public required StudioPackageFamily Family { get; init; }

    /// <summary>Package envelope.</summary>
    [JsonPropertyName("envelope")]
    public required StudioPackageEnvelope Envelope { get; init; }

    /// <summary>Last validation summary known for the draft.</summary>
    [JsonPropertyName("validation")]
    public StudioValidationSummary Validation { get; init; } = StudioValidationSummary.NotValidated;

    /// <summary>Version this draft was reopened from, when applicable.</summary>
    [JsonPropertyName("baseVersionId")]
    public Guid? BaseVersionId { get; init; }

    /// <summary>Optimistic concurrency generation.</summary>
    [JsonPropertyName("generation")]
    public long Generation { get; init; }

    /// <summary>Identifier of the actor that created the draft.</summary>
    [JsonPropertyName("createdBy")]
    public string? CreatedBy { get; init; }

    /// <summary>Identifier of the actor that last updated the draft.</summary>
    [JsonPropertyName("updatedBy")]
    public string? UpdatedBy { get; init; }

    /// <summary>Timestamp when the draft was created.</summary>
    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Timestamp when the draft was last updated.</summary>
    [JsonPropertyName("updatedAt")]
    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Immutable Studio content version.
/// </summary>
public sealed record StudioContentVersion
{
    /// <summary>Content item identifier.</summary>
    [JsonPropertyName("itemId")]
    public required Guid ItemId { get; init; }

    /// <summary>Machine-friendly package key captured when the version was created.</summary>
    [JsonPropertyName("packageKey")]
    public required string PackageKey { get; init; }

    /// <summary>Workspace identifier captured when the version was created.</summary>
    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; init; }

    /// <summary>Owner principal identifier captured when the version was created.</summary>
    [JsonPropertyName("ownerId")]
    public string? OwnerId { get; init; }

    /// <summary>Version identifier.</summary>
    [JsonPropertyName("versionId")]
    public required Guid VersionId { get; init; }

    /// <summary>Monotonic version number within an item.</summary>
    [JsonPropertyName("versionNumber")]
    public required int VersionNumber { get; init; }

    /// <summary>SHA-256 hash of the immutable package envelope.</summary>
    [JsonPropertyName("contentHash")]
    public required string ContentHash { get; init; }

    /// <summary>Immutable package envelope.</summary>
    [JsonPropertyName("envelope")]
    public required StudioPackageEnvelope Envelope { get; init; }

    /// <summary>Validation summary captured at version creation.</summary>
    [JsonPropertyName("validation")]
    public required StudioValidationSummary Validation { get; init; }

    /// <summary>Dependency sidecars captured from the immutable package envelope.</summary>
    [JsonPropertyName("dependencies")]
    public IReadOnlyList<StudioPackageDependency> Dependencies { get; init; } = Array.Empty<StudioPackageDependency>();

    /// <summary>Provenance captured from the immutable package envelope.</summary>
    [JsonPropertyName("provenance")]
    public IReadOnlyList<StudioProvenanceRef> Provenance { get; init; } = Array.Empty<StudioProvenanceRef>();

    /// <summary>Source draft identifier.</summary>
    [JsonPropertyName("sourceDraftId")]
    public Guid? SourceDraftId { get; init; }

    /// <summary>Base version identifier when this version came from a reopened draft.</summary>
    [JsonPropertyName("baseVersionId")]
    public Guid? BaseVersionId { get; init; }

    /// <summary>Optional author change note.</summary>
    [JsonPropertyName("changeNote")]
    public string? ChangeNote { get; init; }

    /// <summary>Identifier of the actor that created the immutable version.</summary>
    [JsonPropertyName("createdBy")]
    public string? CreatedBy { get; init; }

    /// <summary>Timestamp when the immutable version was created.</summary>
    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Current and published version pointers for a Studio content item.
/// </summary>
public sealed record StudioContentItemPointers
{
    /// <summary>Content item identifier.</summary>
    [JsonPropertyName("itemId")]
    public required Guid ItemId { get; init; }

    /// <summary>Current version identifier.</summary>
    [JsonPropertyName("currentVersionId")]
    public Guid? CurrentVersionId { get; init; }

    /// <summary>Published version identifier.</summary>
    [JsonPropertyName("publishedVersionId")]
    public Guid? PublishedVersionId { get; init; }
}

/// <summary>
/// Durable publication request for an immutable Studio content version.
/// </summary>
public sealed record StudioPublicationRequest
{
    /// <summary>Publication request identifier.</summary>
    [JsonPropertyName("requestId")]
    public required Guid RequestId { get; init; }

    /// <summary>Content item identifier.</summary>
    [JsonPropertyName("itemId")]
    public required Guid ItemId { get; init; }

    /// <summary>Immutable version identifier requested for publication.</summary>
    [JsonPropertyName("versionId")]
    public required Guid VersionId { get; init; }

    /// <summary>Publication intent.</summary>
    [JsonPropertyName("intent")]
    public StudioPublicationIntent? Intent { get; init; }

    /// <summary>Persisted request status.</summary>
    [JsonPropertyName("status")]
    public required StudioPublicationRequestStatus Status { get; init; }

    /// <summary>Validation evidence captured for the request.</summary>
    [JsonPropertyName("validation")]
    public StudioValidationSummary Validation { get; init; } = StudioValidationSummary.NotValidated;

    /// <summary>Optional acknowledgement text for validation warnings.</summary>
    [JsonPropertyName("warningAcknowledgement")]
    public string? WarningAcknowledgement { get; init; }

    /// <summary>Identifier of the actor that requested publication.</summary>
    [JsonPropertyName("requestedBy")]
    public string? RequestedBy { get; init; }

    /// <summary>Timestamp when the request was created.</summary>
    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Result of a deterministic comparison between two immutable content versions.
/// </summary>
public sealed record StudioVersionComparison
{
    /// <summary>Source version identifier.</summary>
    [JsonPropertyName("leftVersionId")]
    public required Guid LeftVersionId { get; init; }

    /// <summary>Target version identifier.</summary>
    [JsonPropertyName("rightVersionId")]
    public required Guid RightVersionId { get; init; }

    /// <summary>True when the version envelope hashes match.</summary>
    [JsonPropertyName("contentEqual")]
    public required bool ContentEqual { get; init; }

    /// <summary>True when dependency sets match.</summary>
    [JsonPropertyName("dependenciesEqual")]
    public required bool DependenciesEqual { get; init; }

    /// <summary>True when validation status and diagnostics match.</summary>
    [JsonPropertyName("validationEqual")]
    public required bool ValidationEqual { get; init; }

    /// <summary>True when provenance sets match.</summary>
    [JsonPropertyName("provenanceEqual")]
    public required bool ProvenanceEqual { get; init; }

    /// <summary>Deterministic change labels for clients to render.</summary>
    [JsonPropertyName("changes")]
    public IReadOnlyList<string> Changes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Stable preview plan for a mutable Studio package draft.
/// </summary>
public sealed record StudioPreviewPlan
{
    /// <summary>Draft identifier.</summary>
    [JsonPropertyName("draftId")]
    public required Guid DraftId { get; init; }

    /// <summary>Package family.</summary>
    [JsonPropertyName("family")]
    public required StudioPackageFamily Family { get; init; }

    /// <summary>True when preview can run synchronously.</summary>
    [JsonPropertyName("synchronous")]
    public required bool Synchronous { get; init; }

    /// <summary>True when preview requires a background job.</summary>
    [JsonPropertyName("requiresJob")]
    public required bool RequiresJob { get; init; }

    /// <summary>Preview steps.</summary>
    [JsonPropertyName("steps")]
    public IReadOnlyList<string> Steps { get; init; } = Array.Empty<string>();

    /// <summary>Validation summary used for the preview plan.</summary>
    [JsonPropertyName("validation")]
    public required StudioValidationSummary Validation { get; init; }
}

/// <summary>
/// Durable rollback request and resulting pointer state.
/// </summary>
public sealed record StudioRollbackRequest
{
    /// <summary>Rollback request identifier.</summary>
    [JsonPropertyName("requestId")]
    public required Guid RequestId { get; init; }

    /// <summary>Content item identifier.</summary>
    [JsonPropertyName("itemId")]
    public required Guid ItemId { get; init; }

    /// <summary>Version identifier selected as the rollback target.</summary>
    [JsonPropertyName("targetVersionId")]
    public required Guid TargetVersionId { get; init; }

    /// <summary>Pointer updated by the rollback.</summary>
    [JsonPropertyName("pointer")]
    public required StudioRollbackPointer Target { get; init; }

    /// <summary>Resulting current/published pointers.</summary>
    [JsonPropertyName("pointers")]
    public required StudioContentItemPointers Pointers { get; init; }

    /// <summary>Identifier of the actor that requested rollback.</summary>
    [JsonPropertyName("requestedBy")]
    public string? RequestedBy { get; init; }

    /// <summary>Reason supplied by the actor.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>Timestamp when the request was created.</summary>
    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }
}

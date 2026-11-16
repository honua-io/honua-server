// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.MultiTenancy;

namespace Honua.Core.Features.Studio.Domain;

public sealed record StudioPackageEnvelope
{
    [JsonPropertyName("family")]
    public required StudioPackageFamily Family { get; init; }

    [JsonPropertyName("schemaVersion")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("format")]
    public string? Format { get; init; }

    [JsonPropertyName("bindings")]
    public IReadOnlyList<StudioPackageBinding> Bindings { get; init; } = Array.Empty<StudioPackageBinding>();

    [JsonPropertyName("dependencies")]
    public IReadOnlyList<StudioPackageDependency> Dependencies { get; init; } = Array.Empty<StudioPackageDependency>();

    [JsonPropertyName("validation")]
    public StudioValidationSummary Validation { get; init; } = StudioValidationSummary.NotValidated;

    [JsonPropertyName("publicationIntent")]
    public StudioPublicationIntent? PublicationIntent { get; init; }

    [JsonPropertyName("provenance")]
    public IReadOnlyList<StudioProvenanceRef> Provenance { get; init; } = Array.Empty<StudioProvenanceRef>();

    [JsonPropertyName("body")]
    public JsonElement? Body { get; init; }
}

public sealed record StudioPackageBinding
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("ref")]
    public required string Ref { get; init; }

    [JsonPropertyName("crs")]
    public string? Crs { get; init; }

    [JsonPropertyName("srid")]
    public int? Srid { get; init; }

    [JsonPropertyName("units")]
    public string? Units { get; init; }

    [JsonPropertyName("requiredPermissions")]
    public IReadOnlyList<string> RequiredPermissions { get; init; } = Array.Empty<string>();

    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; init; }
}

public sealed record StudioPackageDependency
{
    [JsonPropertyName("ref")]
    public required string Ref { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("versionId")]
    public string? VersionId { get; init; }

    [JsonPropertyName("required")]
    public bool Required { get; init; } = true;

    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; init; }
}

public sealed record StudioValidationSummary
{
    public static StudioValidationSummary NotValidated { get; } = new()
    {
        Status = StudioPackageValidationStatus.NotValidated,
        GeneratedAt = null,
    };

    [JsonPropertyName("status")]
    public required StudioPackageValidationStatus Status { get; init; }

    [JsonPropertyName("diagnostics")]
    public IReadOnlyList<StudioValidationDiagnostic> Diagnostics { get; init; } = Array.Empty<StudioValidationDiagnostic>();

    [JsonPropertyName("unsupportedCapabilities")]
    public IReadOnlyList<string> UnsupportedCapabilities { get; init; } = Array.Empty<string>();

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset? GeneratedAt { get; init; }
}

public sealed record StudioValidationDiagnostic
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("severity")]
    public required StudioPackageDiagnosticSeverity Severity { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

public sealed record StudioPublicationIntent
{
    [JsonPropertyName("route")]
    public string? Route { get; init; }

    [JsonPropertyName("visibility")]
    public string? Visibility { get; init; }

    [JsonPropertyName("embed")]
    public bool? Embed { get; init; }

    [JsonPropertyName("service")]
    public string? Service { get; init; }

    [JsonPropertyName("schedule")]
    public string? Schedule { get; init; }

    [JsonPropertyName("job")]
    public string? Job { get; init; }

    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; init; }
}

public sealed record StudioProvenanceRef
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("ref")]
    public required string Ref { get; init; }

    [JsonPropertyName("rel")]
    public required string Rel { get; init; }

    [JsonPropertyName("actorId")]
    public string? ActorId { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset? Timestamp { get; init; }
}

public sealed record StudioPackageFamilyDescriptor
{
    [JsonPropertyName("family")]
    public required StudioPackageFamily Family { get; init; }

    [JsonPropertyName("currentSchemaVersion")]
    public required string CurrentSchemaVersion { get; init; }

    [JsonPropertyName("format")]
    public required string Format { get; init; }

    [JsonPropertyName("supportLevel")]
    public required StudioPackageSupportLevel SupportLevel { get; init; }

    [JsonPropertyName("supportedOperations")]
    public IReadOnlyList<StudioPackageOperation> SupportedOperations { get; init; } = Array.Empty<StudioPackageOperation>();

    [JsonPropertyName("validationDepth")]
    public required string ValidationDepth { get; init; }

    [JsonPropertyName("limitations")]
    public IReadOnlyList<string> Limitations { get; init; } = Array.Empty<string>();

    [JsonPropertyName("maxPackageBytes")]
    public int MaxPackageBytes { get; init; }

    [JsonPropertyName("previewSupported")]
    public bool PreviewSupported { get; init; }

    [JsonPropertyName("publishSupported")]
    public bool PublishSupported { get; init; }

    [JsonIgnore]
    public bool GovernsStudioRoutes { get; init; } = true;
}

public sealed record StudioPackageFamilyCapabilities
{
    [JsonPropertyName("persistenceMode")]
    public required StudioPackagePersistenceMode PersistenceMode { get; init; }

    [JsonPropertyName("durable")]
    public required bool Durable { get; init; }

    [JsonPropertyName("families")]
    public IReadOnlyList<StudioPackageFamilyDescriptor> Families { get; init; } = Array.Empty<StudioPackageFamilyDescriptor>();
}

public sealed record StudioPackageDraft
{
    [JsonPropertyName("draftId")]
    public required Guid DraftId { get; init; }

    [JsonPropertyName("itemId")]
    public required Guid ItemId { get; init; }

    [JsonPropertyName("packageKey")]
    public required string PackageKey { get; init; }

    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("ownerId")]
    public string? OwnerId { get; init; }

    [JsonIgnore]
    public string? TenantId { get; init; }

    [JsonIgnore]
    public string? ExpectedExistingItemOwnerId { get; init; }

    [JsonIgnore]
    public bool ExpectedExistingItemPresent { get; init; }

    [JsonPropertyName("family")]
    public required StudioPackageFamily Family { get; init; }

    [JsonPropertyName("envelope")]
    public required StudioPackageEnvelope Envelope { get; init; }

    [JsonPropertyName("validation")]
    public StudioValidationSummary Validation { get; init; } = StudioValidationSummary.NotValidated;

    [JsonPropertyName("baseVersionId")]
    public Guid? BaseVersionId { get; init; }

    [JsonPropertyName("generation")]
    public long Generation { get; init; }

    [JsonPropertyName("createdBy")]
    public string? CreatedBy { get; init; }

    [JsonPropertyName("updatedBy")]
    public string? UpdatedBy { get; init; }

    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record StudioContentVersion
{
    [JsonPropertyName("itemId")]
    public required Guid ItemId { get; init; }

    [JsonPropertyName("packageKey")]
    public required string PackageKey { get; init; }

    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("ownerId")]
    public string? OwnerId { get; init; }

    [JsonIgnore]
    public string? TenantId { get; init; }

    [JsonPropertyName("versionId")]
    public required Guid VersionId { get; init; }

    [JsonPropertyName("versionNumber")]
    public required int VersionNumber { get; init; }

    [JsonPropertyName("contentHash")]
    public required string ContentHash { get; init; }

    [JsonPropertyName("contentHashInput")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContentHashInput { get; init; }

    [JsonPropertyName("envelope")]
    public required StudioPackageEnvelope Envelope { get; init; }

    [JsonPropertyName("validation")]
    public required StudioValidationSummary Validation { get; init; }

    [JsonPropertyName("dependencies")]
    public IReadOnlyList<StudioPackageDependency> Dependencies { get; init; } = Array.Empty<StudioPackageDependency>();

    [JsonPropertyName("provenance")]
    public IReadOnlyList<StudioProvenanceRef> Provenance { get; init; } = Array.Empty<StudioProvenanceRef>();

    [JsonPropertyName("sourceDraftId")]
    public Guid? SourceDraftId { get; init; }

    [JsonPropertyName("baseVersionId")]
    public Guid? BaseVersionId { get; init; }

    [JsonPropertyName("changeNote")]
    public string? ChangeNote { get; init; }

    [JsonPropertyName("createdBy")]
    public string? CreatedBy { get; init; }

    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record StudioVersionCheckpoint
{
    public required string MapId { get; init; }

    public required long OperationCursor { get; init; }
}

public sealed record StudioCheckpointedVersion
{
    public required StudioContentVersion Version { get; init; }

    public required StudioVersionCheckpoint Checkpoint { get; init; }
}

public sealed record StudioContentItemPointers
{
    [JsonPropertyName("itemId")]
    public required Guid ItemId { get; init; }

    [JsonPropertyName("currentVersionId")]
    public Guid? CurrentVersionId { get; init; }

    [JsonPropertyName("publishedVersionId")]
    public Guid? PublishedVersionId { get; init; }

    [JsonPropertyName("ownerId")]
    public string? OwnerId { get; init; }

    [JsonIgnore]
    public string? TenantId { get; init; }

    [JsonIgnore]
    public bool GovernsStudioRoutes { get; init; } = true;
}

public sealed record StudioPublicationRequest
{
    [JsonPropertyName("requestId")]
    public required Guid RequestId { get; init; }

    [JsonPropertyName("deploymentId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeploymentId { get; init; }

    [JsonPropertyName("deploymentResourceUri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeploymentResourceUri => DeploymentId is { Length: > 0 }
        ? $"honua://deployments/{Uri.EscapeDataString(DeploymentId)}"
        : null;

    [JsonPropertyName("activeDeploymentId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActiveDeploymentId { get; init; }

    [JsonPropertyName("activeDeploymentResourceUri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActiveDeploymentResourceUri => ActiveDeploymentId is { Length: > 0 }
        ? $"honua://deployments/{Uri.EscapeDataString(ActiveDeploymentId)}"
        : null;

    [JsonPropertyName("itemId")]
    public required Guid ItemId { get; init; }

    [JsonPropertyName("versionId")]
    public required Guid VersionId { get; init; }

    [JsonPropertyName("intent")]
    public StudioPublicationIntent? Intent { get; init; }

    [JsonPropertyName("status")]
    public required StudioPublicationRequestStatus Status { get; init; }

    [JsonPropertyName("validation")]
    public StudioValidationSummary Validation { get; init; } = StudioValidationSummary.NotValidated;

    [JsonPropertyName("warningAcknowledgement")]
    public string? WarningAcknowledgement { get; init; }

    [JsonPropertyName("requestedBy")]
    public string? RequestedBy { get; init; }

    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }

    [JsonIgnore]
    public bool GovernsStudioRoutes { get; init; } = true;

    [JsonPropertyName("publicationUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PublicationUrl
        => GovernsStudioRoutes
            && Status == StudioPublicationRequestStatus.Accepted
            && !string.IsNullOrWhiteSpace(Intent?.Route)
            ? StudioPublishedRoutes.BuildActiveUrl(Intent.Route)
            : null;
}

public sealed record StudioVersionComparison
{
    [JsonPropertyName("leftVersionId")]
    public required Guid LeftVersionId { get; init; }

    [JsonPropertyName("rightVersionId")]
    public required Guid RightVersionId { get; init; }

    [JsonPropertyName("contentEqual")]
    public required bool ContentEqual { get; init; }

    [JsonPropertyName("dependenciesEqual")]
    public required bool DependenciesEqual { get; init; }

    [JsonPropertyName("validationEqual")]
    public required bool ValidationEqual { get; init; }

    [JsonPropertyName("provenanceEqual")]
    public required bool ProvenanceEqual { get; init; }

    [JsonPropertyName("changes")]
    public IReadOnlyList<string> Changes { get; init; } = Array.Empty<string>();
}

public sealed record StudioPreviewPlan
{
    [JsonPropertyName("draftId")]
    public required Guid DraftId { get; init; }

    [JsonPropertyName("family")]
    public required StudioPackageFamily Family { get; init; }

    [JsonPropertyName("synchronous")]
    public required bool Synchronous { get; init; }

    [JsonPropertyName("requiresJob")]
    public required bool RequiresJob { get; init; }

    [JsonPropertyName("steps")]
    public IReadOnlyList<string> Steps { get; init; } = Array.Empty<string>();

    [JsonPropertyName("validation")]
    public required StudioValidationSummary Validation { get; init; }
}

public sealed record StudioContentItemQuery
{
    public IReadOnlyList<StudioPackageFamily>? Families { get; init; }

    public string? WorkspaceId { get; init; }

    public string? OwnerId { get; init; }

    public TenantScopeFilter? Tenant { get; init; }

    public IReadOnlyList<StudioContentItemState>? States { get; init; }

    public string? SearchTerm { get; init; }

    public string? Cursor { get; init; }

    public int Limit { get; init; } = 25;
}

public sealed record StudioContentItemSummary
{
    [JsonPropertyName("itemId")]
    public required Guid ItemId { get; init; }

    [JsonPropertyName("packageKey")]
    public required string PackageKey { get; init; }

    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("family")]
    public required StudioPackageFamily Family { get; init; }

    [JsonPropertyName("state")]
    public required StudioContentItemState State { get; init; }

    [JsonPropertyName("currentVersionId")]
    public Guid? CurrentVersionId { get; init; }

    [JsonPropertyName("publishedVersionId")]
    public Guid? PublishedVersionId { get; init; }

    [JsonPropertyName("ownerId")]
    public string? OwnerId { get; init; }

    // Owning tenant, used to apply the caller's tenant scope to bridged families; never serialized.
    [JsonIgnore]
    public string? TenantId { get; init; }

    [JsonPropertyName("createdBy")]
    public string? CreatedBy { get; init; }

    [JsonPropertyName("updatedBy")]
    public string? UpdatedBy { get; init; }

    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record StudioContentItemListResult
{
    public IReadOnlyList<StudioContentItemSummary> Items { get; init; } = Array.Empty<StudioContentItemSummary>();

    public int Total { get; init; }

    public string? NextCursor { get; init; }
}

public sealed record StudioPackageDraftQuery
{
    public IReadOnlyList<StudioPackageFamily>? Families { get; init; }

    public string? WorkspaceId { get; init; }

    public string? OwnerId { get; init; }

    public TenantScopeFilter? Tenant { get; init; }

    public string? SearchTerm { get; init; }

    public string? Cursor { get; init; }

    public int Limit { get; init; } = 25;
}

public sealed record StudioPackageDraftListResult
{
    public IReadOnlyList<StudioPackageDraft> Items { get; init; } = Array.Empty<StudioPackageDraft>();

    public int Total { get; init; }

    public string? NextCursor { get; init; }
}

public sealed record StudioRollbackRequest
{
    [JsonPropertyName("requestId")]
    public required Guid RequestId { get; init; }

    [JsonPropertyName("deploymentId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeploymentId { get; init; }

    [JsonPropertyName("deploymentResourceUri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeploymentResourceUri => DeploymentId is { Length: > 0 }
        ? $"honua://deployments/{Uri.EscapeDataString(DeploymentId)}"
        : null;

    [JsonPropertyName("itemId")]
    public required Guid ItemId { get; init; }

    [JsonPropertyName("targetVersionId")]
    public required Guid TargetVersionId { get; init; }

    [JsonPropertyName("pointer")]
    public required StudioRollbackPointer Target { get; init; }

    [JsonPropertyName("pointers")]
    public required StudioContentItemPointers Pointers { get; init; }

    [JsonPropertyName("requestedBy")]
    public string? RequestedBy { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }
}

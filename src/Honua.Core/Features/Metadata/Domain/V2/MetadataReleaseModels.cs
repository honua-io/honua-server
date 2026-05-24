// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Console.Domain;

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Current Metadata v2 revision metadata for one environment.
/// </summary>
public sealed record MetadataV2EnvironmentRevision
{
    /// <summary>Environment identifier.</summary>
    [JsonPropertyName("environment")]
    public required string Environment { get; init; }

    /// <summary>Active Metadata v2 revision.</summary>
    [JsonPropertyName("revision")]
    public required long Revision { get; init; }

    /// <summary>Active Metadata v2 entity tag.</summary>
    [JsonPropertyName("etag")]
    public required string ETag { get; init; }

    /// <summary>Timestamp when the revision was activated or observed.</summary>
    [JsonPropertyName("activatedAt")]
    public DateTimeOffset? ActivatedAt { get; init; }
}

/// <summary>
/// Filter used when projecting semantic inventory from an environment snapshot.
/// </summary>
public sealed record MetadataSemanticInventoryFilter
{
    /// <summary>Optional artifact kind filter.</summary>
    public MetadataSemanticArtifactKind? ArtifactKind { get; init; }

    /// <summary>Optional Metadata v2 resource type filter.</summary>
    public MetadataV2ResourceType? ResourceType { get; init; }
}

/// <summary>
/// Semantic resource inventory for an environment.
/// </summary>
public sealed record MetadataSemanticInventoryResponse
{
    /// <summary>Environment represented by the inventory.</summary>
    [JsonPropertyName("environment")]
    public required string Environment { get; init; }

    /// <summary>Active Metadata v2 revision used to project the inventory.</summary>
    [JsonPropertyName("revision")]
    public required long Revision { get; init; }

    /// <summary>Active Metadata v2 entity tag used to project the inventory.</summary>
    [JsonPropertyName("etag")]
    public required string ETag { get; init; }

    /// <summary>Timestamp when the inventory was generated.</summary>
    [JsonPropertyName("generatedAt")]
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Inventory entries.</summary>
    [JsonPropertyName("entries")]
    public IReadOnlyList<MetadataSemanticInventoryEntry> Entries { get; init; } =
        Array.Empty<MetadataSemanticInventoryEntry>();
}

/// <summary>
/// One semantic artifact in an environment inventory.
/// </summary>
public sealed record MetadataSemanticInventoryEntry
{
    /// <summary>Stable semantic identifier.</summary>
    [JsonPropertyName("semanticId")]
    public required string SemanticId { get; init; }

    /// <summary>Artifact kind.</summary>
    [JsonPropertyName("artifactKind")]
    public required MetadataSemanticArtifactKind ArtifactKind { get; init; }

    /// <summary>Resource type when the artifact is a resource or resource child.</summary>
    [JsonPropertyName("resourceType")]
    public MetadataV2ResourceType? ResourceType { get; init; }

    /// <summary>Service type when the artifact is a service.</summary>
    [JsonPropertyName("serviceType")]
    public MetadataV2ServiceType? ServiceType { get; init; }

    /// <summary>Publication type when the artifact is a publication.</summary>
    [JsonPropertyName("publicationType")]
    public MetadataV2PublicationType? PublicationType { get; init; }

    /// <summary>Parent semantic identifier for fields and publications.</summary>
    [JsonPropertyName("parentSemanticId")]
    public string? ParentSemanticId { get; init; }

    /// <summary>Machine-friendly name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Optional namespace.</summary>
    [JsonPropertyName("namespace")]
    public string? Namespace { get; init; }

    /// <summary>Human-readable title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Metadata generation for optimistic comparisons.</summary>
    [JsonPropertyName("metadataGeneration")]
    public long? MetadataGeneration { get; init; }

    /// <summary>Lifecycle and operational status reused from Metadata v2.</summary>
    [JsonPropertyName("status")]
    public MetadataV2Status? Status { get; init; }

    /// <summary>Desired or observed content-version reference.</summary>
    [JsonPropertyName("contentVersionId")]
    public string? ContentVersionId { get; init; }

    /// <summary>Provenance references reused from Console content metadata.</summary>
    [JsonPropertyName("provenance")]
    public IReadOnlyList<ConsoleProvenanceRef> Provenance { get; init; } = Array.Empty<ConsoleProvenanceRef>();

    /// <summary>Metadata v2 policy identifiers associated with the artifact.</summary>
    [JsonPropertyName("policyIds")]
    public IReadOnlyList<string> PolicyIds { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Request for environment binding summaries.
/// </summary>
public sealed record MetadataEnvironmentBindingsRequest
{
    /// <summary>Environments to inspect.</summary>
    [JsonPropertyName("environments")]
    public IReadOnlyList<string> Environments { get; init; } = Array.Empty<string>();

    /// <summary>Semantic identifiers to resolve in each environment.</summary>
    [JsonPropertyName("semanticIds")]
    public IReadOnlyList<string> SemanticIds { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Binding summaries for requested semantic artifacts across environments.
/// </summary>
public sealed record MetadataEnvironmentBindingsResponse
{
    /// <summary>Timestamp when bindings were requested.</summary>
    [JsonPropertyName("requestedAt")]
    public required DateTimeOffset RequestedAt { get; init; }

    /// <summary>Environment identifiers requested by the caller.</summary>
    [JsonPropertyName("environments")]
    public IReadOnlyList<string> Environments { get; init; } = Array.Empty<string>();

    /// <summary>Binding summaries in environment and semantic-id order.</summary>
    [JsonPropertyName("bindings")]
    public IReadOnlyList<MetadataEnvironmentBindingSummary> Bindings { get; init; } =
        Array.Empty<MetadataEnvironmentBindingSummary>();
}

/// <summary>
/// Secret-safe binding summary for one semantic artifact in one environment.
/// </summary>
public sealed record MetadataEnvironmentBindingSummary
{
    /// <summary>Semantic identifier requested by the caller.</summary>
    [JsonPropertyName("semanticId")]
    public required string SemanticId { get; init; }

    /// <summary>Environment inspected.</summary>
    [JsonPropertyName("environment")]
    public required string Environment { get; init; }

    /// <summary>Binding state.</summary>
    [JsonPropertyName("state")]
    public required MetadataEnvironmentBindingState State { get; init; }

    /// <summary>Metadata v2 revision observed in the environment.</summary>
    [JsonPropertyName("revision")]
    public long? Revision { get; init; }

    /// <summary>Metadata v2 entity tag observed in the environment.</summary>
    [JsonPropertyName("etag")]
    public string? ETag { get; init; }

    /// <summary>Artifact kind when resolved.</summary>
    [JsonPropertyName("artifactKind")]
    public MetadataSemanticArtifactKind? ArtifactKind { get; init; }

    /// <summary>Resource summary when applicable.</summary>
    [JsonPropertyName("resource")]
    public MetadataBoundResourceSummary? Resource { get; init; }

    /// <summary>Field summary when applicable.</summary>
    [JsonPropertyName("field")]
    public MetadataBoundFieldSummary? Field { get; init; }

    /// <summary>Service summary when applicable.</summary>
    [JsonPropertyName("service")]
    public MetadataBoundServiceSummary? Service { get; init; }

    /// <summary>Publication summary when applicable.</summary>
    [JsonPropertyName("publication")]
    public MetadataBoundPublicationSummary? Publication { get; init; }

    /// <summary>Storage binding summary when applicable.</summary>
    [JsonPropertyName("storage")]
    public MetadataBoundStorageSummary? Storage { get; init; }

    /// <summary>Connection reference summary when applicable.</summary>
    [JsonPropertyName("connection")]
    public MetadataBoundConnectionSummary? Connection { get; init; }

    /// <summary>Content-version reference observed for the artifact.</summary>
    [JsonPropertyName("contentVersionId")]
    public string? ContentVersionId { get; init; }

    /// <summary>Timestamp when the binding snapshot was observed.</summary>
    [JsonPropertyName("lastObservedAt")]
    public DateTimeOffset? LastObservedAt { get; init; }
}

/// <summary>
/// Secret-safe resource binding summary.
/// </summary>
public sealed record MetadataBoundResourceSummary
{
    /// <summary>Resource semantic identifier.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Resource name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Optional namespace.</summary>
    [JsonPropertyName("namespace")]
    public string? Namespace { get; init; }

    /// <summary>Resource title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Resource type.</summary>
    [JsonPropertyName("resourceType")]
    public required MetadataV2ResourceType ResourceType { get; init; }

    /// <summary>Primary storage binding identifier.</summary>
    [JsonPropertyName("primaryStorageBindingId")]
    public string? PrimaryStorageBindingId { get; init; }
}

/// <summary>
/// Secret-safe field binding summary.
/// </summary>
public sealed record MetadataBoundFieldSummary
{
    /// <summary>Field semantic identifier.</summary>
    [JsonPropertyName("semanticId")]
    public required string SemanticId { get; init; }

    /// <summary>Parent resource semantic identifier.</summary>
    [JsonPropertyName("parentResourceId")]
    public required string ParentResourceId { get; init; }

    /// <summary>Field name.</summary>
    [JsonPropertyName("fieldName")]
    public required string FieldName { get; init; }

    /// <summary>Field type.</summary>
    [JsonPropertyName("fieldType")]
    public string? FieldType { get; init; }
}

/// <summary>
/// Secret-safe service binding summary.
/// </summary>
public sealed record MetadataBoundServiceSummary
{
    /// <summary>Service semantic identifier.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Service name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Optional namespace.</summary>
    [JsonPropertyName("namespace")]
    public string? Namespace { get; init; }

    /// <summary>Service title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Service type.</summary>
    [JsonPropertyName("serviceType")]
    public required MetadataV2ServiceType ServiceType { get; init; }

    /// <summary>Service route or base path.</summary>
    [JsonPropertyName("route")]
    public string? Route { get; init; }
}

/// <summary>
/// Secret-safe publication binding summary.
/// </summary>
public sealed record MetadataBoundPublicationSummary
{
    /// <summary>Publication semantic identifier.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Resource semantic identifier.</summary>
    [JsonPropertyName("resourceId")]
    public required string ResourceId { get; init; }

    /// <summary>Service semantic identifier.</summary>
    [JsonPropertyName("serviceId")]
    public required string ServiceId { get; init; }

    /// <summary>Publication type.</summary>
    [JsonPropertyName("publicationType")]
    public required MetadataV2PublicationType PublicationType { get; init; }

    /// <summary>Service-local path.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>Service-local layer index.</summary>
    [JsonPropertyName("layerIndex")]
    public int? LayerIndex { get; init; }

    /// <summary>Service-local identifier.</summary>
    [JsonPropertyName("serviceLocalId")]
    public string? ServiceLocalId { get; init; }
}

/// <summary>
/// Secret-safe storage binding summary.
/// </summary>
public sealed record MetadataBoundStorageSummary
{
    /// <summary>Storage binding semantic identifier.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Resource semantic identifier.</summary>
    [JsonPropertyName("resourceId")]
    public required string ResourceId { get; init; }

    /// <summary>Storage type.</summary>
    [JsonPropertyName("storageType")]
    public required MetadataV2StorageType StorageType { get; init; }

    /// <summary>Physical locator such as schema/table, object key, or route.</summary>
    [JsonPropertyName("locator")]
    public required string Locator { get; init; }

    /// <summary>Connection identifier.</summary>
    [JsonPropertyName("connectionId")]
    public string? ConnectionId { get; init; }
}

/// <summary>
/// Secret-safe connection reference summary.
/// </summary>
public sealed record MetadataBoundConnectionSummary
{
    /// <summary>Connection semantic identifier.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Connection name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Connection type.</summary>
    [JsonPropertyName("connectionType")]
    public required MetadataV2ConnectionType ConnectionType { get; init; }

    /// <summary>Provider name.</summary>
    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    /// <summary>Safe endpoint when the Metadata v2 graph already declares one.</summary>
    [JsonPropertyName("endpoint")]
    public Uri? Endpoint { get; init; }

    /// <summary>External secret reference name; never a resolved credential value.</summary>
    [JsonPropertyName("secretRef")]
    public string? SecretRef { get; init; }
}

/// <summary>
/// Request to create a metadata release package.
/// </summary>
public sealed record CreateMetadataReleasePackageRequest
{
    /// <summary>Optional package key. Generated when absent.</summary>
    [JsonPropertyName("packageKey")]
    public string? PackageKey { get; init; }

    /// <summary>Optional package namespace.</summary>
    [JsonPropertyName("namespace")]
    public string? Namespace { get; init; }

    /// <summary>Display title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Package summary.</summary>
    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    /// <summary>Source environment.</summary>
    [JsonPropertyName("sourceEnvironment")]
    public required string SourceEnvironment { get; init; }

    /// <summary>Target environments.</summary>
    [JsonPropertyName("targetEnvironments")]
    public IReadOnlyList<string> TargetEnvironments { get; init; } = Array.Empty<string>();

    /// <summary>Changed semantic identifiers.</summary>
    [JsonPropertyName("semanticIds")]
    public IReadOnlyList<string> SemanticIds { get; init; } = Array.Empty<string>();

    /// <summary>Desired Metadata v2 revision. Defaults to the source active revision.</summary>
    [JsonPropertyName("desiredRevision")]
    public long? DesiredRevision { get; init; }

    /// <summary>Optional content-version reference applied to entries without per-artifact content references.</summary>
    [JsonPropertyName("desiredContentVersionId")]
    public string? DesiredContentVersionId { get; init; }

    /// <summary>Optional per-semantic-id change class overrides.</summary>
    [JsonPropertyName("changeClasses")]
    public IReadOnlyDictionary<string, MetadataReleaseChangeClass>? ChangeClasses { get; init; }

    /// <summary>Package-level provenance references reused from Console content metadata.</summary>
    [JsonPropertyName("provenance")]
    public IReadOnlyList<ConsoleProvenanceRef> Provenance { get; init; } = Array.Empty<ConsoleProvenanceRef>();
}

/// <summary>
/// Persisted metadata release package.
/// </summary>
public sealed record MetadataReleasePackage
{
    /// <summary>Package identifier.</summary>
    [JsonPropertyName("packageId")]
    public required Guid PackageId { get; init; }

    /// <summary>Package Metadata v2 object metadata.</summary>
    [JsonPropertyName("metadata")]
    public required MetadataV2ObjectMetadata Metadata { get; init; }

    /// <summary>Source environment.</summary>
    [JsonPropertyName("sourceEnvironment")]
    public required string SourceEnvironment { get; init; }

    /// <summary>Source Metadata v2 revision.</summary>
    [JsonPropertyName("sourceRevision")]
    public required long SourceRevision { get; init; }

    /// <summary>Source Metadata v2 entity tag.</summary>
    [JsonPropertyName("sourceEtag")]
    public required string SourceEtag { get; init; }

    /// <summary>Target environments.</summary>
    [JsonPropertyName("targetEnvironments")]
    public IReadOnlyList<string> TargetEnvironments { get; init; } = Array.Empty<string>();

    /// <summary>Release entries.</summary>
    [JsonPropertyName("entries")]
    public IReadOnlyList<MetadataReleaseEntry> Entries { get; init; } = Array.Empty<MetadataReleaseEntry>();

    /// <summary>Package status.</summary>
    [JsonPropertyName("status")]
    public MetadataReleasePackageStatus Status { get; init; } = MetadataReleasePackageStatus.Draft;

    /// <summary>Actor that created the package.</summary>
    [JsonPropertyName("createdBy")]
    public required string CreatedBy { get; init; }

    /// <summary>Creation timestamp.</summary>
    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Last update timestamp.</summary>
    [JsonPropertyName("updatedAt")]
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Extension data for alpha consumers.</summary>
    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}

/// <summary>
/// One semantic artifact entry in a release package.
/// </summary>
public sealed record MetadataReleaseEntry
{
    /// <summary>Semantic identifier.</summary>
    [JsonPropertyName("semanticId")]
    public required string SemanticId { get; init; }

    /// <summary>Artifact kind.</summary>
    [JsonPropertyName("artifactKind")]
    public required MetadataSemanticArtifactKind ArtifactKind { get; init; }

    /// <summary>Resource type when applicable.</summary>
    [JsonPropertyName("resourceType")]
    public MetadataV2ResourceType? ResourceType { get; init; }

    /// <summary>Desired Metadata v2 revision.</summary>
    [JsonPropertyName("desiredMetadataRevision")]
    public required long DesiredMetadataRevision { get; init; }

    /// <summary>Desired content-version reference.</summary>
    [JsonPropertyName("desiredContentVersionId")]
    public string? DesiredContentVersionId { get; init; }

    /// <summary>Desired provenance references.</summary>
    [JsonPropertyName("desiredProvenance")]
    public IReadOnlyList<ConsoleProvenanceRef> DesiredProvenance { get; init; } = Array.Empty<ConsoleProvenanceRef>();

    /// <summary>Change class.</summary>
    [JsonPropertyName("changeClass")]
    public MetadataReleaseChangeClass ChangeClass { get; init; } = MetadataReleaseChangeClass.Metadata;

    /// <summary>Last-observed target states.</summary>
    [JsonPropertyName("targetStates")]
    public IReadOnlyList<MetadataReleaseTargetState> TargetStates { get; init; } = Array.Empty<MetadataReleaseTargetState>();

    /// <summary>Dependent semantic identifiers.</summary>
    [JsonPropertyName("dependentSemanticIds")]
    public IReadOnlyList<string> DependentSemanticIds { get; init; } = Array.Empty<string>();

    /// <summary>Entry status.</summary>
    [JsonPropertyName("status")]
    public MetadataReleaseEntryStatus Status { get; init; } = MetadataReleaseEntryStatus.Draft;
}

/// <summary>
/// Last-observed target environment state captured for a release entry.
/// </summary>
public sealed record MetadataReleaseTargetState
{
    /// <summary>Target environment.</summary>
    [JsonPropertyName("environment")]
    public required string Environment { get; init; }

    /// <summary>Current Metadata v2 revision in the target environment.</summary>
    [JsonPropertyName("currentMetadataRevision")]
    public long? CurrentMetadataRevision { get; init; }

    /// <summary>Current content-version reference in the target environment.</summary>
    [JsonPropertyName("currentContentVersionId")]
    public string? CurrentContentVersionId { get; init; }

    /// <summary>Binding state in the target environment.</summary>
    [JsonPropertyName("bindingState")]
    public required MetadataEnvironmentBindingState BindingState { get; init; }

    /// <summary>Secret-safe binding summary.</summary>
    [JsonPropertyName("bindingSummary")]
    public MetadataEnvironmentBindingSummary? BindingSummary { get; init; }
}

/// <summary>
/// GitOps-safe metadata release manifest.
/// </summary>
public sealed record GitOpsMetadataReleaseManifest
{
    /// <summary>Manifest API version.</summary>
    [JsonPropertyName("apiVersion")]
    public string ApiVersion { get; init; } = MetadataV2Constants.ApiVersion;

    /// <summary>Manifest kind.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "MetadataReleasePackage";

    /// <summary>Manifest metadata.</summary>
    [JsonPropertyName("metadata")]
    public required MetadataV2ObjectMetadata Metadata { get; init; }

    /// <summary>Manifest specification.</summary>
    [JsonPropertyName("spec")]
    public required GitOpsMetadataReleaseSpec Spec { get; init; }
}

/// <summary>
/// GitOps manifest spec for a metadata release package.
/// </summary>
public sealed record GitOpsMetadataReleaseSpec
{
    /// <summary>Package identifier.</summary>
    [JsonPropertyName("packageId")]
    public required Guid PackageId { get; init; }

    /// <summary>Source environment revision.</summary>
    [JsonPropertyName("source")]
    public required GitOpsMetadataReleaseSource Source { get; init; }

    /// <summary>Target environments.</summary>
    [JsonPropertyName("targets")]
    public IReadOnlyList<GitOpsMetadataReleaseTarget> Targets { get; init; } =
        Array.Empty<GitOpsMetadataReleaseTarget>();

    /// <summary>Release entries.</summary>
    [JsonPropertyName("entries")]
    public IReadOnlyList<GitOpsMetadataReleaseEntry> Entries { get; init; } =
        Array.Empty<GitOpsMetadataReleaseEntry>();
}

/// <summary>
/// GitOps manifest source environment state.
/// </summary>
public sealed record GitOpsMetadataReleaseSource
{
    /// <summary>Source environment.</summary>
    [JsonPropertyName("environment")]
    public required string Environment { get; init; }

    /// <summary>Source Metadata v2 revision.</summary>
    [JsonPropertyName("revision")]
    public required long Revision { get; init; }

    /// <summary>Source Metadata v2 entity tag.</summary>
    [JsonPropertyName("etag")]
    public required string ETag { get; init; }
}

/// <summary>
/// GitOps manifest target environment.
/// </summary>
public sealed record GitOpsMetadataReleaseTarget
{
    /// <summary>Target environment.</summary>
    [JsonPropertyName("environment")]
    public required string Environment { get; init; }
}

/// <summary>
/// GitOps-safe release entry.
/// </summary>
public sealed record GitOpsMetadataReleaseEntry
{
    /// <summary>Semantic identifier.</summary>
    [JsonPropertyName("semanticId")]
    public required string SemanticId { get; init; }

    /// <summary>Artifact kind.</summary>
    [JsonPropertyName("artifactKind")]
    public required MetadataSemanticArtifactKind ArtifactKind { get; init; }

    /// <summary>Resource type when applicable.</summary>
    [JsonPropertyName("resourceType")]
    public MetadataV2ResourceType? ResourceType { get; init; }

    /// <summary>Desired Metadata v2 revision.</summary>
    [JsonPropertyName("desiredMetadataRevision")]
    public required long DesiredMetadataRevision { get; init; }

    /// <summary>Desired content-version reference.</summary>
    [JsonPropertyName("desiredContentVersionId")]
    public string? DesiredContentVersionId { get; init; }

    /// <summary>Desired provenance references.</summary>
    [JsonPropertyName("desiredProvenance")]
    public IReadOnlyList<ConsoleProvenanceRef> DesiredProvenance { get; init; } = Array.Empty<ConsoleProvenanceRef>();

    /// <summary>Change class.</summary>
    [JsonPropertyName("changeClass")]
    public required MetadataReleaseChangeClass ChangeClass { get; init; }

    /// <summary>Last-observed target states with secret-safe bindings.</summary>
    [JsonPropertyName("targetStates")]
    public IReadOnlyList<MetadataReleaseTargetState> TargetStates { get; init; } =
        Array.Empty<MetadataReleaseTargetState>();

    /// <summary>Dependent semantic identifiers.</summary>
    [JsonPropertyName("dependentSemanticIds")]
    public IReadOnlyList<string> DependentSemanticIds { get; init; } = Array.Empty<string>();
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Console.Domain;

namespace Honua.Core.Features.Metadata.Domain.V2;

public sealed record MetadataV2EnvironmentRevision
{
    [JsonPropertyName("environment")]
    public required string Environment { get; init; }

    [JsonPropertyName("revision")]
    public required long Revision { get; init; }

    [JsonPropertyName("etag")]
    public required string ETag { get; init; }

    [JsonPropertyName("activatedAt")]
    public DateTimeOffset? ActivatedAt { get; init; }
}

public sealed record MetadataSemanticInventoryFilter
{
    public MetadataSemanticArtifactKind? ArtifactKind { get; init; }

    public MetadataV2ObjectType? ResourceType { get; init; }
}

public sealed record MetadataSemanticInventoryResponse
{
    [JsonPropertyName("environment")]
    public required string Environment { get; init; }

    [JsonPropertyName("revision")]
    public required long Revision { get; init; }

    [JsonPropertyName("etag")]
    public required string ETag { get; init; }

    [JsonPropertyName("generatedAt")]
    public required DateTimeOffset GeneratedAt { get; init; }

    [JsonPropertyName("entries")]
    public IReadOnlyList<MetadataSemanticInventoryEntry> Entries { get; init; } =
        Array.Empty<MetadataSemanticInventoryEntry>();
}

public sealed record MetadataSemanticInventoryEntry
{
    [JsonPropertyName("semanticId")]
    public required string SemanticId { get; init; }

    [JsonPropertyName("artifactKind")]
    public required MetadataSemanticArtifactKind ArtifactKind { get; init; }

    [JsonPropertyName("resourceType")]
    public MetadataV2ObjectType? ResourceType { get; init; }

    [JsonPropertyName("serviceType")]
    public MetadataV2ServiceType? ServiceType { get; init; }

    [JsonPropertyName("publicationType")]
    public MetadataV2LayerType? PublicationType { get; init; }

    [JsonPropertyName("parentSemanticId")]
    public string? ParentSemanticId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("namespace")]
    public string? Namespace { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("metadataGeneration")]
    public long? MetadataGeneration { get; init; }

    [JsonPropertyName("status")]
    public MetadataV2Status? Status { get; init; }

    [JsonPropertyName("contentVersionId")]
    public string? ContentVersionId { get; init; }

    [JsonPropertyName("provenance")]
    public IReadOnlyList<ConsoleProvenanceRef> Provenance { get; init; } = Array.Empty<ConsoleProvenanceRef>();

    [JsonPropertyName("policyIds")]
    public IReadOnlyList<string> PolicyIds { get; init; } = Array.Empty<string>();
}

public sealed record MetadataEnvironmentBindingsRequest
{
    [JsonPropertyName("environments")]
    public IReadOnlyList<string> Environments { get; init; } = Array.Empty<string>();

    [JsonPropertyName("semanticIds")]
    public IReadOnlyList<string> SemanticIds { get; init; } = Array.Empty<string>();
}

public sealed record MetadataEnvironmentBindingsResponse
{
    [JsonPropertyName("requestedAt")]
    public required DateTimeOffset RequestedAt { get; init; }

    [JsonPropertyName("environments")]
    public IReadOnlyList<string> Environments { get; init; } = Array.Empty<string>();

    [JsonPropertyName("bindings")]
    public IReadOnlyList<MetadataEnvironmentBindingSummary> Bindings { get; init; } =
        Array.Empty<MetadataEnvironmentBindingSummary>();
}

public sealed record MetadataEnvironmentBindingSummary
{
    [JsonPropertyName("semanticId")]
    public required string SemanticId { get; init; }

    [JsonPropertyName("environment")]
    public required string Environment { get; init; }

    [JsonPropertyName("state")]
    public required MetadataEnvironmentBindingState State { get; init; }

    [JsonPropertyName("revision")]
    public long? Revision { get; init; }

    [JsonPropertyName("etag")]
    public string? ETag { get; init; }

    [JsonPropertyName("artifactKind")]
    public MetadataSemanticArtifactKind? ArtifactKind { get; init; }

    [JsonPropertyName("resource")]
    public MetadataBoundResourceSummary? Resource { get; init; }

    [JsonPropertyName("field")]
    public MetadataBoundFieldSummary? Field { get; init; }

    [JsonPropertyName("service")]
    public MetadataBoundServiceSummary? Service { get; init; }

    [JsonPropertyName("publication")]
    public MetadataBoundPublicationSummary? Publication { get; init; }

    [JsonPropertyName("storage")]
    public MetadataBoundStorageSummary? Storage { get; init; }

    [JsonPropertyName("connection")]
    public MetadataBoundConnectionSummary? Connection { get; init; }

    [JsonPropertyName("contentVersionId")]
    public string? ContentVersionId { get; init; }

    [JsonPropertyName("lastObservedAt")]
    public DateTimeOffset? LastObservedAt { get; init; }
}

public sealed record MetadataBoundResourceSummary
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("namespace")]
    public string? Namespace { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("resourceType")]
    public required MetadataV2ObjectType ResourceType { get; init; }

    [JsonPropertyName("primaryStorageBindingId")]
    public string? PrimaryStorageBindingId { get; init; }
}

public sealed record MetadataBoundFieldSummary
{
    [JsonPropertyName("semanticId")]
    public required string SemanticId { get; init; }

    [JsonPropertyName("parentResourceId")]
    public required string ParentResourceId { get; init; }

    [JsonPropertyName("fieldName")]
    public required string FieldName { get; init; }

    [JsonPropertyName("fieldType")]
    public string? FieldType { get; init; }
}

public sealed record MetadataBoundServiceSummary
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("namespace")]
    public string? Namespace { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("serviceType")]
    public required MetadataV2ServiceType ServiceType { get; init; }

    [JsonPropertyName("route")]
    public string? Route { get; init; }
}

public sealed record MetadataBoundPublicationSummary
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("resourceId")]
    public required string ResourceId { get; init; }

    [JsonPropertyName("serviceId")]
    public required string ServiceId { get; init; }

    [JsonPropertyName("publicationType")]
    public required MetadataV2LayerType PublicationType { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("layerIndex")]
    public int? LayerIndex { get; init; }

    [JsonPropertyName("serviceLocalId")]
    public string? ServiceLocalId { get; init; }
}

public sealed record MetadataBoundStorageSummary
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("resourceId")]
    public required string ResourceId { get; init; }

    [JsonPropertyName("storageType")]
    public required MetadataV2StorageType StorageType { get; init; }

    [JsonPropertyName("locator")]
    public required string Locator { get; init; }

    [JsonPropertyName("connectionId")]
    public string? ConnectionId { get; init; }
}

public sealed record MetadataBoundConnectionSummary
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("connectionType")]
    public required MetadataV2ConnectionType ConnectionType { get; init; }

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("endpoint")]
    public Uri? Endpoint { get; init; }

    [JsonPropertyName("secretRef")]
    public string? SecretRef { get; init; }
}

public sealed record CreateMetadataReleasePackageRequest
{
    [JsonPropertyName("packageKey")]
    public string? PackageKey { get; init; }

    [JsonPropertyName("namespace")]
    public string? Namespace { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("sourceEnvironment")]
    public required string SourceEnvironment { get; init; }

    [JsonPropertyName("targetEnvironments")]
    public IReadOnlyList<string> TargetEnvironments { get; init; } = Array.Empty<string>();

    [JsonPropertyName("semanticIds")]
    public IReadOnlyList<string> SemanticIds { get; init; } = Array.Empty<string>();

    [JsonPropertyName("desiredRevision")]
    public long? DesiredRevision { get; init; }

    [JsonPropertyName("desiredContentVersionId")]
    public string? DesiredContentVersionId { get; init; }

    [JsonPropertyName("changeClasses")]
    public IReadOnlyDictionary<string, MetadataReleaseChangeClass>? ChangeClasses { get; init; }

    [JsonPropertyName("provenance")]
    public IReadOnlyList<ConsoleProvenanceRef> Provenance { get; init; } = Array.Empty<ConsoleProvenanceRef>();
}

public sealed record CreateWorkflowReleasePackageRequest
{
    [JsonPropertyName("semanticId")]
    public required string SemanticId { get; init; }

    [JsonPropertyName("packageId")]
    public required string PackageId { get; init; }

    [JsonPropertyName("packageVersion")]
    public required int PackageVersion { get; init; }

    [JsonPropertyName("packageHash")]
    public required string PackageHash { get; init; }

    [JsonPropertyName("publicationId")]
    public required string PublicationId { get; init; }

    [JsonPropertyName("sourceEnvironment")]
    public required string SourceEnvironment { get; init; }

    [JsonPropertyName("targetEnvironments")]
    public IReadOnlyList<string> TargetEnvironments { get; init; } = Array.Empty<string>();

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("namespace")]
    public string? Namespace { get; init; }

    [JsonPropertyName("provenance")]
    public IReadOnlyList<ConsoleProvenanceRef> Provenance { get; init; } = Array.Empty<ConsoleProvenanceRef>();
}

public sealed record MetadataReleasePackage
{
    [JsonPropertyName("packageId")]
    public required Guid PackageId { get; init; }

    [JsonPropertyName("metadata")]
    public required MetadataV2ObjectMetadata Metadata { get; init; }

    [JsonPropertyName("sourceEnvironment")]
    public required string SourceEnvironment { get; init; }

    [JsonPropertyName("sourceRevision")]
    public required long SourceRevision { get; init; }

    [JsonPropertyName("sourceEtag")]
    public required string SourceEtag { get; init; }

    [JsonPropertyName("targetEnvironments")]
    public IReadOnlyList<string> TargetEnvironments { get; init; } = Array.Empty<string>();

    [JsonPropertyName("entries")]
    public IReadOnlyList<MetadataReleaseEntry> Entries { get; init; } = Array.Empty<MetadataReleaseEntry>();

    [JsonPropertyName("status")]
    public MetadataReleasePackageStatus Status { get; init; } = MetadataReleasePackageStatus.Draft;

    [JsonPropertyName("createdBy")]
    public required string CreatedBy { get; init; }

    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public required DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("extensions")]
    public IReadOnlyDictionary<string, JsonElement> Extensions { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record MetadataReleasePackageSummary
{
    [JsonPropertyName("packageId")]
    public required Guid PackageId { get; init; }

    [JsonPropertyName("packageKey")]
    public required string PackageKey { get; init; }

    [JsonPropertyName("namespace")]
    public string? Namespace { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("sourceEnvironment")]
    public required string SourceEnvironment { get; init; }

    [JsonPropertyName("sourceRevision")]
    public required long SourceRevision { get; init; }

    [JsonPropertyName("targetEnvironments")]
    public IReadOnlyList<string> TargetEnvironments { get; init; } = Array.Empty<string>();

    [JsonPropertyName("entryCount")]
    public required int EntryCount { get; init; }

    [JsonPropertyName("status")]
    public MetadataReleasePackageStatus Status { get; init; } = MetadataReleasePackageStatus.Draft;

    [JsonPropertyName("createdBy")]
    public required string CreatedBy { get; init; }

    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record MetadataReleasePackageListFilter
{
    public int Limit { get; init; } = 50;

    public int Offset { get; init; }

    public string? SourceEnvironment { get; init; }

    public string? TargetEnvironment { get; init; }

    public MetadataReleasePackageStatus? Status { get; init; }
}

public sealed record MetadataReleasePackageListResponse
{
    [JsonPropertyName("generatedAt")]
    public required DateTimeOffset GeneratedAt { get; init; }

    [JsonPropertyName("items")]
    public IReadOnlyList<MetadataReleasePackageSummary> Items { get; init; } =
        Array.Empty<MetadataReleasePackageSummary>();

    [JsonPropertyName("count")]
    public required int Count { get; init; }

    [JsonPropertyName("limit")]
    public required int Limit { get; init; }

    [JsonPropertyName("offset")]
    public required int Offset { get; init; }
}

public sealed record MetadataReleaseEntry
{
    [JsonPropertyName("semanticId")]
    public required string SemanticId { get; init; }

    [JsonPropertyName("artifactKind")]
    public required MetadataSemanticArtifactKind ArtifactKind { get; init; }

    [JsonPropertyName("resourceType")]
    public MetadataV2ObjectType? ResourceType { get; init; }

    [JsonPropertyName("sourceField")]
    public MetadataBoundFieldSummary? SourceField { get; init; }

    [JsonPropertyName("desiredMetadataRevision")]
    public required long DesiredMetadataRevision { get; init; }

    [JsonPropertyName("desiredContentVersionId")]
    public string? DesiredContentVersionId { get; init; }

    [JsonPropertyName("desiredProvenance")]
    public IReadOnlyList<ConsoleProvenanceRef> DesiredProvenance { get; init; } = Array.Empty<ConsoleProvenanceRef>();

    [JsonPropertyName("changeClass")]
    public MetadataReleaseChangeClass ChangeClass { get; init; } = MetadataReleaseChangeClass.Metadata;

    [JsonPropertyName("targetStates")]
    public IReadOnlyList<MetadataReleaseTargetState> TargetStates { get; init; } = Array.Empty<MetadataReleaseTargetState>();

    [JsonPropertyName("dependentSemanticIds")]
    public IReadOnlyList<string> DependentSemanticIds { get; init; } = Array.Empty<string>();

    [JsonPropertyName("status")]
    public MetadataReleaseEntryStatus Status { get; init; } = MetadataReleaseEntryStatus.Draft;
}

public sealed record MetadataReleaseTargetState
{
    [JsonPropertyName("environment")]
    public required string Environment { get; init; }

    [JsonPropertyName("currentMetadataRevision")]
    public long? CurrentMetadataRevision { get; init; }

    [JsonPropertyName("currentContentVersionId")]
    public string? CurrentContentVersionId { get; init; }

    [JsonPropertyName("bindingState")]
    public required MetadataEnvironmentBindingState BindingState { get; init; }

    [JsonPropertyName("bindingSummary")]
    public MetadataEnvironmentBindingSummary? BindingSummary { get; init; }
}

public sealed record GitOpsMetadataReleaseManifest
{
    [JsonPropertyName("apiVersion")]
    public string ApiVersion { get; init; } = MetadataV2Constants.ApiVersion;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "MetadataReleasePackage";

    [JsonPropertyName("metadata")]
    public required MetadataV2ObjectMetadata Metadata { get; init; }

    [JsonPropertyName("spec")]
    public required GitOpsMetadataReleaseSpec Spec { get; init; }
}

public sealed record GitOpsMetadataReleaseSpec
{
    [JsonPropertyName("packageId")]
    public required Guid PackageId { get; init; }

    [JsonPropertyName("source")]
    public required GitOpsMetadataReleaseSource Source { get; init; }

    [JsonPropertyName("targets")]
    public IReadOnlyList<GitOpsMetadataReleaseTarget> Targets { get; init; } =
        Array.Empty<GitOpsMetadataReleaseTarget>();

    [JsonPropertyName("entries")]
    public IReadOnlyList<GitOpsMetadataReleaseEntry> Entries { get; init; } =
        Array.Empty<GitOpsMetadataReleaseEntry>();
}

public sealed record GitOpsMetadataReleaseSource
{
    [JsonPropertyName("environment")]
    public required string Environment { get; init; }

    [JsonPropertyName("revision")]
    public required long Revision { get; init; }

    [JsonPropertyName("etag")]
    public required string ETag { get; init; }
}

public sealed record GitOpsMetadataReleaseTarget
{
    [JsonPropertyName("environment")]
    public required string Environment { get; init; }
}

public sealed record GitOpsMetadataReleaseEntry
{
    [JsonPropertyName("semanticId")]
    public required string SemanticId { get; init; }

    [JsonPropertyName("artifactKind")]
    public required MetadataSemanticArtifactKind ArtifactKind { get; init; }

    [JsonPropertyName("resourceType")]
    public MetadataV2ObjectType? ResourceType { get; init; }

    [JsonPropertyName("sourceField")]
    public MetadataBoundFieldSummary? SourceField { get; init; }

    [JsonPropertyName("desiredMetadataRevision")]
    public required long DesiredMetadataRevision { get; init; }

    [JsonPropertyName("desiredContentVersionId")]
    public string? DesiredContentVersionId { get; init; }

    [JsonPropertyName("desiredProvenance")]
    public IReadOnlyList<ConsoleProvenanceRef> DesiredProvenance { get; init; } = Array.Empty<ConsoleProvenanceRef>();

    [JsonPropertyName("changeClass")]
    public required MetadataReleaseChangeClass ChangeClass { get; init; }

    [JsonPropertyName("targetStates")]
    public IReadOnlyList<MetadataReleaseTargetState> TargetStates { get; init; } =
        Array.Empty<MetadataReleaseTargetState>();

    [JsonPropertyName("dependentSemanticIds")]
    public IReadOnlyList<string> DependentSemanticIds { get; init; } = Array.Empty<string>();
}

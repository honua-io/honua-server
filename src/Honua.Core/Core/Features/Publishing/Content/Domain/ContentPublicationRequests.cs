// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Publishing.Content.Domain;

public sealed record PublishContentRequest
{
    [JsonPropertyName("kind")]
    public ContentPublicationKind Kind { get; init; }

    [JsonPropertyName("routeSlug")]
    public string? RouteSlug { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("sourceContentId")]
    public string? SourceContentId { get; init; }

    [JsonPropertyName("sourcePackageId")]
    public string? SourcePackageId { get; init; }

    // The server hashes the raw content and never stores it.
    [JsonPropertyName("contentPayload")]
    public string? ContentPayload { get; init; }

    [JsonPropertyName("contentHash")]
    public string? ContentHash { get; init; }

    [JsonPropertyName("contentVersionId")]
    public string? ContentVersionId { get; init; }

    [JsonPropertyName("sourceMetadataRevision")]
    public long? SourceMetadataRevision { get; init; }

    [JsonPropertyName("sourceMetadataEtag")]
    public string? SourceMetadataEtag { get; init; }

    [JsonPropertyName("appManifestId")]
    public string? AppManifestId { get; init; }

    [JsonPropertyName("appBundleArtifactId")]
    public string? AppBundleArtifactId { get; init; }

    [JsonPropertyName("defaultViewBbox")]
    public ContentPublicationBbox? DefaultViewBbox { get; init; }

    [JsonPropertyName("policy")]
    public ContentPublicationPolicy? Policy { get; init; }

    [JsonPropertyName("dependencies")]
    public IReadOnlyList<ContentPublicationDependencyRef>? Dependencies { get; init; }

    [JsonPropertyName("provenance")]
    public IReadOnlyList<ContentPublicationProvenanceRef>? Provenance { get; init; }

    [JsonPropertyName("jobId")]
    public string? JobId { get; init; }

    [JsonPropertyName("operationId")]
    public string? OperationId { get; init; }
}

public sealed record RepublishContentRequest
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("sourceContentId")]
    public string? SourceContentId { get; init; }

    [JsonPropertyName("sourcePackageId")]
    public string? SourcePackageId { get; init; }

    [JsonPropertyName("contentPayload")]
    public string? ContentPayload { get; init; }

    [JsonPropertyName("contentHash")]
    public string? ContentHash { get; init; }

    [JsonPropertyName("contentVersionId")]
    public string? ContentVersionId { get; init; }

    [JsonPropertyName("sourceMetadataRevision")]
    public long? SourceMetadataRevision { get; init; }

    [JsonPropertyName("sourceMetadataEtag")]
    public string? SourceMetadataEtag { get; init; }

    [JsonPropertyName("appManifestId")]
    public string? AppManifestId { get; init; }

    [JsonPropertyName("appBundleArtifactId")]
    public string? AppBundleArtifactId { get; init; }

    [JsonPropertyName("defaultViewBbox")]
    public ContentPublicationBbox? DefaultViewBbox { get; init; }

    [JsonPropertyName("dependencies")]
    public IReadOnlyList<ContentPublicationDependencyRef>? Dependencies { get; init; }

    [JsonPropertyName("provenance")]
    public IReadOnlyList<ContentPublicationProvenanceRef>? Provenance { get; init; }

    [JsonPropertyName("jobId")]
    public string? JobId { get; init; }

    [JsonPropertyName("operationId")]
    public string? OperationId { get; init; }

    [JsonPropertyName("expectedEtag")]
    public string? ExpectedEtag { get; init; }
}

public sealed record RollbackContentRequest
{
    [JsonPropertyName("targetVersionId")]
    public string? TargetVersionId { get; init; }

    [JsonPropertyName("targetRevision")]
    public long? TargetRevision { get; init; }

    [JsonPropertyName("expectedEtag")]
    public string? ExpectedEtag { get; init; }
}

public sealed record UpdatePublicationPolicyRequest
{
    [JsonPropertyName("visibility")]
    public ContentPublicationVisibility? Visibility { get; init; }

    [JsonPropertyName("access")]
    public Honua.Core.Features.Security.Domain.AccessPolicy? Access { get; init; }

    [JsonPropertyName("share")]
    public ContentSharePolicy? Share { get; init; }

    [JsonPropertyName("embed")]
    public ContentEmbedPolicy? Embed { get; init; }

    [JsonPropertyName("service")]
    public ContentServicePolicy? Service { get; init; }

    [JsonPropertyName("publicLinkEnabled")]
    public bool? PublicLinkEnabled { get; init; }

    [JsonPropertyName("createPublicLink")]
    public ContentPublicLinkRequest? CreatePublicLink { get; init; }

    [JsonPropertyName("revokePublicLinkId")]
    public string? RevokePublicLinkId { get; init; }

    [JsonPropertyName("expectedEtag")]
    public string? ExpectedEtag { get; init; }
}

public sealed record ContentPublicLinkRequest
{
    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("token")]
    public string? Token { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; init; }
}

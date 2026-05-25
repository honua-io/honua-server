// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Publishing.Content.Domain;

/// <summary>
/// Request to publish a brand-new artifact version and claim a route. The server
/// allocates the publication id, version id, and revision; the route slug is
/// normalized server-side.
/// </summary>
public sealed record PublishContentRequest
{
    /// <summary>Artifact kind.</summary>
    [JsonPropertyName("kind")]
    public ContentPublicationKind Kind { get; init; }

    /// <summary>Requested route slug. Normalized and validated server-side.</summary>
    [JsonPropertyName("routeSlug")]
    public string? RouteSlug { get; init; }

    /// <summary>Optional display title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Optional source Console content item id.</summary>
    [JsonPropertyName("sourceContentId")]
    public string? SourceContentId { get; init; }

    /// <summary>Optional source map/app package id.</summary>
    [JsonPropertyName("sourcePackageId")]
    public string? SourcePackageId { get; init; }

    /// <summary>Optional raw content payload; the server hashes it (SHA-256) and never stores it.</summary>
    [JsonPropertyName("contentPayload")]
    public string? ContentPayload { get; init; }

    /// <summary>Optional precomputed content hash (used when the payload is not supplied).</summary>
    [JsonPropertyName("contentHash")]
    public string? ContentHash { get; init; }

    /// <summary>Optional opaque content-version id.</summary>
    [JsonPropertyName("contentVersionId")]
    public string? ContentVersionId { get; init; }

    /// <summary>Optional Metadata v2 graph revision the artifact was published against.</summary>
    [JsonPropertyName("sourceMetadataRevision")]
    public long? SourceMetadataRevision { get; init; }

    /// <summary>Optional Metadata v2 graph etag captured at publish time.</summary>
    [JsonPropertyName("sourceMetadataEtag")]
    public string? SourceMetadataEtag { get; init; }

    /// <summary>Generated-app manifest artifact id.</summary>
    [JsonPropertyName("appManifestId")]
    public string? AppManifestId { get; init; }

    /// <summary>Generated-app bundle artifact id.</summary>
    [JsonPropertyName("appBundleArtifactId")]
    public string? AppBundleArtifactId { get; init; }

    /// <summary>CRS-declared default view bbox.</summary>
    [JsonPropertyName("defaultViewBbox")]
    public ContentPublicationBbox? DefaultViewBbox { get; init; }

    /// <summary>Initial server-owned policy. Defaults to private when omitted.</summary>
    [JsonPropertyName("policy")]
    public ContentPublicationPolicy? Policy { get; init; }

    /// <summary>Dependency references (validated against registered stores when possible).</summary>
    [JsonPropertyName("dependencies")]
    public IReadOnlyList<ContentPublicationDependencyRef>? Dependencies { get; init; }

    /// <summary>Provenance references.</summary>
    [JsonPropertyName("provenance")]
    public IReadOnlyList<ContentPublicationProvenanceRef>? Provenance { get; init; }

    /// <summary>Optional source job id.</summary>
    [JsonPropertyName("jobId")]
    public string? JobId { get; init; }

    /// <summary>Optional operation id.</summary>
    [JsonPropertyName("operationId")]
    public string? OperationId { get; init; }
}

/// <summary>
/// Request to republish an existing publication: creates a new immutable version
/// and moves the active route pointer to it.
/// </summary>
public sealed record RepublishContentRequest
{
    /// <summary>Optional display title for the new version.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Optional source Console content item id.</summary>
    [JsonPropertyName("sourceContentId")]
    public string? SourceContentId { get; init; }

    /// <summary>Optional source map/app package id.</summary>
    [JsonPropertyName("sourcePackageId")]
    public string? SourcePackageId { get; init; }

    /// <summary>Optional raw content payload; hashed server-side and never stored.</summary>
    [JsonPropertyName("contentPayload")]
    public string? ContentPayload { get; init; }

    /// <summary>Optional precomputed content hash.</summary>
    [JsonPropertyName("contentHash")]
    public string? ContentHash { get; init; }

    /// <summary>Optional opaque content-version id.</summary>
    [JsonPropertyName("contentVersionId")]
    public string? ContentVersionId { get; init; }

    /// <summary>Optional Metadata v2 graph revision.</summary>
    [JsonPropertyName("sourceMetadataRevision")]
    public long? SourceMetadataRevision { get; init; }

    /// <summary>Optional Metadata v2 graph etag.</summary>
    [JsonPropertyName("sourceMetadataEtag")]
    public string? SourceMetadataEtag { get; init; }

    /// <summary>Generated-app manifest artifact id.</summary>
    [JsonPropertyName("appManifestId")]
    public string? AppManifestId { get; init; }

    /// <summary>Generated-app bundle artifact id.</summary>
    [JsonPropertyName("appBundleArtifactId")]
    public string? AppBundleArtifactId { get; init; }

    /// <summary>CRS-declared default view bbox.</summary>
    [JsonPropertyName("defaultViewBbox")]
    public ContentPublicationBbox? DefaultViewBbox { get; init; }

    /// <summary>Dependency references for the new version.</summary>
    [JsonPropertyName("dependencies")]
    public IReadOnlyList<ContentPublicationDependencyRef>? Dependencies { get; init; }

    /// <summary>Provenance references for the new version.</summary>
    [JsonPropertyName("provenance")]
    public IReadOnlyList<ContentPublicationProvenanceRef>? Provenance { get; init; }

    /// <summary>Optional source job id.</summary>
    [JsonPropertyName("jobId")]
    public string? JobId { get; init; }

    /// <summary>Optional operation id.</summary>
    [JsonPropertyName("operationId")]
    public string? OperationId { get; init; }

    /// <summary>Expected route etag for optimistic concurrency. When set, a mismatch is a conflict.</summary>
    [JsonPropertyName("expectedEtag")]
    public string? ExpectedEtag { get; init; }
}

/// <summary>
/// Request to roll back the active route pointer to an earlier immutable version.
/// No new version row is created.
/// </summary>
public sealed record RollbackContentRequest
{
    /// <summary>Target version id to roll back to. Either this or <see cref="TargetRevision"/> is required.</summary>
    [JsonPropertyName("targetVersionId")]
    public string? TargetVersionId { get; init; }

    /// <summary>Target revision to roll back to.</summary>
    [JsonPropertyName("targetRevision")]
    public long? TargetRevision { get; init; }

    /// <summary>Expected route etag for optimistic concurrency.</summary>
    [JsonPropertyName("expectedEtag")]
    public string? ExpectedEtag { get; init; }
}

/// <summary>
/// Request to update server-owned share/embed/visibility/public-link policy. No new
/// version row is created; the change is recorded as an audited event.
/// </summary>
public sealed record UpdatePublicationPolicyRequest
{
    /// <summary>New visibility scope, when changing.</summary>
    [JsonPropertyName("visibility")]
    public ContentPublicationVisibility? Visibility { get; init; }

    /// <summary>New role/anonymous access policy, when changing.</summary>
    [JsonPropertyName("access")]
    public Honua.Core.Features.Security.Domain.AccessPolicy? Access { get; init; }

    /// <summary>New share policy, when changing.</summary>
    [JsonPropertyName("share")]
    public ContentSharePolicy? Share { get; init; }

    /// <summary>New embed policy, when changing.</summary>
    [JsonPropertyName("embed")]
    public ContentEmbedPolicy? Embed { get; init; }

    /// <summary>New backing-service policy, when changing.</summary>
    [JsonPropertyName("service")]
    public ContentServicePolicy? Service { get; init; }

    /// <summary>Master public-link enable flag, when changing.</summary>
    [JsonPropertyName("publicLinkEnabled")]
    public bool? PublicLinkEnabled { get; init; }

    /// <summary>When set, creates a new public link.</summary>
    [JsonPropertyName("createPublicLink")]
    public ContentPublicLinkRequest? CreatePublicLink { get; init; }

    /// <summary>When set, revokes the public link with this id.</summary>
    [JsonPropertyName("revokePublicLinkId")]
    public string? RevokePublicLinkId { get; init; }

    /// <summary>Expected route etag for optimistic concurrency.</summary>
    [JsonPropertyName("expectedEtag")]
    public string? ExpectedEtag { get; init; }
}

/// <summary>
/// Request to create a public link. The optional raw token is hashed server-side
/// and never persisted or echoed back after creation.
/// </summary>
public sealed record ContentPublicLinkRequest
{
    /// <summary>Optional human-readable label.</summary>
    [JsonPropertyName("label")]
    public string? Label { get; init; }

    /// <summary>Optional raw token. Hashed (SHA-256) server-side; never stored in the clear.</summary>
    [JsonPropertyName("token")]
    public string? Token { get; init; }

    /// <summary>Optional expiry.</summary>
    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; init; }
}

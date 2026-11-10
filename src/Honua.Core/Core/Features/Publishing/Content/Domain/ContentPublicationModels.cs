// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Publishing.Content.Domain;

// The bounding box CRS must be declared; Web Mercator is never assumed.
public sealed record ContentPublicationBbox
{
    [JsonPropertyName("crs")]
    public string Crs { get; init; } = "EPSG:4326";

    [JsonPropertyName("minX")]
    public required double MinX { get; init; }

    [JsonPropertyName("minY")]
    public required double MinY { get; init; }

    [JsonPropertyName("maxX")]
    public required double MaxX { get; init; }

    [JsonPropertyName("maxY")]
    public required double MaxY { get; init; }
}

// A dependency is stored by id only, so the registry never copies the service, layer, or package payload.
public sealed record ContentPublicationDependencyRef
{
    [JsonPropertyName("kind")]
    public required ContentPublicationDependencyKind Kind { get; init; }

    [JsonPropertyName("refId")]
    public required string RefId { get; init; }

    [JsonPropertyName("revision")]
    public long? Revision { get; init; }

    [JsonPropertyName("etag")]
    public string? Etag { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }
}

public sealed record ContentPublicationProvenanceRef
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("refId")]
    public required string RefId { get; init; }

    [JsonPropertyName("rel")]
    public string? Rel { get; init; }
}

// A version row is never mutated; rollback and republish move the route pointer only.
public sealed record ContentPublicationVersion
{
    [JsonPropertyName("publicationId")]
    public required string PublicationId { get; init; }

    [JsonPropertyName("versionId")]
    public required string VersionId { get; init; }

    [JsonPropertyName("revision")]
    public required long Revision { get; init; }

    [JsonPropertyName("kind")]
    public required ContentPublicationKind Kind { get; init; }

    [JsonPropertyName("routeSlug")]
    public required string RouteSlug { get; init; }

    [JsonPropertyName("routePath")]
    public required string RoutePath { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("sourceContentId")]
    public string? SourceContentId { get; init; }

    [JsonPropertyName("sourcePackageId")]
    public string? SourcePackageId { get; init; }

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
    public required ContentPublicationPolicy Policy { get; init; }

    [JsonPropertyName("dependencies")]
    public IReadOnlyList<ContentPublicationDependencyRef> Dependencies { get; init; } = [];

    [JsonPropertyName("provenance")]
    public IReadOnlyList<ContentPublicationProvenanceRef> Provenance { get; init; } = [];

    [JsonPropertyName("jobId")]
    public string? JobId { get; init; }

    [JsonPropertyName("operationId")]
    public string? OperationId { get; init; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("auditRef")]
    public string? AuditRef { get; init; }

    [JsonPropertyName("createdBy")]
    public required string CreatedBy { get; init; }

    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record ContentPublicationRouteState
{
    [JsonPropertyName("publicationId")]
    public required string PublicationId { get; init; }

    [JsonPropertyName("routeSlug")]
    public required string RouteSlug { get; init; }

    [JsonPropertyName("routePath")]
    public required string RoutePath { get; init; }

    [JsonPropertyName("kind")]
    public required ContentPublicationKind Kind { get; init; }

    [JsonPropertyName("activeVersionId")]
    public required string ActiveVersionId { get; init; }

    [JsonPropertyName("activeRevision")]
    public required long ActiveRevision { get; init; }

    [JsonPropertyName("previousVersionId")]
    public string? PreviousVersionId { get; init; }

    [JsonPropertyName("rollbackTargetVersionId")]
    public string? RollbackTargetVersionId { get; init; }

    [JsonPropertyName("lifecycle")]
    public ContentPublicationLifecycle Lifecycle { get; init; } = ContentPublicationLifecycle.Active;

    [JsonPropertyName("policy")]
    public required ContentPublicationPolicy Policy { get; init; }

    [JsonPropertyName("generation")]
    public required long Generation { get; init; }

    [JsonPropertyName("etag")]
    public required string Etag { get; init; }

    [JsonPropertyName("updatedBy")]
    public required string UpdatedBy { get; init; }

    [JsonPropertyName("updatedAt")]
    public required DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record ContentPublicationEvent
{
    [JsonPropertyName("eventId")]
    public required string EventId { get; init; }

    [JsonPropertyName("publicationId")]
    public required string PublicationId { get; init; }

    [JsonPropertyName("sequence")]
    public long Sequence { get; init; }

    [JsonPropertyName("operation")]
    public required ContentPublicationOperation Operation { get; init; }

    [JsonPropertyName("versionId")]
    public string? VersionId { get; init; }

    [JsonPropertyName("revision")]
    public long? Revision { get; init; }

    [JsonPropertyName("routeSlug")]
    public required string RouteSlug { get; init; }

    [JsonPropertyName("actor")]
    public required string Actor { get; init; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record ContentPublicationDetail
{
    [JsonPropertyName("route")]
    public required ContentPublicationRouteState Route { get; init; }

    [JsonPropertyName("versions")]
    public IReadOnlyList<ContentPublicationVersion> Versions { get; init; } = [];
}

public sealed record ContentPublicationListResponse
{
    [JsonPropertyName("items")]
    public IReadOnlyList<ContentPublicationDetail> Items { get; init; } = [];

    [JsonPropertyName("total")]
    public int Total { get; init; }

    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; init; }
}

public sealed record ContentPublicationListQuery
{
    public string? Cursor { get; init; }

    public int Limit { get; init; } = 25;

    public ContentPublicationLifecycle? Lifecycle { get; init; }
}

public sealed record PublishedArtifactView
{
    [JsonPropertyName("publicationId")]
    public required string PublicationId { get; init; }

    [JsonPropertyName("routeSlug")]
    public required string RouteSlug { get; init; }

    [JsonPropertyName("kind")]
    public required ContentPublicationKind Kind { get; init; }

    [JsonPropertyName("revision")]
    public required long Revision { get; init; }

    [JsonPropertyName("versionId")]
    public required string VersionId { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("visibility")]
    public required ContentPublicationVisibility Visibility { get; init; }

    [JsonPropertyName("defaultViewBbox")]
    public ContentPublicationBbox? DefaultViewBbox { get; init; }

    [JsonPropertyName("embeddable")]
    public bool Embeddable { get; init; }

    [JsonPropertyName("allowedEmbedOrigins")]
    public IReadOnlyList<string>? AllowedEmbedOrigins { get; init; }

    [JsonPropertyName("frameAncestors")]
    public IReadOnlyList<string>? FrameAncestors { get; init; }

    [JsonPropertyName("appManifestId")]
    public string? AppManifestId { get; init; }

    [JsonPropertyName("appBundleArtifactId")]
    public string? AppBundleArtifactId { get; init; }

    [JsonPropertyName("contentHash")]
    public string? ContentHash { get; init; }

    [JsonPropertyName("dependencies")]
    public IReadOnlyList<ContentPublicationDependencyRef>? Dependencies { get; init; }

    [JsonPropertyName("updatedAt")]
    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ContentPublicationPolicyUpdateResponse
{
    [JsonPropertyName("route")]
    public required ContentPublicationRouteState Route { get; init; }

    [JsonPropertyName("createdPublicLinkId")]
    public string? CreatedPublicLinkId { get; init; }

    [JsonPropertyName("createdPublicLinkToken")]
    public string? CreatedPublicLinkToken { get; init; }
}

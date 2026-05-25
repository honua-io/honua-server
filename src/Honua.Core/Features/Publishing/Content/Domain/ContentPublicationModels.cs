// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Publishing.Content.Domain;

/// <summary>
/// A CRS-declared bounding box for an artifact's default view. The CRS must be
/// declared explicitly; the default is EPSG:4326 lon/lat and Web Mercator is never
/// silently assumed.
/// </summary>
public sealed record ContentPublicationBbox
{
    /// <summary>Coordinate reference system identifier. Defaults to EPSG:4326 (lon/lat).</summary>
    [JsonPropertyName("crs")]
    public string Crs { get; init; } = "EPSG:4326";

    /// <summary>Minimum X (longitude in EPSG:4326).</summary>
    [JsonPropertyName("minX")]
    public required double MinX { get; init; }

    /// <summary>Minimum Y (latitude in EPSG:4326).</summary>
    [JsonPropertyName("minY")]
    public required double MinY { get; init; }

    /// <summary>Maximum X (longitude in EPSG:4326).</summary>
    [JsonPropertyName("maxX")]
    public required double MaxX { get; init; }

    /// <summary>Maximum Y (latitude in EPSG:4326).</summary>
    [JsonPropertyName("maxY")]
    public required double MaxY { get; init; }
}

/// <summary>
/// A reference to a dependency of a published artifact. Stored by id only so the
/// registry never duplicates service/layer/package payloads.
/// </summary>
public sealed record ContentPublicationDependencyRef
{
    /// <summary>Category of the referenced dependency.</summary>
    [JsonPropertyName("kind")]
    public required ContentPublicationDependencyKind Kind { get; init; }

    /// <summary>Identifier of the referenced dependency.</summary>
    [JsonPropertyName("refId")]
    public required string RefId { get; init; }

    /// <summary>Optional revision the artifact was published against.</summary>
    [JsonPropertyName("revision")]
    public long? Revision { get; init; }

    /// <summary>Optional etag captured at publish time.</summary>
    [JsonPropertyName("etag")]
    public string? Etag { get; init; }

    /// <summary>Optional human-readable label.</summary>
    [JsonPropertyName("label")]
    public string? Label { get; init; }
}

/// <summary>
/// A provenance reference linking a published version to a source job, event,
/// audit record, or Console content item.
/// </summary>
public sealed record ContentPublicationProvenanceRef
{
    /// <summary>Source kind (e.g. <c>job</c>, <c>event</c>, <c>audit</c>, <c>console-item</c>).</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>Source identifier.</summary>
    [JsonPropertyName("refId")]
    public required string RefId { get; init; }

    /// <summary>Optional relationship label.</summary>
    [JsonPropertyName("rel")]
    public string? Rel { get; init; }
}

/// <summary>
/// An immutable, append-only content publication version. Once created a version
/// row is never mutated; rollback and republish operate on the route pointer only.
/// </summary>
public sealed record ContentPublicationVersion
{
    /// <summary>Owning publication id.</summary>
    [JsonPropertyName("publicationId")]
    public required string PublicationId { get; init; }

    /// <summary>Stable version id (immutable).</summary>
    [JsonPropertyName("versionId")]
    public required string VersionId { get; init; }

    /// <summary>Monotonically increasing revision within the publication (1-based).</summary>
    [JsonPropertyName("revision")]
    public required long Revision { get; init; }

    /// <summary>Artifact kind.</summary>
    [JsonPropertyName("kind")]
    public required ContentPublicationKind Kind { get; init; }

    /// <summary>Route slug captured at publish time.</summary>
    [JsonPropertyName("routeSlug")]
    public required string RouteSlug { get; init; }

    /// <summary>Canonical public route path captured at publish time.</summary>
    [JsonPropertyName("routePath")]
    public required string RoutePath { get; init; }

    /// <summary>Optional display title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Optional source Console content item id.</summary>
    [JsonPropertyName("sourceContentId")]
    public string? SourceContentId { get; init; }

    /// <summary>Optional source map/app package id.</summary>
    [JsonPropertyName("sourcePackageId")]
    public string? SourcePackageId { get; init; }

    /// <summary>Content hash of the published payload (SHA-256, hex) when supplied.</summary>
    [JsonPropertyName("contentHash")]
    public string? ContentHash { get; init; }

    /// <summary>Optional opaque content-version id of the source payload.</summary>
    [JsonPropertyName("contentVersionId")]
    public string? ContentVersionId { get; init; }

    /// <summary>Optional Metadata v2 graph revision the artifact was published against.</summary>
    [JsonPropertyName("sourceMetadataRevision")]
    public long? SourceMetadataRevision { get; init; }

    /// <summary>Optional Metadata v2 graph etag captured at publish time.</summary>
    [JsonPropertyName("sourceMetadataEtag")]
    public string? SourceMetadataEtag { get; init; }

    /// <summary>Generated-app manifest artifact id for this exact version.</summary>
    [JsonPropertyName("appManifestId")]
    public string? AppManifestId { get; init; }

    /// <summary>Generated-app bundle artifact id for this exact version.</summary>
    [JsonPropertyName("appBundleArtifactId")]
    public string? AppBundleArtifactId { get; init; }

    /// <summary>CRS-declared default view bbox, when applicable.</summary>
    [JsonPropertyName("defaultViewBbox")]
    public ContentPublicationBbox? DefaultViewBbox { get; init; }

    /// <summary>Policy snapshot captured at publish time.</summary>
    [JsonPropertyName("policy")]
    public required ContentPublicationPolicy Policy { get; init; }

    /// <summary>Dependency graph references (by id only).</summary>
    [JsonPropertyName("dependencies")]
    public IReadOnlyList<ContentPublicationDependencyRef> Dependencies { get; init; } = [];

    /// <summary>Provenance references.</summary>
    [JsonPropertyName("provenance")]
    public IReadOnlyList<ContentPublicationProvenanceRef> Provenance { get; init; } = [];

    /// <summary>Optional source job id.</summary>
    [JsonPropertyName("jobId")]
    public string? JobId { get; init; }

    /// <summary>Optional operation id.</summary>
    [JsonPropertyName("operationId")]
    public string? OperationId { get; init; }

    /// <summary>Optional correlation id linking to logs/traces.</summary>
    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    /// <summary>Optional reference to the audit record for this publish.</summary>
    [JsonPropertyName("auditRef")]
    public string? AuditRef { get; init; }

    /// <summary>Actor that created the version.</summary>
    [JsonPropertyName("createdBy")]
    public required string CreatedBy { get; init; }

    /// <summary>Creation time.</summary>
    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// The mutable route-state pointer for a publication. This is the only mutable
/// record; version rows remain append-only. The route state owns the currently
/// active version, rollback pointer, and current server-owned policy.
/// </summary>
public sealed record ContentPublicationRouteState
{
    /// <summary>Owning publication id.</summary>
    [JsonPropertyName("publicationId")]
    public required string PublicationId { get; init; }

    /// <summary>Stable, unique route slug.</summary>
    [JsonPropertyName("routeSlug")]
    public required string RouteSlug { get; init; }

    /// <summary>Canonical public route path.</summary>
    [JsonPropertyName("routePath")]
    public required string RoutePath { get; init; }

    /// <summary>Artifact kind.</summary>
    [JsonPropertyName("kind")]
    public required ContentPublicationKind Kind { get; init; }

    /// <summary>Currently active version id.</summary>
    [JsonPropertyName("activeVersionId")]
    public required string ActiveVersionId { get; init; }

    /// <summary>Currently active revision.</summary>
    [JsonPropertyName("activeRevision")]
    public required long ActiveRevision { get; init; }

    /// <summary>Previously active version id, when the pointer has moved at least once.</summary>
    [JsonPropertyName("previousVersionId")]
    public string? PreviousVersionId { get; init; }

    /// <summary>Target version id of the most recent rollback, when applicable.</summary>
    [JsonPropertyName("rollbackTargetVersionId")]
    public string? RollbackTargetVersionId { get; init; }

    /// <summary>Lifecycle state of the route pointer.</summary>
    [JsonPropertyName("lifecycle")]
    public ContentPublicationLifecycle Lifecycle { get; init; } = ContentPublicationLifecycle.Active;

    /// <summary>Current server-owned policy.</summary>
    [JsonPropertyName("policy")]
    public required ContentPublicationPolicy Policy { get; init; }

    /// <summary>Monotonic generation used for optimistic concurrency.</summary>
    [JsonPropertyName("generation")]
    public required long Generation { get; init; }

    /// <summary>Opaque etag for optimistic concurrency on route-state writes.</summary>
    [JsonPropertyName("etag")]
    public required string Etag { get; init; }

    /// <summary>Actor that last updated the route state.</summary>
    [JsonPropertyName("updatedBy")]
    public required string UpdatedBy { get; init; }

    /// <summary>Last route-state update time.</summary>
    [JsonPropertyName("updatedAt")]
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Route creation time.</summary>
    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// An append-only audit/event record for a publication operation.
/// </summary>
public sealed record ContentPublicationEvent
{
    /// <summary>Stable event id.</summary>
    [JsonPropertyName("eventId")]
    public required string EventId { get; init; }

    /// <summary>Owning publication id.</summary>
    [JsonPropertyName("publicationId")]
    public required string PublicationId { get; init; }

    /// <summary>Monotonic sequence assigned by the store on append (store-owned ordering).</summary>
    [JsonPropertyName("sequence")]
    public long Sequence { get; init; }

    /// <summary>Operation that produced the event.</summary>
    [JsonPropertyName("operation")]
    public required ContentPublicationOperation Operation { get; init; }

    /// <summary>Version id associated with the operation, when applicable.</summary>
    [JsonPropertyName("versionId")]
    public string? VersionId { get; init; }

    /// <summary>Revision associated with the operation, when applicable.</summary>
    [JsonPropertyName("revision")]
    public long? Revision { get; init; }

    /// <summary>Route slug at the time of the operation.</summary>
    [JsonPropertyName("routeSlug")]
    public required string RouteSlug { get; init; }

    /// <summary>Actor that performed the operation.</summary>
    [JsonPropertyName("actor")]
    public required string Actor { get; init; }

    /// <summary>Optional correlation id.</summary>
    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    /// <summary>Optional pre-sanitized detail string.</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    /// <summary>Event time.</summary>
    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Aggregate detail returned by the authenticated detail endpoint: route state plus
/// the publication's immutable versions (newest first, bounded).
/// </summary>
public sealed record ContentPublicationDetail
{
    /// <summary>Current route state.</summary>
    [JsonPropertyName("route")]
    public required ContentPublicationRouteState Route { get; init; }

    /// <summary>Immutable versions, newest first.</summary>
    [JsonPropertyName("versions")]
    public IReadOnlyList<ContentPublicationVersion> Versions { get; init; } = [];
}

/// <summary>
/// Client-safe projection of a published artifact returned by public route reads.
/// Excludes server-only policy internals (role lists, public-link hashes) but
/// surfaces the embed contract the client needs.
/// </summary>
public sealed record PublishedArtifactView
{
    /// <summary>Publication id.</summary>
    [JsonPropertyName("publicationId")]
    public required string PublicationId { get; init; }

    /// <summary>Route slug.</summary>
    [JsonPropertyName("routeSlug")]
    public required string RouteSlug { get; init; }

    /// <summary>Artifact kind.</summary>
    [JsonPropertyName("kind")]
    public required ContentPublicationKind Kind { get; init; }

    /// <summary>Resolved revision.</summary>
    [JsonPropertyName("revision")]
    public required long Revision { get; init; }

    /// <summary>Resolved version id.</summary>
    [JsonPropertyName("versionId")]
    public required string VersionId { get; init; }

    /// <summary>Display title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Visibility scope.</summary>
    [JsonPropertyName("visibility")]
    public required ContentPublicationVisibility Visibility { get; init; }

    /// <summary>CRS-declared default view bbox, when applicable.</summary>
    [JsonPropertyName("defaultViewBbox")]
    public ContentPublicationBbox? DefaultViewBbox { get; init; }

    /// <summary>Whether the artifact may be embedded.</summary>
    [JsonPropertyName("embeddable")]
    public bool Embeddable { get; init; }

    /// <summary>Allowed embed origins (client needs this to render embeds).</summary>
    [JsonPropertyName("allowedEmbedOrigins")]
    public IReadOnlyList<string>? AllowedEmbedOrigins { get; init; }

    /// <summary>Allowed frame ancestors.</summary>
    [JsonPropertyName("frameAncestors")]
    public IReadOnlyList<string>? FrameAncestors { get; init; }

    /// <summary>Generated-app manifest id for the resolved version.</summary>
    [JsonPropertyName("appManifestId")]
    public string? AppManifestId { get; init; }

    /// <summary>Generated-app bundle artifact id for the resolved version.</summary>
    [JsonPropertyName("appBundleArtifactId")]
    public string? AppBundleArtifactId { get; init; }

    /// <summary>Content hash of the resolved version.</summary>
    [JsonPropertyName("contentHash")]
    public string? ContentHash { get; init; }

    /// <summary>Dependency references; only populated when explicitly expanded.</summary>
    [JsonPropertyName("dependencies")]
    public IReadOnlyList<ContentPublicationDependencyRef>? Dependencies { get; init; }

    /// <summary>Time the resolved version was created.</summary>
    [JsonPropertyName("updatedAt")]
    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Response for a policy update. Returns the updated route state plus, when a public
/// link was created, its stable id and the raw token exactly once (the raw token is
/// never persisted or returned again).
/// </summary>
public sealed record ContentPublicationPolicyUpdateResponse
{
    /// <summary>Updated route state.</summary>
    [JsonPropertyName("route")]
    public required ContentPublicationRouteState Route { get; init; }

    /// <summary>Stable id of a public link created by this update, if any.</summary>
    [JsonPropertyName("createdPublicLinkId")]
    public string? CreatedPublicLinkId { get; init; }

    /// <summary>Raw token for a created public link, returned exactly once.</summary>
    [JsonPropertyName("createdPublicLinkToken")]
    public string? CreatedPublicLinkToken { get; init; }
}

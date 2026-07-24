// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Server.Features.Studio.Models;

/// <summary>
/// Request body for creating a mutable Studio package draft.
/// </summary>
public sealed class CreateStudioPackageDraftRequest
{
    /// <summary>Optional existing content item id; omitted to create a new item.</summary>
    [JsonPropertyName("itemId")]
    public Guid? ItemId { get; init; }

    /// <summary>Machine-friendly package key.</summary>
    [Required]
    [StringLength(200, MinimumLength = 1)]
    [JsonPropertyName("packageKey")]
    public required string PackageKey { get; init; }

    /// <summary>Workspace identifier.</summary>
    [StringLength(200)]
    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; init; }

    /// <summary>Owner principal identifier.</summary>
    [StringLength(200)]
    [JsonPropertyName("ownerId")]
    public string? OwnerId { get; init; }

    /// <summary>Package envelope.</summary>
    [Required]
    [JsonPropertyName("envelope")]
    public required StudioPackageEnvelope Envelope { get; init; }
}

/// <summary>
/// Request body for updating a mutable Studio package draft.
/// </summary>
public sealed class UpdateStudioPackageDraftRequest
{
    /// <summary>Machine-friendly package key.</summary>
    [Required]
    [StringLength(200, MinimumLength = 1)]
    [JsonPropertyName("packageKey")]
    public required string PackageKey { get; init; }

    /// <summary>Workspace identifier.</summary>
    [StringLength(200)]
    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; init; }

    /// <summary>Owner principal identifier.</summary>
    [StringLength(200)]
    [JsonPropertyName("ownerId")]
    public string? OwnerId { get; init; }

    /// <summary>Package envelope.</summary>
    [Required]
    [JsonPropertyName("envelope")]
    public required StudioPackageEnvelope Envelope { get; init; }

    /// <summary>Expected draft generation for optimistic concurrency.</summary>
    [JsonPropertyName("generation")]
    public long? Generation { get; init; }
}

/// <summary>
/// Request body for saving a draft as an immutable content version.
/// </summary>
public sealed class SaveStudioContentVersionRequest
{
    /// <summary>Optional author change note.</summary>
    [StringLength(1000)]
    [JsonPropertyName("changeNote")]
    public string? ChangeNote { get; init; }
}

/// <summary>
/// Request body for comparing two immutable content versions.
/// </summary>
public sealed class CompareStudioContentVersionsRequest
{
    /// <summary>Left-side version identifier.</summary>
    [Required]
    [JsonPropertyName("leftVersionId")]
    public required Guid LeftVersionId { get; init; }

    /// <summary>Right-side version identifier.</summary>
    [Required]
    [JsonPropertyName("rightVersionId")]
    public required Guid RightVersionId { get; init; }
}

/// <summary>
/// Request body for creating a publication request.
/// </summary>
public sealed class CreateStudioPublicationRequest
{
    /// <summary>Optional publication intent override.</summary>
    [JsonPropertyName("intent")]
    public StudioPublicationIntent? Intent { get; init; }

    /// <summary>Optional acknowledgement for validation warnings.</summary>
    [StringLength(1000)]
    [JsonPropertyName("warningAcknowledgement")]
    public string? WarningAcknowledgement { get; init; }
}

/// <summary>
/// Request body for rolling a content item pointer back to an immutable version.
/// </summary>
public sealed class CreateStudioRollbackRequest
{
    /// <summary>Version identifier selected as the rollback target.</summary>
    [Required]
    [JsonPropertyName("targetVersionId")]
    public required Guid TargetVersionId { get; init; }

    /// <summary>Pointer to update.</summary>
    [JsonPropertyName("pointer")]
    public StudioRollbackPointer Target { get; init; } = StudioRollbackPointer.Current;

    /// <summary>Optional reason supplied by the actor.</summary>
    [StringLength(1000)]
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>
/// Response body for listing immutable content versions.
/// </summary>
public sealed class StudioContentVersionListResponse
{
    /// <summary>Content item identifier.</summary>
    [JsonPropertyName("itemId")]
    public required Guid ItemId { get; init; }

    /// <summary>Immutable versions ordered by version number.</summary>
    [JsonPropertyName("versions")]
    public required IReadOnlyList<StudioContentVersion> Versions { get; init; }
}

/// <summary>
/// Publication-registry lifecycle badge joined onto a Studio content item list row. Populated
/// when the Content Publication Registry has a version referencing the item (see
/// <c>docs/internal/admin-api/content-publication-registry.md</c>); omitted otherwise.
/// </summary>
public sealed class StudioContentItemPublicationBadge
{
    /// <summary>Publication identifier.</summary>
    [JsonPropertyName("publicationId")]
    public required string PublicationId { get; init; }

    /// <summary>Route slug the publication resolves under.</summary>
    [JsonPropertyName("routeSlug")]
    public required string RouteSlug { get; init; }

    /// <summary>Canonical public route path.</summary>
    [JsonPropertyName("routePath")]
    public required string RoutePath { get; init; }

    /// <summary>Route lifecycle: <c>active</c>, <c>suspended</c>, or <c>archived</c>.</summary>
    [JsonPropertyName("lifecycle")]
    public required string Lifecycle { get; init; }

    /// <summary>Currently active revision.</summary>
    [JsonPropertyName("activeRevision")]
    public required long ActiveRevision { get; init; }

    /// <summary>Timestamp the route pointer was last updated.</summary>
    [JsonPropertyName("updatedAt")]
    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// One row in a Studio content item listing, with an optional publication-registry lifecycle
/// badge (REQ-004).
/// </summary>
public sealed class StudioContentItemListRow
{
    /// <summary>Content item identifier.</summary>
    [JsonPropertyName("itemId")]
    public required Guid ItemId { get; init; }

    /// <summary>Machine-friendly package key.</summary>
    [JsonPropertyName("packageKey")]
    public required string PackageKey { get; init; }

    /// <summary>Workspace identifier.</summary>
    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; init; }

    /// <summary>Package family.</summary>
    [JsonPropertyName("family")]
    public required StudioPackageFamily Family { get; init; }

    /// <summary>Derived lifecycle state (draft, current, or published).</summary>
    [JsonPropertyName("state")]
    public required StudioContentItemState State { get; init; }

    /// <summary>Current version identifier.</summary>
    [JsonPropertyName("currentVersionId")]
    public Guid? CurrentVersionId { get; init; }

    /// <summary>Published version identifier.</summary>
    [JsonPropertyName("publishedVersionId")]
    public Guid? PublishedVersionId { get; init; }

    /// <summary>Owner principal identifier (honua-server#3001).</summary>
    [JsonPropertyName("ownerId")]
    public string? OwnerId { get; init; }

    /// <summary>Identifier of the actor that created the item.</summary>
    [JsonPropertyName("createdBy")]
    public string? CreatedBy { get; init; }

    /// <summary>Identifier of the actor that last updated the item.</summary>
    [JsonPropertyName("updatedBy")]
    public string? UpdatedBy { get; init; }

    /// <summary>Timestamp when the item was created.</summary>
    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Timestamp when the item was last updated.</summary>
    [JsonPropertyName("updatedAt")]
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Publication-registry lifecycle badge, when the item has a joinable publication.</summary>
    [JsonPropertyName("publication")]
    public StudioContentItemPublicationBadge? Publication { get; init; }
}

/// <summary>
/// Response body for listing Studio content items.
/// </summary>
public sealed class StudioContentItemListResponse
{
    /// <summary>Items in this page.</summary>
    [JsonPropertyName("items")]
    public required IReadOnlyList<StudioContentItemListRow> Items { get; init; }

    /// <summary>Total items matching the query across all pages.</summary>
    [JsonPropertyName("total")]
    public required int Total { get; init; }

    /// <summary>Opaque cursor for the next page, or null when no more items remain.</summary>
    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; init; }
}

/// <summary>
/// Response body for listing mutable Studio package drafts.
/// </summary>
public sealed class StudioPackageDraftListResponse
{
    /// <summary>Drafts in this page.</summary>
    [JsonPropertyName("items")]
    public required IReadOnlyList<StudioPackageDraft> Items { get; init; }

    /// <summary>Total drafts matching the query across all pages.</summary>
    [JsonPropertyName("total")]
    public required int Total { get; init; }

    /// <summary>Opaque cursor for the next page, or null when no more drafts remain.</summary>
    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; init; }
}

/// <summary>
/// Response body for a stored Studio deliverable export (returned when the artifact is persisted to share storage).
/// </summary>
public sealed class StudioDeliverableExportResponse
{
    /// <summary>Content item identifier the deliverable was rendered from.</summary>
    [JsonPropertyName("itemId")]
    public required Guid ItemId { get; init; }

    /// <summary>Deliverable kind (map, dashboard, or report).</summary>
    [JsonPropertyName("kind")]
    public required StudioPackageFamily Kind { get; init; }

    /// <summary>Export format (pdf or png).</summary>
    [JsonPropertyName("format")]
    public required string Format { get; init; }

    /// <summary>Suggested download file name including extension.</summary>
    [JsonPropertyName("fileName")]
    public required string FileName { get; init; }

    /// <summary>MIME content type of the rendered artifact.</summary>
    [JsonPropertyName("contentType")]
    public required string ContentType { get; init; }

    /// <summary>Rendered artifact size in bytes.</summary>
    [JsonPropertyName("sizeBytes")]
    public required int SizeBytes { get; init; }

    /// <summary>Stored share-artifact URL, when the deliverable was persisted to cloud storage.</summary>
    [JsonPropertyName("artifactUrl")]
    public string? ArtifactUrl { get; init; }
}

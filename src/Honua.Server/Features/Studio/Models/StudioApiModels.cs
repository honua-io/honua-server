// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.ComponentModel;
using System.Text.Json.Serialization;
using Honua.Core.Features.Studio.Domain;

namespace Honua.Server.Features.Studio.Models;

public sealed class CreateStudioPackageDraftRequest
{
    [JsonPropertyName("itemId")]
    public Guid? ItemId { get; init; }

    [Required]
    [StringLength(200, MinimumLength = 1)]
    [JsonPropertyName("packageKey")]
    public required string PackageKey { get; init; }

    [StringLength(200)]
    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; init; }

    [StringLength(200)]
    [Description("Admin-only owner principal identifier. Non-admin callers must omit this field.")]
    [JsonPropertyName("ownerId")]
    public string? OwnerId { get; init; }

    [Required]
    [JsonPropertyName("envelope")]
    public required StudioPackageEnvelope Envelope { get; init; }
}

public sealed record StudioPublishedSource
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("protocol")]
    public string Protocol { get; init; } = "geoservices-feature-service";

    [JsonPropertyName("geometryType")]
    public required string GeometryType { get; init; }

    [JsonPropertyName("locator")]
    public required StudioPublishedSourceLocator Locator { get; init; }
}

// The locator is never a credential or a grant.
public sealed record StudioPublishedSourceLocator
{
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("serviceId")]
    public required string ServiceId { get; init; }

    [JsonPropertyName("layerId")]
    public required int LayerId { get; init; }
}

public sealed class UpdateStudioPackageDraftRequest
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    [JsonPropertyName("packageKey")]
    public required string PackageKey { get; init; }

    [StringLength(200)]
    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; init; }

    [StringLength(200)]
    [Description("Admin-only owner principal identifier. Non-admin callers must omit this field.")]
    [JsonPropertyName("ownerId")]
    public string? OwnerId { get; init; }

    [Required]
    [JsonPropertyName("envelope")]
    public required StudioPackageEnvelope Envelope { get; init; }

    [JsonPropertyName("generation")]
    public long? Generation { get; init; }
}

public sealed class SaveStudioContentVersionRequest
{
    [StringLength(1000)]
    [JsonPropertyName("changeNote")]
    public string? ChangeNote { get; init; }
}

public sealed class CompareStudioContentVersionsRequest
{
    [Required]
    [JsonPropertyName("leftVersionId")]
    public required Guid LeftVersionId { get; init; }

    [Required]
    [JsonPropertyName("rightVersionId")]
    public required Guid RightVersionId { get; init; }
}

public sealed class CreateStudioPublicationRequest
{
    [JsonPropertyName("intent")]
    public StudioPublicationIntent? Intent { get; init; }

    [StringLength(1000)]
    [JsonPropertyName("warningAcknowledgement")]
    public string? WarningAcknowledgement { get; init; }
}

public sealed class CreateStudioRollbackRequest
{
    [Required]
    [JsonPropertyName("targetVersionId")]
    public required Guid TargetVersionId { get; init; }

    [JsonPropertyName("pointer")]
    public StudioRollbackPointer Target { get; init; } = StudioRollbackPointer.Current;

    [StringLength(1000)]
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed class StudioContentVersionListResponse
{
    [JsonPropertyName("itemId")]
    public required Guid ItemId { get; init; }

    [JsonPropertyName("versions")]
    public required IReadOnlyList<StudioContentVersion> Versions { get; init; }
}

public sealed class StudioContentItemPublicationBadge
{
    [JsonPropertyName("publicationId")]
    public required string PublicationId { get; init; }

    [JsonPropertyName("routeSlug")]
    public required string RouteSlug { get; init; }

    [JsonPropertyName("routePath")]
    public required string RoutePath { get; init; }

    [JsonPropertyName("lifecycle")]
    public required string Lifecycle { get; init; }

    [JsonPropertyName("activeRevision")]
    public required long ActiveRevision { get; init; }

    [JsonPropertyName("updatedAt")]
    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed class StudioContentItemListRow
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

    [JsonPropertyName("createdBy")]
    public string? CreatedBy { get; init; }

    [JsonPropertyName("updatedBy")]
    public string? UpdatedBy { get; init; }

    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public required DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("publication")]
    public StudioContentItemPublicationBadge? Publication { get; init; }
}

public sealed class StudioContentItemListResponse
{
    [JsonPropertyName("items")]
    public required IReadOnlyList<StudioContentItemListRow> Items { get; init; }

    [JsonPropertyName("total")]
    public required int Total { get; init; }

    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; init; }
}

public sealed class StudioPackageDraftListResponse
{
    [JsonPropertyName("items")]
    public required IReadOnlyList<StudioPackageDraft> Items { get; init; }

    [JsonPropertyName("total")]
    public required int Total { get; init; }

    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; init; }
}

public sealed class StudioDeliverableExportResponse
{
    [JsonPropertyName("itemId")]
    public required Guid ItemId { get; init; }

    [JsonPropertyName("kind")]
    public required StudioPackageFamily Kind { get; init; }

    [JsonPropertyName("format")]
    public required string Format { get; init; }

    [JsonPropertyName("fileName")]
    public required string FileName { get; init; }

    [JsonPropertyName("contentType")]
    public required string ContentType { get; init; }

    [JsonPropertyName("sizeBytes")]
    public required int SizeBytes { get; init; }

    [JsonPropertyName("artifactUrl")]
    public string? ArtifactUrl { get; init; }
}

public sealed class StudioPublishedArtifact
{
    [JsonPropertyName("route")]
    public required string Route { get; init; }

    [JsonPropertyName("visibility")]
    public string? Visibility { get; init; }

    [JsonPropertyName("publicationId")]
    public required Guid PublicationId { get; init; }

    [JsonPropertyName("itemId")]
    public required Guid ItemId { get; init; }

    [JsonPropertyName("versionId")]
    public required Guid VersionId { get; init; }

    [JsonPropertyName("versionNumber")]
    public required int VersionNumber { get; init; }

    [JsonPropertyName("packageKey")]
    public required string PackageKey { get; init; }

    [JsonPropertyName("family")]
    public required StudioPackageFamily Family { get; init; }

    [JsonPropertyName("contentHash")]
    public required string ContentHash { get; init; }

    // The hash input is absent when the server cannot supply bytes that hash to the advertised digest.
    [JsonPropertyName("contentHashInput")]
    public string? ContentHashInput { get; init; }

    [JsonPropertyName("publishedAt")]
    public required DateTimeOffset PublishedAt { get; init; }

    [JsonPropertyName("sourceCatalog")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<StudioPublishedSource>? SourceCatalog { get; init; }

    [JsonPropertyName("envelope")]
    public required StudioPackageEnvelope Envelope { get; init; }
}

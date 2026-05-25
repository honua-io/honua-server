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

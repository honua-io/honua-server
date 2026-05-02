// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Lightweight summary projection used in list responses.
/// </summary>
public sealed class SceneDatasetSummary
{
    /// <summary>Database primary key.</summary>
    public Guid DatasetId { get; set; }

    /// <summary>URL slug.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional description.</summary>
    public string? Description { get; set; }

    /// <summary>Source kind (hosted_tiles, terrain).</summary>
    public string DatasetType { get; set; } = "hosted_tiles";

    /// <summary>Whether the dataset is publicly readable.</summary>
    public bool IsPublic { get; set; }

    /// <summary>Whether the dataset requires authentication.</summary>
    public bool RequiresAuth { get; set; }

    /// <summary>Lifecycle state (active, inactive, validation_failed).</summary>
    public string Status { get; set; } = "active";

    /// <summary>Revision counter; bumped on each update.</summary>
    public int Revision { get; set; }

    /// <summary>Creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Last-update timestamp.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>
/// Detailed projection used by GET-by-id, register, and update responses.
/// </summary>
public sealed class SceneDatasetDetail
{
    /// <summary>Database primary key.</summary>
    public Guid DatasetId { get; set; }

    /// <summary>URL slug.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional description.</summary>
    public string? Description { get; set; }

    /// <summary>Source kind (hosted_tiles, terrain).</summary>
    public string DatasetType { get; set; } = "hosted_tiles";

    /// <summary>Filesystem path containing the root tileset document.</summary>
    public string AssetRoot { get; set; } = string.Empty;

    /// <summary>Filename of the root tileset document.</summary>
    public string TilesetFileName { get; set; } = "tileset.json";

    /// <summary>Optional WGS-84 axis-aligned bounding box.</summary>
    public SceneExtentDto? Extent { get; set; }

    /// <summary>Optional CRS authority token.</summary>
    public string? Crs { get; set; }

    /// <summary>Cache directives.</summary>
    public SceneCachePolicyDto CachePolicy { get; set; } = new();

    /// <summary>Optional edition gate slug.</summary>
    public string? EditionGate { get; set; }

    /// <summary>Whether the dataset requires authentication.</summary>
    public bool RequiresAuth { get; set; }

    /// <summary>Whether the dataset is publicly readable.</summary>
    public bool IsPublic { get; set; }

    /// <summary>Allowed roles for protected datasets.</summary>
    public string[]? AllowedRoles { get; set; }

    /// <summary>Lifecycle state.</summary>
    public string Status { get; set; } = "active";

    /// <summary>Optional non-fatal validation message.</summary>
    public string? ValidationMessage { get; set; }

    /// <summary>Revision counter.</summary>
    public int Revision { get; set; }

    /// <summary>Creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Identity that created the entry.</summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>Last-update timestamp.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>
/// Returned by the resolve endpoint with snippet output for known consumers.
/// </summary>
public sealed class SceneDatasetResolveResponse
{
    /// <summary>Database primary key.</summary>
    public Guid DatasetId { get; set; }

    /// <summary>URL slug.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Absolute URL of the root tileset document for client consumption.</summary>
    public string TilesetUrl { get; set; } = string.Empty;

    /// <summary>Optional WGS-84 bounding box.</summary>
    public SceneExtentDto? Extent { get; set; }

    /// <summary>Optional CRS authority token.</summary>
    public string? Crs { get; set; }

    /// <summary>Cache directives that the hosted serving path emits.</summary>
    public SceneCachePolicyDto CachePolicy { get; set; } = new();

    /// <summary>Whether the dataset is publicly readable.</summary>
    public bool IsPublic { get; set; }

    /// <summary>Whether the dataset requires authentication.</summary>
    public bool RequiresAuth { get; set; }

    /// <summary>Lifecycle state.</summary>
    public string Status { get; set; } = "active";

    /// <summary>CesiumJS initialization snippet.</summary>
    public string CesiumJsSnippet { get; set; } = string.Empty;

    /// <summary>Honua scene custom-element snippet.</summary>
    public string HonuaSceneSnippet { get; set; } = string.Empty;
}

/// <summary>
/// Wire-format DTO for <see cref="Honua.Core.Features.Scene.Domain.SceneExtent"/>.
/// </summary>
public sealed class SceneExtentDto
{
    /// <summary>Minimum longitude.</summary>
    public double XMin { get; set; }
    /// <summary>Minimum latitude.</summary>
    public double YMin { get; set; }
    /// <summary>Maximum longitude.</summary>
    public double XMax { get; set; }
    /// <summary>Maximum latitude.</summary>
    public double YMax { get; set; }
}

/// <summary>
/// Wire-format DTO for <see cref="Honua.Core.Features.Scene.Domain.SceneCachePolicy"/>.
/// </summary>
public sealed class SceneCachePolicyDto
{
    /// <summary>Recommended <c>max-age</c> for the Cache-Control header (0–86400).</summary>
    public int MaxAgeSeconds { get; set; } = 3600;

    /// <summary>Whether downstream caches must not store the response.</summary>
    public bool NoStore { get; set; }
}

/// <summary>
/// Request payload accepted by POST /admin/scenes.
/// </summary>
public sealed class RegisterSceneDatasetRequest
{
    /// <summary>URL slug. Required.</summary>
    [Required]
    public string Id { get; set; } = string.Empty;

    /// <summary>Display name. Required.</summary>
    [Required]
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional description.</summary>
    public string? Description { get; set; }

    /// <summary>Filesystem path containing tileset.json. Required.</summary>
    [Required]
    public string AssetRoot { get; set; } = string.Empty;

    /// <summary>Filename of the root tileset document.</summary>
    public string? TilesetFileName { get; set; }

    /// <summary>Source kind (<c>hosted_tiles</c> or <c>terrain</c>).</summary>
    public string? DatasetType { get; set; }

    /// <summary>Optional WGS-84 bounding box.</summary>
    public SceneExtentDto? Extent { get; set; }

    /// <summary>Optional CRS authority token.</summary>
    public string? Crs { get; set; }

    /// <summary>Optional cache directives.</summary>
    public SceneCachePolicyDto? CachePolicy { get; set; }

    /// <summary>Optional edition gate slug.</summary>
    public string? EditionGate { get; set; }

    /// <summary>Whether the dataset requires authentication.</summary>
    public bool RequiresAuth { get; set; }

    /// <summary>Whether the dataset is publicly readable. Defaults to true.</summary>
    public bool IsPublic { get; set; } = true;

    /// <summary>Allowed roles for protected datasets.</summary>
    public string[]? AllowedRoles { get; set; }
}

/// <summary>
/// Request payload accepted by PUT /admin/scenes/{id}. All fields optional;
/// omitted fields preserve their previous value.
/// </summary>
public sealed class UpdateSceneDatasetRequest
{
    /// <summary>Updated display name.</summary>
    public string? Name { get; set; }

    /// <summary>Updated description.</summary>
    public string? Description { get; set; }

    /// <summary>Updated asset root path.</summary>
    public string? AssetRoot { get; set; }

    /// <summary>Updated tileset filename.</summary>
    public string? TilesetFileName { get; set; }

    /// <summary>Updated dataset type.</summary>
    public string? DatasetType { get; set; }

    /// <summary>Updated extent. Pass an explicit empty extent to clear (server checks).</summary>
    public SceneExtentDto? Extent { get; set; }

    /// <summary>Pass true to clear the extent.</summary>
    public bool? ClearExtent { get; set; }

    /// <summary>Updated CRS authority token.</summary>
    public string? Crs { get; set; }

    /// <summary>Pass true to clear the CRS.</summary>
    public bool? ClearCrs { get; set; }

    /// <summary>Updated cache directives.</summary>
    public SceneCachePolicyDto? CachePolicy { get; set; }

    /// <summary>Updated edition gate slug.</summary>
    public string? EditionGate { get; set; }

    /// <summary>Pass true to clear the edition gate.</summary>
    public bool? ClearEditionGate { get; set; }

    /// <summary>Updated authentication requirement.</summary>
    public bool? RequiresAuth { get; set; }

    /// <summary>Updated public flag.</summary>
    public bool? IsPublic { get; set; }

    /// <summary>Updated allowed roles.</summary>
    public string[]? AllowedRoles { get; set; }

    /// <summary>Pass true to clear the allowed-roles list.</summary>
    public bool? ClearAllowedRoles { get; set; }
}

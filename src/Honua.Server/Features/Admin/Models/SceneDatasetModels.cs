// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Admin.Models;

public sealed class SceneDatasetSummary
{
    public Guid DatasetId { get; set; }

    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string DatasetType { get; set; } = "hosted_tiles";

    public bool IsPublic { get; set; }

    public bool RequiresAuth { get; set; }

    public string Status { get; set; } = "active";

    public int Revision { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class SceneDatasetDetail
{
    public Guid DatasetId { get; set; }

    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string DatasetType { get; set; } = "hosted_tiles";

    public string AssetRoot { get; set; } = string.Empty;

    public string TilesetFileName { get; set; } = "tileset.json";

    public SceneExtentDto? Extent { get; set; }

    public string? Crs { get; set; }

    public SceneCachePolicyDto CachePolicy { get; set; } = new();

    public string? EditionGate { get; set; }

    public bool RequiresAuth { get; set; }

    public bool IsPublic { get; set; }

    public string[]? AllowedRoles { get; set; }

    public string Status { get; set; } = "active";

    public string? ValidationMessage { get; set; }

    public int Revision { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class SceneDatasetResolveResponse
{
    public Guid DatasetId { get; set; }

    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string TilesetUrl { get; set; } = string.Empty;

    public SceneExtentDto? Extent { get; set; }

    public string? Crs { get; set; }

    public SceneCachePolicyDto CachePolicy { get; set; } = new();

    public bool IsPublic { get; set; }

    public bool RequiresAuth { get; set; }

    public string Status { get; set; } = "active";

    public string CesiumJsSnippet { get; set; } = string.Empty;

    public string HonuaSceneSnippet { get; set; } = string.Empty;
}

public sealed class SceneExtentDto
{
    public double? XMin { get; set; }
    public double? YMin { get; set; }
    public double? XMax { get; set; }
    public double? YMax { get; set; }
}

public sealed class SceneCachePolicyDto
{
    public int MaxAgeSeconds { get; set; } = 3600;

    public bool NoStore { get; set; }
}

public sealed class RegisterSceneDatasetRequest
{
    [Required]
    public string Id { get; set; } = string.Empty;

    [Required]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    [Required]
    public string AssetRoot { get; set; } = string.Empty;

    public string? TilesetFileName { get; set; }

    public string? DatasetType { get; set; }

    public SceneExtentDto? Extent { get; set; }

    public string? Crs { get; set; }

    public SceneCachePolicyDto? CachePolicy { get; set; }

    public string? EditionGate { get; set; }

    public bool RequiresAuth { get; set; }

    public bool IsPublic { get; set; } = true;

    public string[]? AllowedRoles { get; set; }
}

public sealed class UpdateSceneDatasetRequest
{
    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? AssetRoot { get; set; }

    public string? TilesetFileName { get; set; }

    public string? DatasetType { get; set; }

    public SceneExtentDto? Extent { get; set; }

    public bool? ClearExtent { get; set; }

    public string? Crs { get; set; }

    public bool? ClearCrs { get; set; }

    public SceneCachePolicyDto? CachePolicy { get; set; }

    public string? EditionGate { get; set; }

    public bool? ClearEditionGate { get; set; }

    public bool? RequiresAuth { get; set; }

    public bool? IsPublic { get; set; }

    public string[]? AllowedRoles { get; set; }

    public bool? ClearAllowedRoles { get; set; }
}

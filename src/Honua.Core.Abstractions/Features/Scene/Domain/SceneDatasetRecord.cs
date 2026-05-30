// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Scene.Domain;

/// <summary>
/// Full-fidelity persistence record for a registered scene dataset.
/// </summary>
/// <remarks>
/// <see cref="SceneDataset"/> remains the lean read model used by the hosted
/// 3D Tiles serving path (#837). Admin lifecycle work uses
/// <see cref="SceneDatasetRecord"/>, which carries the additional metadata
/// (extent, cache policy, edition gate, auth requirements, audit fields)
/// captured during registration.
/// </remarks>
public sealed record SceneDatasetRecord
{
    /// <summary>
    /// Database primary key. Stable across slug renames.
    /// </summary>
    public Guid DatasetId { get; init; }

    /// <summary>
    /// URL slug; mirrors <see cref="SceneDataset.Id"/>.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Optional description text.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Filesystem path that contains the root tileset document. Mirrors
    /// <see cref="SceneDataset.AssetRoot"/>.
    /// </summary>
    public required string AssetRoot { get; init; }

    /// <summary>
    /// Filename of the root tileset document under <see cref="AssetRoot"/>.
    /// Defaults to <c>tileset.json</c> per the OGC 3D Tiles specification.
    /// </summary>
    public string TilesetFileName { get; init; } = "tileset.json";

    /// <summary>
    /// Source kind for the dataset. Defaults to hosted 3D Tiles.
    /// </summary>
    public SceneDatasetType DatasetType { get; init; } = SceneDatasetType.HostedTiles;

    /// <summary>
    /// Optional WGS-84 bounding box for the dataset. Returned to clients via
    /// resolve metadata so they can position their viewer.
    /// </summary>
    public SceneExtent? Extent { get; init; }

    /// <summary>
    /// Optional CRS authority token (e.g. <c>EPSG:4979</c>). Geodesy
    /// interpretation is the caller's responsibility.
    /// </summary>
    public string? Crs { get; init; }

    /// <summary>
    /// Cache directives for hosted serving.
    /// </summary>
    public SceneCachePolicy CachePolicy { get; init; } = SceneCachePolicy.Default;

    /// <summary>
    /// Optional edition gate slug (e.g. <c>pro</c>, <c>enterprise</c>) used by
    /// the licensing layer to authorize access.
    /// </summary>
    public string? EditionGate { get; init; }

    /// <summary>
    /// When true, the dataset requires an authenticated principal. The
    /// hosted serving path will refuse anonymous access.
    /// </summary>
    public bool RequiresAuth { get; init; }

    /// <summary>
    /// When true, the dataset is publicly readable. Validation rejects
    /// records where both <see cref="IsPublic"/> and <see cref="RequiresAuth"/>
    /// are true.
    /// </summary>
    public bool IsPublic { get; init; } = true;

    /// <summary>
    /// Roles permitted to read a protected dataset. Forwarded to the
    /// hosted-serving access policy as <c>AllowedRoles</c>. Ignored for
    /// public datasets.
    /// </summary>
    public IReadOnlyList<string>? AllowedRoles { get; init; }

    /// <summary>
    /// Lifecycle state.
    /// </summary>
    public SceneDatasetStatus Status { get; init; } = SceneDatasetStatus.Active;

    /// <summary>
    /// Optional human-readable validation message recorded when registration
    /// or update validation flagged a non-fatal issue.
    /// </summary>
    public string? ValidationMessage { get; init; }

    /// <summary>
    /// Monotonically increasing revision counter incremented on every update.
    /// </summary>
    public int Revision { get; init; } = 1;

    /// <summary>
    /// UTC timestamp when the registry entry was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Identity (user principal name) that created the registry entry.
    /// </summary>
    public required string CreatedBy { get; init; }

    /// <summary>
    /// UTC timestamp of the most recent update, or null if the entry has
    /// never been updated.
    /// </summary>
    public DateTimeOffset? UpdatedAt { get; init; }
}

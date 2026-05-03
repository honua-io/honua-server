// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;

namespace Honua.Core.Features.Scene.Domain;

/// <summary>
/// A hosted 3D scene dataset that exposes an OGC 3D Tiles tileset rooted at
/// <see cref="AssetRoot"/>. The asset root is the absolute filesystem prefix
/// under which <c>tileset.json</c> and all relative tile/binary/texture
/// payloads live; the server only serves files that resolve under this prefix.
/// </summary>
/// <remarks>
/// Hosted 3D Tiles datasets are structurally distinct from vector or raster
/// <see cref="LayerDefinition"/> values: they reference a tree of opaque tile
/// payloads (b3dm, i3dm, pnts, glTF) rather than queryable features or pixel
/// arrays. Keeping a dedicated model lets the registry seam evolve without
/// distorting the shared catalog model.
/// </remarks>
public sealed record SceneDataset
{
    /// <summary>
    /// Stable identifier used in scene URLs (e.g. <c>/scenes/{Id}/tileset.json</c>).
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Human-readable display name surfaced through capabilities and tooling.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Optional human-readable description of the tileset.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Absolute filesystem path that contains <c>tileset.json</c>. Asset
    /// requests are resolved relative to this root and refused if the
    /// canonicalized path leaves it.
    /// </summary>
    public required string AssetRoot { get; init; }

    /// <summary>
    /// Filename of the root tileset document under <see cref="AssetRoot"/>.
    /// Defaults to <c>tileset.json</c> per the OGC 3D Tiles specification.
    /// </summary>
    public string TilesetFileName { get; init; } = "tileset.json";

    /// <summary>
    /// Optional catalog metadata (access policy, etc.) applied to scene root
    /// and asset requests. Public scenes leave this null.
    /// </summary>
    public CatalogMetadata? Metadata { get; init; }

    /// <summary>
    /// Per-dataset cache directives surfaced to clients and shared caches.
    /// Null when the registry leaves serving caching to the global default.
    /// </summary>
    public SceneCachePolicy? CachePolicy { get; init; }
}

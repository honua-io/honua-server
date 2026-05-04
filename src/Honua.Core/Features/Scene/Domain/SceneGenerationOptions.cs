// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;

namespace Honua.Core.Features.Scene.Domain;

/// <summary>
/// Options that govern a single 3D Tiles generation job.
/// </summary>
/// <remarks>
/// The v1 pipeline targets small/medium datasets. The default
/// <see cref="MaxFeatureCount"/> of 50 000 is a hard guard rail; layers that
/// exceed it are rejected with <see cref="SceneGenerationErrorCodes.FeatureLimitExceeded"/>
/// before any tile is written. Enterprise-scale streaming and LOD
/// optimization are deliberately deferred (see #842 docs).
/// </remarks>
public sealed record SceneGenerationOptions
{
    /// <summary>
    /// Hard upper bound on the number of features included in the tileset.
    /// </summary>
    public int MaxFeatureCount { get; init; } = 50_000;

    /// <summary>
    /// Optional allowlist of attribute field names to project into the
    /// tileset's metadata schema. When empty (the default), all numeric and
    /// string attributes on the layer are included.
    /// </summary>
    public IReadOnlyList<string> IncludeAttributes { get; init; } = [];

    /// <summary>
    /// Optional per-job extrusion override. When null the executor falls
    /// back to the layer's catalog <see cref="LayerExtrusionInfo"/>.
    /// </summary>
    public LayerExtrusionInfo? ExtrusionOverride { get; init; }

    /// <summary>
    /// Recommended Cache-Control max-age applied to the generated dataset
    /// when registering it with the scene registry. Mirrors the existing
    /// <see cref="SceneCachePolicy.MaxAgeSeconds"/> contract.
    /// </summary>
    public int CacheMaxAgeSeconds { get; init; } = 3600;
}

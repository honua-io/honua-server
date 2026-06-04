// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Scene;

/// <summary>
/// Server-side configuration for the v1 3D Tiles generation pipeline.
/// Bind to the <c>SceneGeneration</c> configuration section.
/// </summary>
internal sealed class SceneGenerationServerOptions
{
    /// <summary>
    /// Configuration section name for appsettings.json binding.
    /// </summary>
    public const string SectionName = "SceneGeneration";

    /// <summary>
    /// Filesystem directory under which generated tilesets are written.
    /// Each generation job creates a child directory named after the
    /// resulting scene id. May be absolute or relative to the application
    /// content root.
    /// </summary>
    public string OutputRoot { get; set; } = "scenes-generated";

    /// <summary>
    /// Hard upper bound on features per generation job. Layers exceeding
    /// this count are rejected with <c>SCENE_FEATURE_LIMIT_EXCEEDED</c>.
    /// </summary>
    /// <remarks>
    /// Raised from the v1 single-tile 50 000 cap to 1 000 000 now that the
    /// quadtree LOD pipeline (#1200) partitions large datasets into a tile
    /// hierarchy instead of one oversized GLB.
    /// </remarks>
    public int MaxFeatureCount { get; set; } = 1_000_000;

    /// <summary>
    /// Feature-count threshold at or above which the generator switches from
    /// single-tile output to quadtree LOD partitioning (#1200). Datasets below
    /// the threshold keep the byte-stable single-tile layout.
    /// </summary>
    public int LodFeatureThreshold { get; set; } = 50_000;

    /// <summary>
    /// Maximum number of features a leaf tile may hold before the quadtree
    /// partitioner subdivides it.
    /// </summary>
    public int MaxFeaturesPerTile { get; set; } = 2_000;

    /// <summary>
    /// Hard cap on quadtree depth for the LOD partitioner.
    /// </summary>
    public int MaxLodDepth { get; set; } = 12;

    /// <summary>
    /// Number of representative features each interior LOD node carries as its
    /// coarse level of detail.
    /// </summary>
    public int InteriorSampleCount { get; set; } = 256;

    /// <summary>
    /// Generator label written into the GLB asset block for traceability.
    /// </summary>
    public string GeneratorTag { get; set; } = "honua-3dtiles-generator/1.0";
}

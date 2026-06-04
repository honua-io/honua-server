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
    public int MaxFeatureCount { get; set; } = 50_000;

    /// <summary>
    /// Generator label written into the GLB asset block for traceability.
    /// </summary>
    public string GeneratorTag { get; set; } = "honua-3dtiles-generator/1.0";
}

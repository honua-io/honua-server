// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Scene;

internal sealed class SceneGenerationServerOptions
{
    public const string SectionName = "SceneGeneration";

    public string OutputRoot { get; set; } = "scenes-generated";

    public int MaxFeatureCount { get; set; } = 1_000_000;

    public int LodFeatureThreshold { get; set; } = 50_000;

    public int MaxFeaturesPerTile { get; set; } = 2_000;

    public int MaxLodDepth { get; set; } = 12;

    public int InteriorSampleCount { get; set; } = 256;

    public string GeneratorTag { get; set; } = "honua-3dtiles-generator/1.0";
}

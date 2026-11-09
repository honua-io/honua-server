// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Scene.Domain;

public sealed record SceneGenerationSummary
{
    public int FeatureCount { get; init; }

    public int TileCount { get; init; }

    public double[] BoundingRegionDegrees { get; init; } = [0, 0, 0, 0];

    public double GeometricError { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

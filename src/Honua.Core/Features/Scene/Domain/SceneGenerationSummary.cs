// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Scene.Domain;

/// <summary>
/// Summary of a completed scene generation job. Returned by the executor
/// and surfaced through the admin API.
/// </summary>
public sealed record SceneGenerationSummary
{
    /// <summary>Number of features written to the tileset after filtering.</summary>
    public int FeatureCount { get; init; }

    /// <summary>Number of tile content files written under the scene asset root.</summary>
    public int TileCount { get; init; }

    /// <summary>Bounding region of the dataset in WGS-84 degrees: [west, south, east, north].</summary>
    public double[] BoundingRegionDegrees { get; init; } = [0, 0, 0, 0];

    /// <summary>Geometric error declared on the root tile, in meters.</summary>
    public double GeometricError { get; init; }

    /// <summary>Non-fatal warnings collected during generation (e.g., missing Z values, attribute coercions).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

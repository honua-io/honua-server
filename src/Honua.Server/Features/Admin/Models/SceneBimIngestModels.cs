// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

public sealed class CityGmlIngestResponse
{
    public string SceneId { get; set; } = string.Empty;

    public string TilesetUrl { get; set; } = string.Empty;

    public int BuildingCount { get; set; }

    public int SurfaceCount { get; set; }

    public int TileCount { get; set; }

    public string[] Disciplines { get; set; } = Array.Empty<string>();

    public double[] BoundingRegionDegrees { get; set; } = [0, 0, 0, 0];

    public string[] Warnings { get; set; } = Array.Empty<string>();
}

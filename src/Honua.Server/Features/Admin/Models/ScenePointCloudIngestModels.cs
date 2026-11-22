// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Models;

public sealed class PointCloudIngestResponse
{
    public string SceneId { get; set; } = string.Empty;

    public string TilesetUrl { get; set; } = string.Empty;

    public string? CopcUrl { get; set; }

    public string ServingFormat { get; set; } = "3d-tiles";

    public string ContentKind { get; set; } = "point-cloud";

    public int PointCount { get; set; }

    public int TileCount { get; set; }

    public bool HasColor { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double[]? BoundingRegionDegrees { get; set; }
}

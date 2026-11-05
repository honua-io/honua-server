// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Admin.Models;

public sealed class GenerateSceneRequest
{
    [Required]
    public int LayerId { get; set; }

    public string? SceneId { get; set; }

    public string? DisplayName { get; set; }

    public string? Description { get; set; }

    public string[]? IncludeAttributes { get; set; }

    public int? MaxFeatureCount { get; set; }

    public int? CacheMaxAgeSeconds { get; set; }

    public string? EditionGate { get; set; }
}

public sealed class GenerateSceneResponse
{
    public string SceneId { get; set; } = string.Empty;

    public string IntentId { get; set; } = string.Empty;

    public string ServiceId { get; set; } = string.Empty;

    public string TilesetUrl { get; set; } = string.Empty;

    public int FeatureCount { get; set; }

    public int TileCount { get; set; }

    public double[] BoundingRegionDegrees { get; set; } = [0, 0, 0, 0];

    public double GeometricError { get; set; }

    public string[] Warnings { get; set; } = Array.Empty<string>();
}

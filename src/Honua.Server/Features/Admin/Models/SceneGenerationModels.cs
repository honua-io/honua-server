// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Request payload accepted by POST /admin/scenes/generate.
/// </summary>
public sealed class GenerateSceneRequest
{
    /// <summary>Layer id to convert into a 3D Tiles tileset.</summary>
    [Required]
    public int LayerId { get; set; }

    /// <summary>Optional explicit scene id (slug). When omitted, the server derives one from the layer name and intent id.</summary>
    public string? SceneId { get; set; }

    /// <summary>Optional display name for the registered scene dataset; defaults to the layer name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Optional human-readable description.</summary>
    public string? Description { get; set; }

    /// <summary>Optional allowlist of attribute fields to include in the metadata schema.</summary>
    public string[]? IncludeAttributes { get; set; }

    /// <summary>Optional override for the v1 maximum feature count guard rail.</summary>
    public int? MaxFeatureCount { get; set; }

    /// <summary>Optional cache <c>max-age</c> applied to the registered scene dataset.</summary>
    public int? CacheMaxAgeSeconds { get; set; }

    /// <summary>Optional edition gate slug forwarded to the scene registration record.</summary>
    public string? EditionGate { get; set; }
}

/// <summary>
/// Response payload returned by POST /admin/scenes/generate.
/// </summary>
public sealed class GenerateSceneResponse
{
    /// <summary>Stable identifier used in the resulting scene URL.</summary>
    public string SceneId { get; set; } = string.Empty;

    /// <summary>Identifier of the publish intent associated with the generation job.</summary>
    public string IntentId { get; set; } = string.Empty;

    /// <summary>Identifier of the published service record created by the generation job.</summary>
    public string ServiceId { get; set; } = string.Empty;

    /// <summary>Routable URL for the generated tileset's root document.</summary>
    public string TilesetUrl { get; set; } = string.Empty;

    /// <summary>Number of features successfully written to the tileset.</summary>
    public int FeatureCount { get; set; }

    /// <summary>Number of tile content files written under the scene asset root.</summary>
    public int TileCount { get; set; }

    /// <summary>Bounding region of the dataset in WGS-84 degrees [west, south, east, north].</summary>
    public double[] BoundingRegionDegrees { get; set; } = [0, 0, 0, 0];

    /// <summary>Geometric error declared on the root tile, in meters.</summary>
    public double GeometricError { get; set; }

    /// <summary>Non-fatal warnings collected during generation.</summary>
    public string[] Warnings { get; set; } = Array.Empty<string>();
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Response payload returned by POST /api/v1/admin/scenes/ingest/citygml (#1207).
/// </summary>
public sealed class CityGmlIngestResponse
{
    /// <summary>Stable identifier used in the resulting scene URL.</summary>
    public string SceneId { get; set; } = string.Empty;

    /// <summary>Routable URL for the generated Building Scene Layer tileset's root document.</summary>
    public string TilesetUrl { get; set; } = string.Empty;

    /// <summary>Number of buildings ingested from the CityGML document.</summary>
    public int BuildingCount { get; set; }

    /// <summary>Number of boundary-surface components emitted with BSL attributes.</summary>
    public int SurfaceCount { get; set; }

    /// <summary>Number of tile content files written under the scene asset root.</summary>
    public int TileCount { get; set; }

    /// <summary>Distinct Building Scene Layer disciplines / sub-layers present, sorted.</summary>
    public string[] Disciplines { get; set; } = Array.Empty<string>();

    /// <summary>Bounding region of the dataset in WGS-84 degrees [west, south, east, north].</summary>
    public double[] BoundingRegionDegrees { get; set; } = [0, 0, 0, 0];

    /// <summary>Non-fatal warnings collected during ingest.</summary>
    public string[] Warnings { get; set; } = Array.Empty<string>();
}

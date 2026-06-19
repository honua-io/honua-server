// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Response payload returned by POST /api/v1/admin/scenes/ingest/pointcloud (#1201).
/// </summary>
public sealed class PointCloudIngestResponse
{
    /// <summary>Stable identifier used in the resulting scene URL.</summary>
    public string SceneId { get; set; } = string.Empty;

    /// <summary>Routable URL for the generated point tileset's root document.</summary>
    public string TilesetUrl { get; set; } = string.Empty;

    /// <summary>Number of points ingested from the LAS point cloud.</summary>
    public int PointCount { get; set; }

    /// <summary>Number of <c>.pnts</c> tile content files written under the scene asset root.</summary>
    public int TileCount { get; set; }

    /// <summary>True when the source LAS carried per-point RGB preserved in the output tiles.</summary>
    public bool HasColor { get; set; }

    /// <summary>Bounding region of the dataset in WGS-84 degrees [west, south, east, north].</summary>
    public double[] BoundingRegionDegrees { get; set; } = [0, 0, 0, 0];
}

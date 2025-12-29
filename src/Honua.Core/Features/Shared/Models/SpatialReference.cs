// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Shared.Models;

/// <summary>
/// Represents a spatial reference system with various identifier formats
/// </summary>
public readonly record struct SpatialReference
{
    /// <summary>
    /// Well-Known ID (EPSG code)
    /// </summary>
    public required int Wkid { get; init; }

    /// <summary>
    /// Latest Well-Known ID (for newer EPSG codes)
    /// </summary>
    public int? LatestWkid { get; init; }

    /// <summary>
    /// Vertical coordinate system WKID
    /// </summary>
    public int? VcsWkid { get; init; }

    /// <summary>
    /// Latest vertical coordinate system WKID
    /// </summary>
    public int? LatestVcsWkid { get; init; }

    /// <summary>
    /// Well-Known Text representation
    /// </summary>
    public string? Wkt { get; init; }

    /// <summary>
    /// Creates a spatial reference with only WKID
    /// </summary>
    /// <param name="wkid">Well-Known ID (EPSG code)</param>
    /// <returns>Spatial reference instance</returns>
    public static SpatialReference Create(int wkid)
        => new() { Wkid = wkid };

    /// <summary>
    /// Creates a spatial reference with WKID and latest WKID
    /// </summary>
    /// <param name="wkid">Well-Known ID (EPSG code)</param>
    /// <param name="latestWkid">Latest Well-Known ID</param>
    /// <returns>Spatial reference instance</returns>
    public static SpatialReference Create(int wkid, int? latestWkid)
        => new() { Wkid = wkid, LatestWkid = latestWkid };

    /// <summary>
    /// Creates a spatial reference with all parameters
    /// </summary>
    /// <param name="wkid">Well-Known ID (EPSG code)</param>
    /// <param name="latestWkid">Latest Well-Known ID</param>
    /// <param name="vcsWkid">Vertical coordinate system WKID</param>
    /// <param name="latestVcsWkid">Latest vertical coordinate system WKID</param>
    /// <param name="wkt">Well-Known Text representation</param>
    /// <returns>Spatial reference instance</returns>
    public static SpatialReference Create(int wkid, int? latestWkid, int? vcsWkid, int? latestVcsWkid, string? wkt)
        => new()
        {
            Wkid = wkid,
            LatestWkid = latestWkid,
            VcsWkid = vcsWkid,
            LatestVcsWkid = latestVcsWkid,
            Wkt = wkt
        };
}

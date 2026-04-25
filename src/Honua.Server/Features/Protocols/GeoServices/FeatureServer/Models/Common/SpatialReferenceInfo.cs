// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// FeatureServer-specific spatial reference information
/// </summary>
public sealed class SpatialReferenceInfo
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
}

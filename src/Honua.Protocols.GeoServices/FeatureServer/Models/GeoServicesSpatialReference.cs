// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// GeoServices spatial reference for geometry objects
/// </summary>
public sealed class GeoServicesSpatialReference
{
    /// <summary>
    /// Well-Known ID (EPSG code)
    /// </summary>
    public int? Wkid { get; init; }

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

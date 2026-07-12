// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

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

/// <summary>
/// Creates GeoServices spatial-reference envelopes while retaining a requested
/// Esri WKID and pairing it with its current canonical WKID.
/// </summary>
internal static class GeoServicesSpatialReferenceFactory
{
    /// <summary>
    /// Creates a spatial reference for a positive requested SRID, or
    /// <c>null</c> when no valid SRID was supplied.
    /// </summary>
    internal static GeoServicesSpatialReference? Create(int? requestedSrid)
        => requestedSrid is > 0
            ? new GeoServicesSpatialReference
            {
                Wkid = requestedSrid.Value,
                LatestWkid = SpatialReferenceExtensions.NormalizeWebMercatorSrid(requestedSrid.Value)
            }
            : null;
}

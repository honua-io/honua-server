// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Server.Features.Ogc.Common;

/// <summary>
/// Extensions for converting between OGC CRS identifiers and shared spatial references.
/// </summary>
public static class OgcCrsExtensions
{
    /// <summary>
    /// Creates SRID from OGC CRS string.
    /// </summary>
    public static int ToSrid(this string crs)
        => ExtentExtensions.TryExtractSridFromCrs(crs, out var srid)
            ? srid
            : throw new FormatException($"Unsupported CRS '{crs}'.");

    /// <summary>
    /// Converts SRID to OGC CRS URI.
    /// </summary>
    public static string ToOgcCrs(this int srid)
        => ExtentExtensions.ToOgcCrsUri(srid);

    /// <summary>
    /// Converts a shared SpatialReference to OGC CRS string.
    /// </summary>
    public static string ToOgcCrs(this SpatialReference spatialRef)
        => ExtentExtensions.ToOgcCrsUri(spatialRef.ToSrid());

    /// <summary>
    /// Creates a shared SpatialReference from OGC CRS string.
    /// </summary>
    public static SpatialReference ToSpatialReference(this string crs)
        => SpatialReference.Create(crs.ToSrid());
}

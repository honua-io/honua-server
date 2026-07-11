// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using NetTopologySuite.Geometries;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// Shared input-validation guard for the single-WKB geometry executors. Every
/// geometry executor parses its payload with <c>new WKBReader { HandleSRID =
/// true }</c> and then stamps the caller-declared srid onto the result
/// unconditionally. When the payload is EWKB carrying its own (nonzero) SRID
/// that differs from the declared srid, the unconditional stamp silently
/// relabels the geometry's coordinate reference, producing results measured or
/// reprojected under the wrong CRS. This guard rejects that mismatch up front so
/// the failure is reported as an input-validation error rather than being
/// masked (#2733).
/// </summary>
internal static class GeometrySridGuard
{
    /// <summary>
    /// Returns <see langword="false"/> with a client-safe <paramref name="error"/>
    /// message when <paramref name="geometry"/> carries a positive embedded SRID
    /// that differs from <paramref name="declaredSrid"/>. A non-positive embedded
    /// SRID (plain WKB, EWKB with SRID 0, or an NTS factory default of -1) is
    /// treated as "unspecified" and accepted, so the caller may stamp the declared
    /// srid onto it.
    /// </summary>
    public static bool TryValidateEmbeddedSrid(Geometry geometry, int declaredSrid, out string error)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        if (geometry.SRID > 0 && geometry.SRID != declaredSrid)
        {
            error = $"geometry carries embedded SRID {geometry.SRID} which does not match the declared srid {declaredSrid}";
            return false;
        }

        error = "";
        return true;
    }
}

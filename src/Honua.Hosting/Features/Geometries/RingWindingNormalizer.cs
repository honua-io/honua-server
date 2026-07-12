// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Geometries;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Infrastructure.Geometries;

/// <summary>
/// Normalizes polygon ring winding to the right-hand rule (exterior rings counter-clockwise,
/// interior rings — holes — clockwise) shared by RFC 7946 §3.1.6 (GeoJSON), GML 3.2 / ISO 19107
/// surface patches, and KML polygons.
/// </summary>
/// <remarks>
/// <para>
/// PostGIS-backed primary output already enforces this orientation via <c>ST_ForcePolygonCCW</c>,
/// and the GeoServices NetTopologySuite path enforces the Esri-JSON convention explicitly. Secondary
/// emitters that hand a stored geometry straight to a serializer (a <see cref="GeoJsonWriter"/> or an
/// in-memory KML writer) would otherwise pass through whatever winding the storage holds — stored
/// data is frequently clockwise-exterior (Esri <c>applyEdits</c>, shapefile imports), which violates
/// the right-hand rule. Routing those emitters through this helper keeps output consistent across
/// protocols (#2745).
/// </para>
/// <para>
/// The helper is cheap on the common path: non-polygonal geometry and already-correctly-wound
/// polygons are returned by reference without cloning; a new geometry is materialized only when a
/// ring actually needs reversing. The reorientation itself is delegated to the shared
/// <see cref="RingOrientationNormalizer"/> so this right-hand-rule emitter and the SQL Server
/// geography filter normalizer stay in lockstep.
/// </para>
/// </remarks>
internal static class RingWindingNormalizer
{
    /// <summary>
    /// Serializes <paramref name="geometry"/> to GeoJSON with right-hand-rule (RFC 7946) winding.
    /// </summary>
    /// <param name="writer">A GeoJSON writer. Not thread-safe; callers must not share it concurrently.</param>
    /// <param name="geometry">The geometry to serialize.</param>
    /// <returns>The GeoJSON representation with normalized polygon ring winding.</returns>
    public static string WriteGeoJson(GeoJsonWriter writer, Geometry geometry)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(geometry);
        return writer.Write(NormalizeToRightHandRule(geometry));
    }

    /// <summary>
    /// Returns a geometry whose polygonal rings follow the right-hand rule (exterior CCW, holes CW).
    /// Non-polygonal geometry and already-normalized polygons are returned unchanged (by reference);
    /// otherwise a reoriented copy is produced.
    /// </summary>
    /// <param name="geometry">The geometry to normalize.</param>
    /// <returns>A geometry with normalized ring winding.</returns>
    public static Geometry NormalizeToRightHandRule(Geometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return RingOrientationNormalizer.Normalize(geometry, wantExteriorCcw: true);
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO;
using Honua.Core.Geometries;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.SqlServer.Features.FeatureStore.Services;

/// <summary>
/// Normalizes SQL Server <c>geometry</c>/<c>geography</c> filter geometries for binding into
/// <c>STGeomFromWKB</c>. <see cref="NormalizeToCcwExterior"/> handles the <c>geography</c> left-hand
/// winding rule: SQL Server's geography type applies the left-hand rule, so exterior rings must
/// be counter-clockwise (CCW) and interior rings (holes) clockwise (CW). Clockwise-exterior
/// polygons — commonly produced by Esri clients — are otherwise interpreted as the polygon's
/// complement, selecting the wrong region or raising error 24205 when the complement spans more
/// than a hemisphere. The ring reorientation is delegated to the shared
/// <see cref="RingOrientationNormalizer"/> (with <c>wantExteriorCcw: true</c>) so the geography
/// filter path and the right-hand-rule egress emitter share one algorithm (#2745).
/// <see cref="NormalizeToPlainWkb"/> handles the planar <c>geometry</c> type's ISO/OGC Z/M
/// dimension-flavor requirement (see its own doc comment).
/// </summary>
internal static class SqlServerGeographyWinding
{
    [ThreadStatic]
    private static WKBReader? _reader;

    /// <summary>
    /// Returns WKB whose polygonal rings are oriented for SQL Server geography (CCW exterior,
    /// CW holes). Non-polygonal geometries and already-normalized polygons are returned as the
    /// original bytes; unparseable input is passed through untouched so parsing errors surface at
    /// the SQL Server engine rather than here.
    /// </summary>
    /// <param name="wkb">The client-supplied filter geometry as well-known binary.</param>
    /// <returns>WKB with CCW-exterior polygon winding, or the original bytes when no change applies.</returns>
    public static byte[] NormalizeToCcwExterior(byte[] wkb)
    {
        ArgumentNullException.ThrowIfNull(wkb);

        Geometry geometry;
        try
        {
            _reader ??= new WKBReader();
            geometry = _reader.Read(wkb);
        }
        catch (Exception ex) when (ex is ParseException or EndOfStreamException or ArgumentException or IndexOutOfRangeException)
        {
            // Leave malformed/short WKB alone; the geography parser will report a precise error.
            return wkb;
        }

        var oriented = RingOrientationNormalizer.Reorient(geometry, wantExteriorCcw: true);
        if (oriented is null)
        {
            return wkb;
        }

        var coordinates = oriented.Coordinates;
        var hasZ = coordinates.Any(coordinate => !double.IsNaN(coordinate.Z));
        var hasM = coordinates.Any(coordinate => !double.IsNaN(coordinate.M));
        var writer = new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: hasZ, emitM: hasM);
        return writer.Write(oriented);
    }

    /// <summary>
    /// Returns WKB re-emitted in the ISO/OGC dimension flavor SQL Server's planar
    /// <c>geometry::STGeomFromWKB</c> expects for Z/M-dimensioned input (type-code offset, e.g.
    /// 1001 for PointZ), rather than the PostGIS-style high-bit dimension flags Honua's canonical
    /// EWKB carries. <see cref="Honua.Core.Features.FeatureStore.Services.WkbSridNormalizer"/>
    /// only strips the embedded SRID word and otherwise byte-preserves the dimension flags as
    /// given, so it cannot perform this translation on its own; reparsing through NTS here
    /// mirrors <see cref="NormalizeToCcwExterior"/> minus the ring-orientation step. Malformed
    /// input is returned unchanged so the SQL Server parser reports the precise error.
    /// </summary>
    /// <param name="wkb">The client-supplied filter geometry as well-known binary.</param>
    /// <returns>ISO-flavor WKB with no embedded SRID, or the original bytes when unparseable.</returns>
    public static byte[] NormalizeToPlainWkb(byte[] wkb)
    {
        ArgumentNullException.ThrowIfNull(wkb);

        Geometry geometry;
        try
        {
            _reader ??= new WKBReader();
            geometry = _reader.Read(wkb);
        }
        catch (Exception ex) when (ex is ParseException or EndOfStreamException or ArgumentException or IndexOutOfRangeException)
        {
            // Leave malformed/short WKB alone; the geometry parser will report a precise error.
            return wkb;
        }

        var coordinates = geometry.Coordinates;
        var hasZ = coordinates.Any(coordinate => !double.IsNaN(coordinate.Z));
        var hasM = coordinates.Any(coordinate => !double.IsNaN(coordinate.M));
        var writer = new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: hasZ, emitM: hasM);
        return writer.Write(geometry);
    }
}

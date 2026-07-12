// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO;
using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.SqlServer.Features.FeatureStore.Services;

/// <summary>
/// Normalizes polygon ring orientation for SQL Server <c>geography</c> filter geometries.
/// SQL Server's geography type applies the left-hand rule, so exterior rings must be
/// counter-clockwise (CCW) and interior rings (holes) clockwise (CW). Clockwise-exterior
/// polygons — commonly produced by Esri clients — are otherwise interpreted as the polygon's
/// complement, selecting the wrong region or raising error 24205 when the complement spans more
/// than a hemisphere.
/// </summary>
internal static class SqlServerGeographyWinding
{
    [ThreadStatic]
    private static WKBReader? _reader;

    [ThreadStatic]
    private static WKBWriter? _writer;

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

        var oriented = OrientPolygonal(geometry);
        if (oriented is null)
        {
            return wkb;
        }

        _writer ??= new WKBWriter();
        return _writer.Write(oriented);
    }

    private static Geometry? OrientPolygonal(Geometry geometry)
    {
        switch (geometry)
        {
            case Polygon polygon:
                return OrientPolygon(polygon);
            case MultiPolygon multiPolygon:
                var polygons = new Polygon[multiPolygon.NumGeometries];
                for (var i = 0; i < multiPolygon.NumGeometries; i++)
                {
                    polygons[i] = OrientPolygon((Polygon)multiPolygon.GetGeometryN(i));
                }

                return geometry.Factory.CreateMultiPolygon(polygons);
            default:
                // Points, lines, and collections carry no meaningful ring orientation.
                return null;
        }
    }

    private static Polygon OrientPolygon(Polygon polygon)
    {
        var factory = polygon.Factory;
        var shell = EnsureOrientation((LinearRing)polygon.ExteriorRing, wantCcw: true);

        var holes = new LinearRing[polygon.NumInteriorRings];
        for (var i = 0; i < polygon.NumInteriorRings; i++)
        {
            holes[i] = EnsureOrientation((LinearRing)polygon.GetInteriorRingN(i), wantCcw: false);
        }

        return factory.CreatePolygon(shell, holes);
    }

    private static LinearRing EnsureOrientation(LinearRing ring, bool wantCcw)
    {
        var isCcw = Orientation.IsCCW(ring.CoordinateSequence);
        return isCcw == wantCcw ? ring : (LinearRing)ring.Reverse();
    }
}

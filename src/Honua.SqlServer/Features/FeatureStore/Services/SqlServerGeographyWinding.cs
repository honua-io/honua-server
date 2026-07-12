// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO;
using Honua.Core.Geometries;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.SqlServer.Features.FeatureStore.Services;

/// <summary>
/// Normalizes polygon ring orientation for SQL Server <c>geography</c> filter geometries.
/// SQL Server's geography type applies the left-hand rule, so exterior rings must be
/// counter-clockwise (CCW) and interior rings (holes) clockwise (CW). Clockwise-exterior
/// polygons — commonly produced by Esri clients — are otherwise interpreted as the polygon's
/// complement, selecting the wrong region or raising error 24205 when the complement spans more
/// than a hemisphere. The ring reorientation is delegated to the shared
/// <see cref="RingOrientationNormalizer"/> (with <c>wantExteriorCcw: true</c>) so the geography
/// filter path and the right-hand-rule egress emitter share one algorithm (#2745).
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

        // CCW exterior is the right-hand-rule orientation the shared core produces with
        // wantExteriorCcw: true. A null return means the geometry already conforms (or is
        // non-polygonal), so the original bytes are handed straight to the geography parser.
        var oriented = RingOrientationNormalizer.Reorient(geometry, wantExteriorCcw: true);
        if (oriented is null)
        {
            return wkb;
        }

        _writer ??= new WKBWriter();
        return _writer.Write(oriented);
    }
}

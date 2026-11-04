// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IO;
using Honua.Core.Geometries;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Db.SqlServer.Features.FeatureStore.Services;

// SQL Server geography uses the left-hand rule, so a clockwise exterior, common from Esri clients, is read as the polygon's complement.
internal static class SqlServerGeographyWinding
{
    [ThreadStatic]
    private static WKBReader? _reader;

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

    // Planar SQL Server geometry expects ISO/OGC Z/M type-code offsets, not the PostGIS EWKB high-bit flags stored here.
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

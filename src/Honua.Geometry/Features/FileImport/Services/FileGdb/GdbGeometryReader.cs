// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.FileImport.Services.FileGdb;

/// <summary>
/// Reads FileGDB geometry blobs and converts them to NTS Geometry objects.
/// </summary>
/// <remarks>
/// FileGDB geometry blobs encode coordinates using origin/scale parameters from the
/// geometry field definition. Coordinates are stored as variable-length integers:
/// point types use unsigned varints for absolute coordinates; multi-vertex types
/// (polyline, polygon, multipoint) use zigzag-encoded signed varints where the
/// first coordinate pair is absolute and subsequent pairs are delta-encoded.
/// </remarks>
internal sealed class GdbGeometryReader
{
    // FileGDB geometry type codes stored in geometry blobs.
    private const int ShptNull = 0;
    private const int ShptPointZ = 9;
    private const int ShptPointZM = 11;
    private const int ShptPointM = 21;
    private const int ShptMultipoint = 8;
    private const int ShptMultipointZ = 20;
    private const int ShptMultipointZM = 18;
    private const int ShptMultipointM = 28;
    private const int ShptArc = 3;
    private const int ShptArcZ = 10;
    private const int ShptArcZM = 13;
    private const int ShptArcM = 23;
    private const int ShptArea = 5;
    private const int ShptAreaZ = 19;
    private const int ShptAreaZM = 15;
    private const int ShptAreaM = 25;
    private const int ShptGeneralPoint = 50;
    private const int ShptGeneralMultipoint = 51;
    private const int ShptGeneralPolyline = 52;
    private const int ShptGeneralPolygon = 53;

    private readonly GeometryFactory _factory;
    private readonly GdbGeometryDef _geomDef;

    /// <summary>
    /// Initializes a new instance of the <see cref="GdbGeometryReader"/> class.
    /// </summary>
    public GdbGeometryReader(GeometryFactory factory, GdbGeometryDef geomDef)
    {
        _factory = factory;
        _geomDef = geomDef;
    }

    /// <summary>
    /// Reads a geometry from a FileGDB geometry blob.
    /// </summary>
    /// <param name="blob">The raw geometry blob bytes (after the varuint length prefix).</param>
    /// <returns>The parsed geometry, or null for null/empty/unsupported geometries.</returns>
    public NtsGeometry? ReadGeometry(ReadOnlySpan<byte> blob)
    {
        if (blob.IsEmpty)
        {
            return null;
        }

        try
        {
            var offset = 0;
            var geomType = (int)GdbVarInt.ReadVarUInt(blob, ref offset);

            return geomType switch
            {
                ShptNull or 0 => null,

                // Point types.
                1 => ReadPoint(blob, ref offset, hasZ: false, hasM: false),
                ShptPointZ => ReadPoint(blob, ref offset, hasZ: true, hasM: false),
                ShptPointM => ReadPoint(blob, ref offset, hasZ: false, hasM: true),
                ShptPointZM => ReadPoint(blob, ref offset, hasZ: true, hasM: true),

                // Polyline types.
                ShptArc => ReadMultiVertexGeometry(blob, ref offset, isPolygon: false, hasZ: false, hasM: false),
                ShptArcZ => ReadMultiVertexGeometry(blob, ref offset, isPolygon: false, hasZ: true, hasM: false),
                ShptArcM => ReadMultiVertexGeometry(blob, ref offset, isPolygon: false, hasZ: false, hasM: true),
                ShptArcZM => ReadMultiVertexGeometry(blob, ref offset, isPolygon: false, hasZ: true, hasM: true),

                // Polygon types.
                ShptArea => ReadMultiVertexGeometry(blob, ref offset, isPolygon: true, hasZ: false, hasM: false),
                ShptAreaZ => ReadMultiVertexGeometry(blob, ref offset, isPolygon: true, hasZ: true, hasM: false),
                ShptAreaM => ReadMultiVertexGeometry(blob, ref offset, isPolygon: true, hasZ: false, hasM: true),
                ShptAreaZM => ReadMultiVertexGeometry(blob, ref offset, isPolygon: true, hasZ: true, hasM: true),

                // Multipoint types.
                ShptMultipoint => ReadMultiPoint(blob, ref offset, hasZ: false, hasM: false),
                ShptMultipointZ => ReadMultiPoint(blob, ref offset, hasZ: true, hasM: false),
                ShptMultipointM => ReadMultiPoint(blob, ref offset, hasZ: false, hasM: true),
                ShptMultipointZM => ReadMultiPoint(blob, ref offset, hasZ: true, hasM: true),

                // General types (have a flags byte indicating components).
                ShptGeneralPoint => ReadGeneralPoint(blob, ref offset),
                ShptGeneralMultipoint => ReadGeneralMultiPoint(blob, ref offset),
                ShptGeneralPolyline => ReadGeneralMultiVertexGeometry(blob, ref offset, isPolygon: false),
                ShptGeneralPolygon => ReadGeneralMultiVertexGeometry(blob, ref offset, isPolygon: true),

                _ => null, // Unsupported geometry type.
            };
        }
        catch (InvalidDataException)
        {
            return null; // Malformed geometry blob.
        }
    }

    private Point? ReadPoint(ReadOnlySpan<byte> blob, ref int offset, bool hasZ, bool hasM)
    {
        if (offset >= blob.Length)
        {
            return null;
        }

        // Point coordinates are stored as unsigned varints (absolute values).
        var xRaw = GdbVarInt.ReadVarUInt(blob, ref offset);
        var yRaw = GdbVarInt.ReadVarUInt(blob, ref offset);

        var x = (double)xRaw / _geomDef.XYScale + _geomDef.XOrigin;
        var y = (double)yRaw / _geomDef.XYScale + _geomDef.YOrigin;

        double? z = null;
        if (hasZ && offset < blob.Length)
        {
            var zRaw = GdbVarInt.ReadVarUInt(blob, ref offset);
            z = (double)zRaw / _geomDef.ZScale + _geomDef.ZOrigin;
        }

        // Carry the M (measure) ordinate so XYM/XYZM points round-trip instead of
        // being silently flattened (#1981). Point M values are unsigned varints.
        double? m = null;
        if (hasM && offset < blob.Length)
        {
            var mRaw = GdbVarInt.ReadVarUInt(blob, ref offset);
            m = (double)mRaw / _geomDef.MScale + _geomDef.MOrigin;
        }

        return _factory.CreatePoint(BuildCoordinate(x, y, z, m));
    }

    private NtsGeometry? ReadMultiVertexGeometry(
        ReadOnlySpan<byte> blob,
        ref int offset,
        bool isPolygon,
        bool hasZ,
        bool hasM)
    {
        if (offset >= blob.Length)
        {
            return null;
        }

        var numPoints = (int)GdbVarInt.ReadVarUInt(blob, ref offset);
        if (numPoints == 0)
        {
            return isPolygon
                ? _factory.CreatePolygon()
                : _factory.CreateLineString();
        }

        var numParts = (int)GdbVarInt.ReadVarUInt(blob, ref offset);
        if (numParts <= 0 || numParts > numPoints)
        {
            throw new InvalidDataException("FileGDB geometry contains invalid point or part counts.");
        }

        if (numPoints > blob.Length || numParts > blob.Length)
        {
            throw new InvalidDataException("FileGDB geometry contains impossible point or part counts.");
        }

        // Read envelope (xmin, ymin, width, height) as unsigned varints.
        GdbVarInt.ReadVarUInt(blob, ref offset); // xmin
        GdbVarInt.ReadVarUInt(blob, ref offset); // ymin
        GdbVarInt.ReadVarUInt(blob, ref offset); // width
        GdbVarInt.ReadVarUInt(blob, ref offset); // height

        // Read part point counts for multi-part geometries.
        // For numParts > 1: read numParts - 1 varuints.
        // Each value represents the number of points in that part.
        var partPointCounts = new int[numParts];
        if (numParts > 1)
        {
            var remaining = numPoints;
            for (var p = 0; p < numParts - 1; p++)
            {
                var partCount = (int)GdbVarInt.ReadVarUInt(blob, ref offset);
                // Each part count must be positive and must not exceed the
                // remaining points budget; a crafted file can otherwise cause
                // BuildPolygon/BuildPolyline to allocate a multi-GB array.
                if (partCount <= 0 || partCount > remaining)
                {
                    throw new InvalidDataException(
                        $"FileGDB geometry part {p} has an invalid point count ({partCount}); remaining budget is {remaining}.");
                }

                partPointCounts[p] = partCount;
                remaining -= partCount;
            }

            // The last part consumes whatever points are left.  'remaining' is
            // already guaranteed non-negative by the loop above.
            partPointCounts[numParts - 1] = remaining;
        }
        else
        {
            partPointCounts[0] = numPoints;
        }

        // Read XY coordinates: all zigzag-encoded, first absolute, rest delta.
        var coordinates = ReadCoordinates(blob, ref offset, numPoints);
        if (coordinates == null)
        {
            return null;
        }

        // Read Z coordinates if present.
        double[]? zValues = null;
        if (hasZ)
        {
            zValues = ReadZCoordinates(blob, ref offset, numPoints);
        }

        // Read M (measure) coordinates if present. Previously these were skipped,
        // silently dropping measures from XYM/XYZM polylines and polygons (#1981).
        double[]? mValues = null;
        if (hasM)
        {
            mValues = ReadMCoordinates(blob, ref offset, numPoints);
        }

        // Apply Z/M values to coordinates, choosing the coordinate type so the
        // resulting geometry's coordinate sequence carries the extra ordinates
        // and the WKB writer can emit them.
        ApplyZm(coordinates, zValues, mValues);

        // Build geometry from parts.
        if (isPolygon)
        {
            return BuildPolygon(coordinates, partPointCounts);
        }

        return BuildPolyline(coordinates, partPointCounts);
    }

    private Coordinate[]? ReadCoordinates(ReadOnlySpan<byte> blob, ref int offset, int numPoints)
    {
        if (numPoints <= 0)
        {
            return null;
        }

        var coords = new Coordinate[numPoints];
        long xAcc = 0;
        long yAcc = 0;

        for (var i = 0; i < numPoints; i++)
        {
            if (offset >= blob.Length)
            {
                return null;
            }

            // All coordinates use zigzag-encoded signed varints.
            // First coordinate is absolute, subsequent are deltas from previous.
            var dx = GdbVarInt.ReadVarInt(blob, ref offset);
            var dy = GdbVarInt.ReadVarInt(blob, ref offset);

            xAcc += dx;
            yAcc += dy;

            var x = (double)xAcc / _geomDef.XYScale + _geomDef.XOrigin;
            var y = (double)yAcc / _geomDef.XYScale + _geomDef.YOrigin;

            coords[i] = new Coordinate(x, y);
        }

        return coords;
    }

    private double[]? ReadZCoordinates(ReadOnlySpan<byte> blob, ref int offset, int numPoints)
    {
        if (numPoints <= 0 || offset >= blob.Length)
        {
            return null;
        }

        var zValues = new double[numPoints];
        long zAcc = 0;

        for (var i = 0; i < numPoints; i++)
        {
            if (offset >= blob.Length)
            {
                break;
            }

            var dz = GdbVarInt.ReadVarInt(blob, ref offset);
            zAcc += dz;
            zValues[i] = (double)zAcc / _geomDef.ZScale + _geomDef.ZOrigin;
        }

        return zValues;
    }

    private double[]? ReadMCoordinates(ReadOnlySpan<byte> blob, ref int offset, int numPoints)
    {
        if (numPoints <= 0 || offset >= blob.Length)
        {
            return null;
        }

        // M values use the same zigzag-encoded signed varints as coordinates:
        // first absolute, subsequent delta-encoded, then descaled with the M
        // origin/scale from the geometry definition.
        var mValues = new double[numPoints];
        long mAcc = 0;

        for (var i = 0; i < numPoints; i++)
        {
            if (offset >= blob.Length)
            {
                break;
            }

            var dm = GdbVarInt.ReadVarInt(blob, ref offset);
            mAcc += dm;
            mValues[i] = (double)mAcc / _geomDef.MScale + _geomDef.MOrigin;
        }

        return mValues;
    }

    /// <summary>
    /// Promotes plain XY coordinates to XYZ/XYM/XYZM in place, picking the NTS
    /// coordinate type so the resulting geometry's coordinate sequence advertises
    /// the extra ordinates and the WKB writer can emit them (#1981).
    /// </summary>
    private static void ApplyZm(Coordinate[] coordinates, double[]? zValues, double[]? mValues)
    {
        if (zValues == null && mValues == null)
        {
            return;
        }

        for (var i = 0; i < coordinates.Length; i++)
        {
            var z = zValues != null && i < zValues.Length ? zValues[i] : (double?)null;
            var m = mValues != null && i < mValues.Length ? mValues[i] : (double?)null;
            coordinates[i] = BuildCoordinate(coordinates[i].X, coordinates[i].Y, z, m);
        }
    }

    /// <summary>
    /// Builds the narrowest NTS coordinate type that carries the supplied
    /// ordinates: <see cref="CoordinateZM"/> for XYZM, <see cref="CoordinateM"/>
    /// for XYM, <see cref="CoordinateZ"/> for XYZ, otherwise plain XY.
    /// </summary>
    private static Coordinate BuildCoordinate(double x, double y, double? z, double? m)
    {
        if (z.HasValue && m.HasValue)
        {
            return new CoordinateZM(x, y, z.Value, m.Value);
        }

        if (m.HasValue)
        {
            return new CoordinateM(x, y, m.Value);
        }

        if (z.HasValue)
        {
            return new CoordinateZ(x, y, z.Value);
        }

        return new Coordinate(x, y);
    }

    private MultiPoint? ReadMultiPoint(
        ReadOnlySpan<byte> blob,
        ref int offset,
        bool hasZ,
        bool hasM)
    {
        if (offset >= blob.Length)
        {
            return null;
        }

        var numPoints = (int)GdbVarInt.ReadVarUInt(blob, ref offset);
        if (numPoints == 0)
        {
            return _factory.CreateMultiPoint();
        }

        if (numPoints > blob.Length)
        {
            throw new InvalidDataException("FileGDB geometry contains an impossible point count.");
        }

        // Read envelope.
        GdbVarInt.ReadVarUInt(blob, ref offset); // xmin
        GdbVarInt.ReadVarUInt(blob, ref offset); // ymin
        GdbVarInt.ReadVarUInt(blob, ref offset); // width
        GdbVarInt.ReadVarUInt(blob, ref offset); // height

        // Read coordinates (zigzag, delta-encoded).
        var coords = ReadCoordinates(blob, ref offset, numPoints);
        if (coords == null)
        {
            return null;
        }

        // Read Z if present.
        double[]? zValues = hasZ ? ReadZCoordinates(blob, ref offset, numPoints) : null;

        // Read M (measure) if present instead of skipping it, so XYM/XYZM
        // multipoints round-trip their measures (#1981).
        double[]? mValues = hasM ? ReadMCoordinates(blob, ref offset, numPoints) : null;

        ApplyZm(coords, zValues, mValues);

        var points = new Point[numPoints];
        for (var i = 0; i < numPoints; i++)
        {
            points[i] = _factory.CreatePoint(coords[i]);
        }

        return _factory.CreateMultiPoint(points);
    }

    private Point? ReadGeneralPoint(ReadOnlySpan<byte> blob, ref int offset)
    {
        if (offset >= blob.Length)
        {
            return null;
        }

        var flags = blob[offset++];
        var hasZ = (flags & 0x80) != 0;
        var hasM = (flags & 0x40) != 0;

        return ReadPoint(blob, ref offset, hasZ, hasM);
    }

    private MultiPoint? ReadGeneralMultiPoint(ReadOnlySpan<byte> blob, ref int offset)
    {
        if (offset >= blob.Length)
        {
            return null;
        }

        var flags = blob[offset++];
        var hasZ = (flags & 0x80) != 0;
        var hasM = (flags & 0x40) != 0;

        return ReadMultiPoint(blob, ref offset, hasZ, hasM);
    }

    private NtsGeometry? ReadGeneralMultiVertexGeometry(
        ReadOnlySpan<byte> blob,
        ref int offset,
        bool isPolygon)
    {
        if (offset >= blob.Length)
        {
            return null;
        }

        var flags = blob[offset++];
        var hasZ = (flags & 0x80) != 0;
        var hasM = (flags & 0x40) != 0;

        return ReadMultiVertexGeometry(blob, ref offset, isPolygon, hasZ, hasM);
    }

    private NtsGeometry BuildPolygon(Coordinate[] allCoords, int[] partPointCounts)
    {
        var rings = new List<LinearRing>();
        var coordIndex = 0;

        for (var p = 0; p < partPointCounts.Length; p++)
        {
            var count = partPointCounts[p];
            if (count < 3)
            {
                coordIndex += count;
                continue;
            }

            var ringCoords = new Coordinate[count];
            Array.Copy(allCoords, coordIndex, ringCoords, 0, Math.Min(count, allCoords.Length - coordIndex));
            coordIndex += count;

            // Ensure ring is closed.
            if (!ringCoords[0].Equals2D(ringCoords[^1]))
            {
                var closed = new Coordinate[count + 1];
                Array.Copy(ringCoords, closed, count);
                closed[count] = ringCoords[0].Copy();
                ringCoords = closed;
            }

            if (ringCoords.Length >= 4)
            {
                rings.Add(_factory.CreateLinearRing(ringCoords));
            }
        }

        if (rings.Count == 0)
        {
            return _factory.CreatePolygon();
        }

        if (rings.Count == 1)
        {
            return _factory.CreatePolygon(rings[0]);
        }

        // Esri convention: clockwise rings are exterior shells, counter-clockwise
        // are holes. Classify each ring and group holes with their preceding shell.
        // NTS IsCCW uses the signed-area test; Esri CW = !IsCCW = shell.
        var polygons = new List<Polygon>();
        LinearRing? currentShell = null;
        var currentHoles = new List<LinearRing>();

        foreach (var ring in rings)
        {
            var isCcw = NetTopologySuite.Algorithm.Orientation.IsCCW(ring.Coordinates);
            if (!isCcw)
            {
                // New exterior shell — flush the previous polygon if any.
                if (currentShell != null)
                {
                    polygons.Add(_factory.CreatePolygon(currentShell, currentHoles.ToArray()));
                    currentHoles.Clear();
                }

                currentShell = ring;
            }
            else
            {
                // Hole — attach to current shell.
                if (currentShell != null)
                {
                    currentHoles.Add(ring);
                }
            }
        }

        // Flush the last polygon.
        if (currentShell != null)
        {
            polygons.Add(_factory.CreatePolygon(currentShell, currentHoles.ToArray()));
        }

        if (polygons.Count == 1)
        {
            return polygons[0];
        }

        return _factory.CreateMultiPolygon(polygons.ToArray());
    }

    private NtsGeometry BuildPolyline(Coordinate[] allCoords, int[] partPointCounts)
    {
        var lines = new List<LineString>();
        var coordIndex = 0;

        for (var p = 0; p < partPointCounts.Length; p++)
        {
            var count = partPointCounts[p];
            if (count < 2)
            {
                coordIndex += count;
                continue;
            }

            var lineCoords = new Coordinate[count];
            Array.Copy(allCoords, coordIndex, lineCoords, 0, Math.Min(count, allCoords.Length - coordIndex));
            coordIndex += count;

            lines.Add(_factory.CreateLineString(lineCoords));
        }

        if (lines.Count == 0)
        {
            return _factory.CreateLineString();
        }

        if (lines.Count == 1)
        {
            return lines[0];
        }

        return _factory.CreateMultiLineString(lines.ToArray());
    }
}

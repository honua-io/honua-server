// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.ArcGisRest.Features.FeatureStore.Services;

/// <summary>
/// Converts Esri JSON geometry payloads returned from ArcGIS REST <c>/query</c> into
/// canonical Well-Known Binary (WKB) byte arrays consumed by Honua's
/// <see cref="Honua.Core.Features.FeatureStore.Domain.Feature"/> record.
/// </summary>
/// <remarks>
/// <para>Supports the four wire-format shapes Esri publishes on
/// FeatureServer/MapServer query responses:
/// point (x/y), multipoint (points[]), polyline (paths[][][]), and polygon
/// (rings[][][]). Z and M coordinates are dropped — the read-through provider
/// projects 2D geometries through Honua's canonical pipeline.</para>
/// <para>Output uses little-endian WKB encoding to match the convention used by
/// every other Honua reader. Polygon rings are classified by signed area using the
/// Esri winding convention (exterior rings are clockwise, holes are
/// counter-clockwise) so that a payload containing two or more disjoint exterior
/// rings is emitted as a true WKB MultiPolygon (type 6) with each hole grouped
/// under its enclosing exterior ring — rather than collapsing a second exterior
/// ring into a hole of the first. A single-exterior payload is emitted as a plain
/// WKB Polygon (type 3). Individual ring vertex order is preserved as supplied;
/// only the grouping into shells/holes is inferred.</para>
/// </remarks>
internal static class EsriJsonWkbWriter
{
    private const byte LittleEndian = 0x01;

    private const uint WkbPoint = 1;
    private const uint WkbLineString = 2;
    private const uint WkbPolygon = 3;
    private const uint WkbMultiPoint = 4;
    private const uint WkbMultiLineString = 5;
    private const uint WkbMultiPolygon = 6;

    /// <summary>
    /// Converts an Esri JSON geometry element into a WKB byte array.
    /// </summary>
    /// <param name="geometry">JSON geometry element from an ArcGIS REST response. May be undefined or null.</param>
    /// <param name="declaredGeometryType">Geometry type declared on the publication; used to disambiguate
    /// between LineString/MultiLineString (Esri encodes both as <c>paths</c>) and
    /// Polygon/MultiPolygon (Esri encodes both as <c>rings</c>).</param>
    /// <returns>WKB-encoded geometry, or <c>null</c> when the source element is empty.</returns>
    /// <exception cref="InvalidOperationException">The Esri geometry shape is malformed or unsupported.</exception>
    public static byte[]? Write(JsonElement? geometry, MetadataV2GeometryType declaredGeometryType)
    {
        if (geometry is not { } element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("ArcGIS REST geometry must be a JSON object.");
        }

        if (element.TryGetProperty("x", out var xProp) && element.TryGetProperty("y", out var yProp))
        {
            return WritePoint(xProp.GetDouble(), yProp.GetDouble());
        }

        if (element.TryGetProperty("points", out var pointsArray))
        {
            return WriteMultiPoint(pointsArray);
        }

        if (element.TryGetProperty("paths", out var pathsArray))
        {
            // A declared LineString is emitted as a single WKB LineString when the
            // payload contains exactly one path. If the source returns multiple
            // paths for a layer declared as LineString, promote to MultiLineString
            // rather than merging or rejecting — the rings/paths count, not the
            // catalog declaration, determines the multipart shape.
            return WriteLineStringOrMultiLineString(pathsArray, declaredGeometryType);
        }

        if (element.TryGetProperty("rings", out var ringsArray))
        {
            // Ring classification (not the catalog declaration) decides between a
            // single Polygon and a MultiPolygon: a layer declared Polygon that
            // returns multiple disjoint exterior rings is encoded as a proper
            // MultiPolygon, and a declared MultiPolygon with a single exterior ring
            // collapses to a plain Polygon.
            return WritePolygonOrMultiPolygon(ringsArray);
        }

        throw new InvalidOperationException(
            "ArcGIS REST geometry payload did not contain a recognised x/y, points, paths, or rings field.");
    }

    private static byte[] WritePoint(double x, double y)
    {
        var buffer = new byte[1 + 4 + 16];
        var span = buffer.AsSpan();
        WriteHeader(span[..5], WkbPoint);
        WriteDouble(span.Slice(5, 8), x);
        WriteDouble(span.Slice(13, 8), y);
        return buffer;
    }

    private static byte[] WriteMultiPoint(JsonElement points)
    {
        if (points.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("ArcGIS multipoint 'points' must be a JSON array.");
        }

        var count = points.GetArrayLength();
        var pointWkbSize = 1 + 4 + 16;
        var buffer = new byte[1 + 4 + 4 + (count * pointWkbSize)];
        var span = buffer.AsSpan();

        WriteHeader(span[..5], WkbMultiPoint);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(5, 4), (uint)count);

        var offset = 9;
        foreach (var point in points.EnumerateArray())
        {
            ReadXy(point, out var x, out var y);
            WriteHeader(span.Slice(offset, 5), WkbPoint);
            WriteDouble(span.Slice(offset + 5, 8), x);
            WriteDouble(span.Slice(offset + 13, 8), y);
            offset += pointWkbSize;
        }

        return buffer;
    }

    private static byte[] WriteLineStringOrMultiLineString(
        JsonElement paths,
        MetadataV2GeometryType declaredGeometryType)
    {
        EnsurePathsShape(paths);

        // A single path is encoded as a plain LineString when the layer is declared
        // as a LineString; otherwise the multipart encoding is used. Multiple paths
        // are always a MultiLineString regardless of the catalog declaration so a
        // genuine multipart feature is never silently merged.
        if (paths.GetArrayLength() == 1 && declaredGeometryType == MetadataV2GeometryType.LineString)
        {
            return WriteSingleLineString(paths[0]);
        }

        return WriteMultiLineString(paths);
    }

    private static byte[] WriteSingleLineString(JsonElement path)
    {
        var count = path.GetArrayLength();
        var buffer = new byte[1 + 4 + 4 + (count * 16)];
        var span = buffer.AsSpan();

        WriteHeader(span[..5], WkbLineString);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(5, 4), (uint)count);

        var offset = 9;
        foreach (var point in path.EnumerateArray())
        {
            ReadXy(point, out var x, out var y);
            WriteDouble(span.Slice(offset, 8), x);
            WriteDouble(span.Slice(offset + 8, 8), y);
            offset += 16;
        }

        return buffer;
    }

    private static byte[] WriteMultiLineString(JsonElement paths)
    {
        EnsurePathsShape(paths);

        var lineBuffers = new List<byte[]>(paths.GetArrayLength());
        var total = 1 + 4 + 4;
        foreach (var path in paths.EnumerateArray())
        {
            var lineWkb = WriteSingleLineString(path);
            lineBuffers.Add(lineWkb);
            total += lineWkb.Length;
        }

        var buffer = new byte[total];
        var span = buffer.AsSpan();

        WriteHeader(span[..5], WkbMultiLineString);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(5, 4), (uint)lineBuffers.Count);

        var offset = 9;
        foreach (var line in lineBuffers)
        {
            line.CopyTo(span.Slice(offset, line.Length));
            offset += line.Length;
        }

        return buffer;
    }

    private static byte[] WritePolygonOrMultiPolygon(JsonElement rings)
    {
        EnsureRingsShape(rings);

        // Esri encodes (multi)polygons as a flat rings[] array. By convention the
        // exterior rings are clockwise and holes are counter-clockwise. We classify
        // each ring by its signed area, then group every hole under the most recent
        // exterior ring. When more than one exterior ring is present the result is a
        // true WKB MultiPolygon (type 6); a single exterior ring (with any holes)
        // is a plain WKB Polygon (type 3).
        var encodedRings = EncodeRings(rings);
        var polygons = GroupRingsIntoPolygons(encodedRings);

        return polygons.Count == 1
            ? WritePolygon(polygons[0])
            : WriteMultiPolygon(polygons);
    }

    private static List<EncodedRing> EncodeRings(JsonElement rings)
    {
        var encoded = new List<EncodedRing>(rings.GetArrayLength());
        foreach (var ring in rings.EnumerateArray())
        {
            var vertexCount = ring.GetArrayLength();
            var ringBuffer = new byte[4 + (vertexCount * 16)];
            var ringSpan = ringBuffer.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(ringSpan[..4], (uint)vertexCount);

            var offset = 4;
            double signedAreaTwice = 0;
            double previousX = 0;
            double previousY = 0;
            var isFirst = true;
            foreach (var point in ring.EnumerateArray())
            {
                ReadXy(point, out var x, out var y);
                WriteDouble(ringSpan.Slice(offset, 8), x);
                WriteDouble(ringSpan.Slice(offset + 8, 8), y);
                offset += 16;

                if (!isFirst)
                {
                    // Shoelace accumulation across consecutive vertices.
                    signedAreaTwice += (previousX * y) - (x * previousY);
                }

                previousX = x;
                previousY = y;
                isFirst = false;
            }

            encoded.Add(new EncodedRing(ringBuffer, signedAreaTwice));
        }

        return encoded;
    }

    private static List<List<EncodedRing>> GroupRingsIntoPolygons(List<EncodedRing> rings)
    {
        // Esri exterior rings are clockwise, which yields a negative shoelace sum in
        // a standard (y-up) coordinate frame; holes are counter-clockwise (positive).
        // A ring is treated as exterior when it is clockwise (or degenerate/zero
        // area, which we conservatively treat as a new shell rather than a hole).
        var polygons = new List<List<EncodedRing>>();
        foreach (var ring in rings)
        {
            var isExterior = ring.SignedAreaTwice <= 0;
            if (isExterior || polygons.Count == 0)
            {
                polygons.Add([ring]);
            }
            else
            {
                polygons[^1].Add(ring);
            }
        }

        return polygons;
    }

    private static byte[] WritePolygon(List<EncodedRing> polygonRings)
    {
        var total = 1 + 4 + 4;
        foreach (var ring in polygonRings)
        {
            total += ring.Buffer.Length;
        }

        var buffer = new byte[total];
        var span = buffer.AsSpan();

        WriteHeader(span[..5], WkbPolygon);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(5, 4), (uint)polygonRings.Count);

        var copyOffset = 9;
        foreach (var ring in polygonRings)
        {
            ring.Buffer.CopyTo(span.Slice(copyOffset, ring.Buffer.Length));
            copyOffset += ring.Buffer.Length;
        }

        return buffer;
    }

    private static byte[] WriteMultiPolygon(List<List<EncodedRing>> polygons)
    {
        var polygonBuffers = new byte[polygons.Count][];
        var total = 1 + 4 + 4;
        for (var i = 0; i < polygons.Count; i++)
        {
            var polygonWkb = WritePolygon(polygons[i]);
            polygonBuffers[i] = polygonWkb;
            total += polygonWkb.Length;
        }

        var buffer = new byte[total];
        var span = buffer.AsSpan();

        WriteHeader(span[..5], WkbMultiPolygon);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(5, 4), (uint)polygonBuffers.Length);

        var copyOffset = 9;
        foreach (var polygonWkb in polygonBuffers)
        {
            polygonWkb.CopyTo(span.Slice(copyOffset, polygonWkb.Length));
            copyOffset += polygonWkb.Length;
        }

        return buffer;
    }

    private static void ReadXy(JsonElement point, out double x, out double y)
    {
        if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() < 2)
        {
            throw new InvalidOperationException(
                "ArcGIS coordinate tuple must be an array of at least two numbers ([x, y]).");
        }

        x = point[0].GetDouble();
        y = point[1].GetDouble();
    }

    private static void EnsurePathsShape(JsonElement paths)
    {
        if (paths.ValueKind != JsonValueKind.Array || paths.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("ArcGIS polyline 'paths' must be a non-empty JSON array.");
        }
    }

    private static void EnsureRingsShape(JsonElement rings)
    {
        if (rings.ValueKind != JsonValueKind.Array || rings.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("ArcGIS polygon 'rings' must be a non-empty JSON array.");
        }
    }

    private static void WriteHeader(Span<byte> destination, uint wkbType)
    {
        destination[0] = LittleEndian;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[1..], wkbType);
    }

    private static void WriteDouble(Span<byte> destination, double value)
        => BinaryPrimitives.WriteDoubleLittleEndian(destination, value);

    /// <summary>
    /// A single ring already encoded into its WKB ring body (vertex count + XY
    /// pairs, without a geometry header), paired with twice its signed shoelace
    /// area. The sign distinguishes Esri exterior rings (clockwise) from holes
    /// (counter-clockwise); the magnitude is unused.
    /// </summary>
    private readonly record struct EncodedRing(byte[] Buffer, double SignedAreaTwice);
}

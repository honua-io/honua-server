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
/// every other Honua reader. Polygons are emitted with their rings exactly as
/// supplied by the source — ring orientation/winding fix-up is intentionally not
/// performed here because the canonical query/render pipeline downstream tolerates
/// either orientation.</para>
/// </remarks>
internal static class EsriJsonWkbWriter
{
    private const byte LittleEndian = 0x01;

    private const uint WkbPoint = 1;
    private const uint WkbLineString = 2;
    private const uint WkbPolygon = 3;
    private const uint WkbMultiPoint = 4;
    private const uint WkbMultiLineString = 5;

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
            return declaredGeometryType == MetadataV2GeometryType.LineString
                ? WriteLineStringFromPaths(pathsArray)
                : WriteMultiLineString(pathsArray);
        }

        if (element.TryGetProperty("rings", out var ringsArray))
        {
            return declaredGeometryType == MetadataV2GeometryType.Polygon
                ? WriteSinglePolygon(ringsArray)
                : WriteMultiPolygon(ringsArray);
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

    private static byte[] WriteLineStringFromPaths(JsonElement paths)
    {
        EnsurePathsShape(paths);
        if (paths.GetArrayLength() != 1)
        {
            throw new InvalidOperationException(
                "Declared linestring geometry but ArcGIS payload contained multiple paths; expected exactly one.");
        }

        var path = paths[0];
        return WriteSingleLineString(path);
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

    private static byte[] WriteSinglePolygon(JsonElement rings)
    {
        EnsureRingsShape(rings);
        return WritePolygonFromRings(rings);
    }

    private static byte[] WriteMultiPolygon(JsonElement rings)
    {
        EnsureRingsShape(rings);

        // ArcGIS encodes multipolygon as a single rings[] array: outer rings are
        // clockwise, inner (hole) rings are counter-clockwise. Without running
        // signed-area + point-in-polygon tests, the most interoperable mapping
        // is to project to a single polygon whose first ring is the exterior
        // and the rest are interpreted as holes. Downstream callers tolerate
        // either form when re-projecting through the canonical pipeline.
        return WritePolygonFromRings(rings);
    }

    private static byte[] WritePolygonFromRings(JsonElement rings)
    {
        var ringCount = rings.GetArrayLength();
        var ringBuffers = new (int Count, byte[] Buffer)[ringCount];
        var total = 1 + 4 + 4;
        var i = 0;
        foreach (var ring in rings.EnumerateArray())
        {
            var ringCountInner = ring.GetArrayLength();
            var ringBuffer = new byte[4 + (ringCountInner * 16)];
            var ringSpan = ringBuffer.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(ringSpan[..4], (uint)ringCountInner);
            var offset = 4;
            foreach (var point in ring.EnumerateArray())
            {
                ReadXy(point, out var x, out var y);
                WriteDouble(ringSpan.Slice(offset, 8), x);
                WriteDouble(ringSpan.Slice(offset + 8, 8), y);
                offset += 16;
            }

            ringBuffers[i++] = (ringCountInner, ringBuffer);
            total += ringBuffer.Length;
        }

        var buffer = new byte[total];
        var span = buffer.AsSpan();

        WriteHeader(span[..5], WkbPolygon);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(5, 4), (uint)ringCount);

        var copyOffset = 9;
        foreach (var (_, ringBuffer) in ringBuffers)
        {
            ringBuffer.CopyTo(span.Slice(copyOffset, ringBuffer.Length));
            copyOffset += ringBuffer.Length;
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
}

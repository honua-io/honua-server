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
/// every other Honua reader. Polygons (both single and multi-part) have their rings
/// classified by signed-area orientation per the Esri convention (clockwise = exterior
/// shell, counter-clockwise = hole) and are emitted as WKB MultiPolygon when more
/// than one shell is detected.</para>
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

    // Upper bound on a single WKB buffer (256 MiB). Coordinate counts come straight
    // from JsonElement.GetArrayLength() on an untrusted response; computing buffer
    // sizes in long and validating against this cap turns an Int32 multiplication
    // overflow (which would silently produce a wrong-sized/negative allocation)
    // into a clear, fail-fast error. In practice the response-size cap enforced by
    // ArcGisRestFeatureClient (LengthLimitedStream, MaxResponseContentBytes) keeps
    // real payloads far below this bound.
    private const long MaxWkbBufferBytes = 256L * 1024 * 1024;

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
            // ArcGIS REST represents empty points as {"x":null} or {"x":"NaN"}.
            // Treat either form as a null geometry (feature retained with attributes).
            if (!TryReadPointOrdinate(xProp, out var x) || !TryReadPointOrdinate(yProp, out var y))
            {
                return null;
            }

            return WritePoint(x, y);
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
        var buffer = new byte[CheckedBufferSize(1 + 4 + 4, count, pointWkbSize)];
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
        var buffer = new byte[CheckedBufferSize(1 + 4 + 4, count, 16)];
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
        long total = 1 + 4 + 4;
        foreach (var path in paths.EnumerateArray())
        {
            var lineWkb = WriteSingleLineString(path);
            lineBuffers.Add(lineWkb);
            total += lineWkb.Length;
        }

        var buffer = new byte[CheckedTotalSize(total)];
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
        // Use the same classified writer so hole-vs-shell ordering is always correct.
        return WritePolygonRingsClassified(rings);
    }

    private static byte[] WriteMultiPolygon(JsonElement rings)
    {
        EnsureRingsShape(rings);

        // ArcGIS encodes all parts of a multi-part polygon in one rings[] array:
        // exterior (shell) rings have clockwise winding; hole rings are counter-clockwise.
        // Classify by signed area, assign holes to their containing shell via a
        // point-in-polygon test, then emit WKB Polygon when only one shell is present
        // and WKB MultiPolygon when two or more shells are detected.
        return WritePolygonRingsClassified(rings);
    }

    // Reads all rings from the Esri rings[] array into flat coordinate arrays.
    // Returns a parallel array of (coords, ringBuffer) tuples together with
    // a pre-computed signed area for each ring.
    private readonly struct RingData
    {
        public readonly (double X, double Y)[] Coords; // always set via constructor; default() is never used
        public readonly byte[] WkbBuffer; // [pointCount:uint32][x0:f64][y0:f64]...

        public RingData((double X, double Y)[] coords, byte[] wkbBuffer)
        {
            Coords = coords;
            WkbBuffer = wkbBuffer;
        }
    }

    private static List<RingData> ReadAllRings(JsonElement rings)
    {
        var result = new List<RingData>(rings.GetArrayLength());
        foreach (var ring in rings.EnumerateArray())
        {
            var count = ring.GetArrayLength();
            var coords = new (double X, double Y)[count];
            var ringBuffer = new byte[CheckedBufferSize(4, count, 16)];
            var ringSpan = ringBuffer.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(ringSpan[..4], (uint)count);
            var offset = 4;
            var j = 0;
            foreach (var point in ring.EnumerateArray())
            {
                ReadXy(point, out var x, out var y);
                coords[j++] = (x, y);
                WriteDouble(ringSpan.Slice(offset, 8), x);
                WriteDouble(ringSpan.Slice(offset + 8, 8), y);
                offset += 16;
            }

            result.Add(new RingData(coords, ringBuffer));
        }

        return result;
    }

    // Esri convention: clockwise winding = exterior shell (negative signed area in
    // standard math convention where Y increases upward). Counter-clockwise = hole.
    // The signed shoelace area gives the right sign regardless of CRS orientation.
    private static double SignedArea(ReadOnlySpan<(double X, double Y)> coords)
    {
        if (coords.Length < 3)
        {
            return 0.0;
        }

        var area = 0.0;
        var n = coords.Length;
        for (var i = 0; i < n; i++)
        {
            var j = (i + 1) % n;
            area += coords[i].X * coords[j].Y;
            area -= coords[j].X * coords[i].Y;
        }

        return area / 2.0;
    }

    // Point-in-polygon ray-casting test (returns true when (px,py) is inside ring).
    private static bool PointInRing(double px, double py, ReadOnlySpan<(double X, double Y)> ring)
    {
        var inside = false;
        var n = ring.Length;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var (xi, yi) = ring[i];
            var (xj, yj) = ring[j];
            if ((yi > py) != (yj > py) &&
                px < (xj - xi) * (py - yi) / (yj - yi) + xi)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static byte[] BuildPolygonWkb(List<byte[]> ringBuffers)
    {
        long total = 1 + 4 + 4;
        foreach (var rb in ringBuffers)
        {
            total += rb.Length;
        }

        var buffer = new byte[CheckedTotalSize(total)];
        var span = buffer.AsSpan();
        WriteHeader(span[..5], WkbPolygon);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(5, 4), (uint)ringBuffers.Count);
        var copyOffset = 9;
        foreach (var rb in ringBuffers)
        {
            rb.CopyTo(span.Slice(copyOffset, rb.Length));
            copyOffset += rb.Length;
        }

        return buffer;
    }

    private static byte[] WritePolygonRingsClassified(JsonElement rings)
    {
        var allRings = ReadAllRings(rings);

        // Separate shells (CW, negative signed area) from holes (CCW, positive signed area).
        var shells = new List<(int Index, RingData Ring)>();
        var holes = new List<(int Index, RingData Ring)>();
        for (var i = 0; i < allRings.Count; i++)
        {
            var ring = allRings[i];
            var area = SignedArea(ring.Coords);
            // Negative area → CW → exterior shell in Esri convention.
            // Use <= 0 so that degenerate (zero-area) rings fall into shells.
            if (area <= 0.0)
            {
                shells.Add((i, ring));
            }
            else
            {
                holes.Add((i, ring));
            }
        }

        // If all rings were classified as holes (e.g. a single CCW ring from a non-conformant
        // source), treat all as shells so we still emit valid geometry.
        if (shells.Count == 0)
        {
            for (var i = 0; i < allRings.Count; i++)
            {
                shells.Add((i, allRings[i]));
            }

            holes.Clear();
        }

        // Assign each hole to the first shell whose ring contains the hole's first vertex.
        var holeAssignments = new Dictionary<int, List<byte[]>>();
        foreach (var (_, holeRing) in holes)
        {
            var assigned = -1;
            if (holeRing.Coords.Length > 0)
            {
                var (hx, hy) = holeRing.Coords[0];
                foreach (var (shellIdx, shellRing) in shells)
                {
                    if (PointInRing(hx, hy, shellRing.Coords))
                    {
                        assigned = shellIdx;
                        break;
                    }
                }
            }

            if (assigned < 0 && shells.Count > 0)
            {
                // Fall back to the first shell when no containment match is found.
                assigned = shells[0].Index;
            }

            if (assigned >= 0)
            {
                if (!holeAssignments.TryGetValue(assigned, out var list))
                {
                    list = new List<byte[]>();
                    holeAssignments[assigned] = list;
                }

                list.Add(holeRing.WkbBuffer);
            }
        }

        // Build one WKB Polygon per shell.
        var polygonBuffers = new List<byte[]>(shells.Count);
        foreach (var (shellIdx, shellRing) in shells)
        {
            holeAssignments.TryGetValue(shellIdx, out var assignedHoles);
            var ringBuffers = new List<byte[]>(1 + (assignedHoles?.Count ?? 0));
            ringBuffers.Add(shellRing.WkbBuffer);
            if (assignedHoles is not null)
            {
                ringBuffers.AddRange(assignedHoles);
            }

            polygonBuffers.Add(BuildPolygonWkb(ringBuffers));
        }

        if (polygonBuffers.Count == 1)
        {
            return polygonBuffers[0];
        }

        // Multiple shells → emit WKB MultiPolygon.
        long multiTotal = 1 + 4 + 4;
        foreach (var poly in polygonBuffers)
        {
            multiTotal += poly.Length;
        }

        var multiBuffer = new byte[CheckedTotalSize(multiTotal)];
        var multiSpan = multiBuffer.AsSpan();
        WriteHeader(multiSpan[..5], WkbMultiPolygon);
        BinaryPrimitives.WriteUInt32LittleEndian(multiSpan.Slice(5, 4), (uint)polygonBuffers.Count);
        var multiOffset = 9;
        foreach (var poly in polygonBuffers)
        {
            poly.CopyTo(multiSpan.Slice(multiOffset, poly.Length));
            multiOffset += poly.Length;
        }

        return multiBuffer;
    }

    // Reads an x or y ordinate from a top-level point property.
    // Returns false (empty geometry) when the value is JSON null, the string "NaN",
    // or a non-finite double — matching how ArcGIS REST encodes empty points.
    private static bool TryReadPointOrdinate(JsonElement prop, out double value)
    {
        value = 0.0;

        if (prop.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        if (prop.ValueKind == JsonValueKind.String)
        {
            var s = prop.GetString();
            if (string.Equals(s, "NaN", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!double.TryParse(s, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                return false;
            }

            return double.IsFinite(value);
        }

        value = prop.GetDouble();
        return double.IsFinite(value);
    }

    private static void ReadXy(JsonElement point, out double x, out double y)
    {
        if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() < 2)
        {
            throw new InvalidOperationException(
                "ArcGIS coordinate tuple must be an array of at least two numbers ([x, y]).");
        }

        x = ReadCoordinate(point[0], "x");
        y = ReadCoordinate(point[1], "y");
    }

    /// <summary>
    /// Reads a single coordinate scalar, enforcing the writer's documented
    /// <see cref="InvalidOperationException"/> contract for malformed geometry. A
    /// bare <see cref="JsonElement.GetDouble"/> would surface a non-numeric string
    /// as <see cref="InvalidOperationException"/> but an out-of-range number as
    /// <see cref="FormatException"/>; validating the value kind (and using
    /// <see cref="JsonElement.TryGetDouble"/>) keeps every malformed coordinate on
    /// the documented exception type.
    /// </summary>
    private static double ReadCoordinate(JsonElement value, string axis)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var result))
        {
            throw new InvalidOperationException(
                $"ArcGIS coordinate '{axis}' must be a finite JSON number.");
        }

        return result;
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

    /// <summary>
    /// Computes a WKB buffer length as <c>header + (count * elementSize)</c> using
    /// 64-bit arithmetic and validates it against <see cref="MaxWkbBufferBytes"/>,
    /// so an oversized untrusted coordinate count fails loudly instead of silently
    /// overflowing Int32 into a wrong-sized (or negative) allocation.
    /// </summary>
    internal static int CheckedBufferSize(int headerBytes, int count, int elementSize)
    {
        var total = headerBytes + ((long)count * elementSize);
        if (count < 0 || total > MaxWkbBufferBytes)
        {
            throw new InvalidOperationException(
                $"ArcGIS geometry has {count} coordinates, exceeding the supported WKB buffer size.");
        }

        return (int)total;
    }

    /// <summary>
    /// Validates an already-computed running total (the sum of several sub-buffer
    /// lengths for a multi-part geometry) against <see cref="MaxWkbBufferBytes"/>
    /// before casting back to int for the final allocation. Each sub-buffer is
    /// individually bounded by <see cref="CheckedBufferSize"/>, but the running sum
    /// is accumulated in a long so summing many near-cap parts cannot overflow
    /// Int32 into a wrong-sized (or negative) allocation.
    /// </summary>
    internal static int CheckedTotalSize(long total)
    {
        if (total < 0 || total > MaxWkbBufferBytes)
        {
            throw new InvalidOperationException(
                "ArcGIS multi-part geometry exceeds the supported WKB buffer size.");
        }

        return (int)total;
    }

    private static void WriteHeader(Span<byte> destination, uint wkbType)
    {
        destination[0] = LittleEndian;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[1..], wkbType);
    }

    private static void WriteDouble(Span<byte> destination, double value)
        => BinaryPrimitives.WriteDoubleLittleEndian(destination, value);
}

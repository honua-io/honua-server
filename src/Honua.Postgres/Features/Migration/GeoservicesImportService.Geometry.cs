// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;

namespace Honua.Postgres.Features.Migration;

internal sealed partial class GeoservicesImportService
{
    private static string? ConvertEsriGeometryToGeoJson(JsonElement geometry)
    {
        // Esri JSON geometry format is similar to GeoJSON but not identical
        // This converts common geometry types to GeoJSON
        try
        {
            if (geometry.TryGetProperty("x", out var x) && geometry.TryGetProperty("y", out var y))
            {
                // Point. Carry the Z ordinate through when the source geometry advertises one
                // (Esri places Z on a sibling "z" property for points) so 3D point layers are
                // not silently flattened to 2D. M is not preserved (see ReadCoordinate).
                double? z = geometry.TryGetProperty("z", out var zElement) && zElement.ValueKind == JsonValueKind.Number
                    ? zElement.GetDouble()
                    : null;
                return BuildPointGeoJson(x.GetDouble(), y.GetDouble(), z);
            }

            if (geometry.TryGetProperty("rings", out var rings))
            {
                // Polygon
                var ringCoordinates = rings.EnumerateArray()
                    .Select(ring => ring.EnumerateArray()
                        .Where(coord => coord.GetArrayLength() >= 2)
                        .Select(ReadCoordinate)
                        .ToArray())
                    .Select(EnsureClosedRing)
                    .Where(ring => ring.Length >= 4)
                    .ToArray();

                if (ringCoordinates.Length == 0)
                    return null;

                var polygons = ClassifyPolygonRings(ringCoordinates);
                return polygons.Length == 0 ? null : BuildMultiPolygonGeoJson(polygons);
            }

            if (geometry.TryGetProperty("paths", out var paths))
            {
                // Polyline
                var coordinates = paths.EnumerateArray()
                    .Select(path => path.EnumerateArray()
                        .Where(coord => coord.GetArrayLength() >= 2)
                        .Select(ReadCoordinate)
                        .ToArray())
                    .Where(path => path.Length >= 2)
                    .ToArray();

                if (coordinates.Length == 0)
                    return null;

                return BuildMultiLineStringGeoJson(coordinates);
            }

            if (geometry.TryGetProperty("points", out var points))
            {
                // Multipoint
                var coordinates = points.EnumerateArray()
                    .Where(p => p.GetArrayLength() >= 2)
                    .Select(ReadCoordinate)
                    .ToArray();

                if (coordinates.Length == 0)
                    return null;

                return BuildMultiPointGeoJson(coordinates);
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool HasHigherDimensionCoordinates(JsonElement geometry)
    {
        try
        {
            // Point: check for z property
            if (geometry.TryGetProperty("z", out _))
                return true;

            // Rings (polygon), paths (polyline), points (multipoint): check coordinate array lengths
            foreach (var propName in new[] { "rings", "paths" })
            {
                if (geometry.TryGetProperty(propName, out var arrays))
                {
                    foreach (var array in arrays.EnumerateArray())
                    {
                        foreach (var coord in array.EnumerateArray())
                        {
                            if (coord.GetArrayLength() > 2)
                                return true;
                        }
                    }
                }
            }

            if (geometry.TryGetProperty("points", out var pts))
            {
                foreach (var coord in pts.EnumerateArray())
                {
                    if (coord.GetArrayLength() > 2)
                        return true;
                }
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads an Esri coordinate array into [x, y] or [x, y, z]. The Z ordinate (3rd element)
    /// is preserved when present so 3D source geometry is not flattened to 2D; the M ordinate
    /// (4th element, when a layer is also hasM) is intentionally not carried through because
    /// GeoJSON has no standard place for measure values.
    /// </summary>
    private static double[] ReadCoordinate(JsonElement coord)
    {
        var length = coord.GetArrayLength();
        if (length >= 3 && coord[2].ValueKind == JsonValueKind.Number)
        {
            return [coord[0].GetDouble(), coord[1].GetDouble(), coord[2].GetDouble()];
        }

        return [coord[0].GetDouble(), coord[1].GetDouble()];
    }

    private static string BuildPointGeoJson(double x, double y, double? z = null)
        => BuildGeoJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "Point");
            writer.WritePropertyName("coordinates");
            writer.WriteStartArray();
            writer.WriteNumberValue(x);
            writer.WriteNumberValue(y);
            if (z.HasValue)
            {
                writer.WriteNumberValue(z.Value);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    private static double[][] EnsureClosedRing(double[][] ring)
    {
        if (ring.Length == 0)
        {
            return ring;
        }

        var first = ring[0];
        var last = ring[^1];
        if (first.Length >= 2 && last.Length >= 2 && first[0] == last[0] && first[1] == last[1])
        {
            return ring;
        }

        var closed = new double[ring.Length + 1][];
        Array.Copy(ring, closed, ring.Length);
        // Close the ring with a copy of the first vertex, preserving its full dimensionality
        // (including Z) so a 3D ring's closing point matches its start point.
        closed[^1] = (double[])first.Clone();
        return closed;
    }

    private static double[][][][] ClassifyPolygonRings(double[][][] rings)
    {
        var classified = rings
            .Select(ring => new EsriPolygonRing(ring, SignedRingArea(ring)))
            .Where(ring => ring.AbsArea > 0)
            .ToArray();

        if (classified.Length == 0)
        {
            return [];
        }

        for (var i = 0; i < classified.Length; i++)
        {
            var parentIndex = -1;
            var parentArea = double.PositiveInfinity;

            for (var j = 0; j < classified.Length; j++)
            {
                if (i == j || classified[j].AbsArea <= classified[i].AbsArea)
                {
                    continue;
                }

                if (classified[j].AbsArea < parentArea &&
                    RingContainsPoint(classified[j].Coordinates, classified[i].Coordinates[0]))
                {
                    parentIndex = j;
                    parentArea = classified[j].AbsArea;
                }
            }

            classified[i].ParentIndex = parentIndex;
        }

        for (var i = 0; i < classified.Length; i++)
        {
            var depth = 0;
            var parentIndex = classified[i].ParentIndex;
            while (parentIndex >= 0)
            {
                depth++;
                parentIndex = classified[parentIndex].ParentIndex;
            }

            classified[i].Depth = depth;
        }

        var polygons = new List<double[][][]>();
        var shellIndexes = classified
            .Select((ring, index) => (Ring: ring, Index: index))
            .Where(item => item.Ring.Depth % 2 == 0)
            .OrderBy(item => item.Index)
            .ToArray();

        foreach (var shell in shellIndexes)
        {
            var polygonRings = new List<double[][]>
            {
                EnsureCounterClockwise(shell.Ring.Coordinates)
            };

            for (var i = 0; i < classified.Length; i++)
            {
                if (classified[i].Depth % 2 == 0)
                {
                    continue;
                }

                if (FindNearestEvenDepthAncestor(classified, i) == shell.Index)
                {
                    polygonRings.Add(EnsureClockwise(classified[i].Coordinates));
                }
            }

            polygons.Add(polygonRings.ToArray());
        }

        return polygons.ToArray();
    }

    private static int FindNearestEvenDepthAncestor(EsriPolygonRing[] rings, int ringIndex)
    {
        var parentIndex = rings[ringIndex].ParentIndex;
        while (parentIndex >= 0)
        {
            if (rings[parentIndex].Depth % 2 == 0)
            {
                return parentIndex;
            }

            parentIndex = rings[parentIndex].ParentIndex;
        }

        return -1;
    }

    private static double SignedRingArea(double[][] ring)
    {
        var area = 0d;
        for (var i = 0; i < ring.Length - 1; i++)
        {
            area += (ring[i][0] * ring[i + 1][1]) - (ring[i + 1][0] * ring[i][1]);
        }

        return area / 2d;
    }

    private static bool RingContainsPoint(double[][] ring, double[] point)
    {
        var inside = false;
        var x = point[0];
        var y = point[1];

        for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
        {
            var xi = ring[i][0];
            var yi = ring[i][1];
            var xj = ring[j][0];
            var yj = ring[j][1];

            if ((yi > y) != (yj > y) &&
                x < ((xj - xi) * (y - yi) / (yj - yi)) + xi)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static double[][] EnsureCounterClockwise(double[][] ring)
        => SignedRingArea(ring) < 0 ? ReverseRing(ring) : ring;

    private static double[][] EnsureClockwise(double[][] ring)
        => SignedRingArea(ring) > 0 ? ReverseRing(ring) : ring;

    private static double[][] ReverseRing(double[][] ring)
    {
        // Preserve each vertex's full dimensionality (including Z) when reversing winding order.
        var reversed = ring.Reverse()
            .Select(coord => (double[])coord.Clone())
            .ToArray();
        return EnsureClosedRing(reversed);
    }

    private sealed class EsriPolygonRing
    {
        public EsriPolygonRing(double[][] coordinates, double signedArea)
        {
            Coordinates = coordinates;
            SignedArea = signedArea;
            AbsArea = Math.Abs(signedArea);
        }

        public double[][] Coordinates { get; }
        public double SignedArea { get; }
        public double AbsArea { get; }
        public int ParentIndex { get; set; } = -1;
        public int Depth { get; set; }
    }

    private static string BuildMultiPolygonGeoJson(double[][][][] polygons)
        => BuildGeoJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "MultiPolygon");
            writer.WritePropertyName("coordinates");
            writer.WriteStartArray();
            foreach (var polygon in polygons)
            {
                writer.WriteStartArray();
                foreach (var ring in polygon)
                {
                    writer.WriteStartArray();
                    foreach (var coord in ring)
                    {
                        WriteCoordinate(writer, coord);
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    private static string BuildMultiLineStringGeoJson(double[][][] lines)
        => BuildGeoJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "MultiLineString");
            writer.WritePropertyName("coordinates");
            writer.WriteStartArray();
            foreach (var line in lines)
            {
                writer.WriteStartArray();
                foreach (var coord in line)
                {
                    WriteCoordinate(writer, coord);
                }
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    private static string BuildMultiPointGeoJson(double[][] points)
        => BuildGeoJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "MultiPoint");
            writer.WritePropertyName("coordinates");
            writer.WriteStartArray();
            foreach (var point in points)
            {
                WriteCoordinate(writer, point);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    /// <summary>
    /// Writes a single coordinate as a GeoJSON array, emitting the Z ordinate when the source
    /// coordinate carries one so 3D geometry survives the conversion.
    /// </summary>
    private static void WriteCoordinate(Utf8JsonWriter writer, double[] coord)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(coord[0]);
        writer.WriteNumberValue(coord[1]);
        if (coord.Length >= 3)
        {
            writer.WriteNumberValue(coord[2]);
        }
        writer.WriteEndArray();
    }

    private static string BuildGeoJson(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        write(writer);
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

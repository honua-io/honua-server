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
    internal static string? ConvertEsriGeometryToGeoJson(JsonElement geometry)
    {
        // Esri JSON geometry format is similar to GeoJSON but not identical
        // This converts common geometry types to GeoJSON
        try
        {
            // Esri geometries carry explicit hasZ/hasM flags. When hasZ is false but
            // hasM is true, the third ordinate in each coordinate is M, NOT Z — so we
            // must not promote it to GeoJSON Z. When the flag is absent (older/partial
            // payloads) we keep the historical behavior of treating index 2 as Z.
            var coordsCarryZ = !geometry.TryGetProperty("hasZ", out var hasZFlag)
                || hasZFlag.ValueKind != JsonValueKind.False;

            if (geometry.TryGetProperty("x", out var x) && geometry.TryGetProperty("y", out var y))
            {
                // Point. Carry the optional Z so elevation-enabled ArcGIS REST
                // points round-trip instead of being silently flattened to 2D (#1981).
                double? z = geometry.TryGetProperty("z", out var zElement)
                    && zElement.ValueKind == JsonValueKind.Number
                    ? zElement.GetDouble()
                    : null;
                return BuildPointGeoJson(x.GetDouble(), y.GetDouble(), z);
            }

            if (geometry.TryGetProperty("rings", out var rings))
            {
                // Polygon. Carry the optional Z (coord[2]) through ring extraction,
                // closing, classification, and orientation so elevation-enabled
                // ArcGIS REST polygons round-trip instead of flattening to 2-D (#1981).
                // The ring math (signed area, point-in-ring, orientation) reads only
                // X/Y (indices 0/1), so longer coordinate arrays pass through intact.
                // Esri M is intentionally dropped on this path: the converter targets
                // GeoJSON consumed by ST_GeomFromGeoJSON, which (per the GeoJSON spec)
                // has no M ordinate. The file-import path (FileGDB/shapefile) carries M
                // through WKB instead.
                var ringCoordinates = rings.EnumerateArray()
                    .Select(ring => ring.EnumerateArray()
                        .Where(coord => coord.GetArrayLength() >= 2)
                        .Select(coord => ExtractXyz(coord, coordsCarryZ))
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
                // Polyline. Carry the optional Z (coord[2]) so elevation-enabled
                // ArcGIS REST lines round-trip instead of flattening to 2D (#1981).
                var coordinates = paths.EnumerateArray()
                    .Select(path => path.EnumerateArray()
                        .Where(coord => coord.GetArrayLength() >= 2)
                        .Select(coord => ExtractXyz(coord, coordsCarryZ))
                        .ToArray())
                    .Where(path => path.Length >= 2)
                    .ToArray();

                if (coordinates.Length == 0)
                    return null;

                return BuildMultiLineStringGeoJson(coordinates);
            }

            if (geometry.TryGetProperty("points", out var points))
            {
                // Multipoint. Carry the optional Z (coord[2]) so elevation-enabled
                // ArcGIS REST multipoints round-trip instead of flattening to 2D (#1981).
                var coordinates = points.EnumerateArray()
                    .Where(p => p.GetArrayLength() >= 2)
                    .Select(coord => ExtractXyz(coord, coordsCarryZ))
                    .ToArray();

                if (coordinates.Length == 0)
                    return null;

                return BuildMultiPointGeoJson(coordinates);
            }

            return null;
        }
        catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
        {
            // Intentionally generic: malformed/unrecognized Esri geometry JSON should return null
            // like the "no recognized shape" path above, not crash the import. The caller
            // (InsertFeaturesAsync) already logs GeometryConversionFailed whenever this returns
            // null, so the failure is surfaced without duplicating it here.
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
            // codeql[cs/linq/missed-where] -- predicate binds state or awaits; retain imperative control flow.
            foreach (var propName in new[] { "rings", "paths" })
            {
                if (geometry.TryGetProperty(propName, out var arrays) &&
                    arrays.EnumerateArray().Any(array => array.EnumerateArray().Any(coord => coord.GetArrayLength() > 2)))
                {
                    return true;
                }
            }

            if (geometry.TryGetProperty("points", out var pts) &&
                pts.EnumerateArray().Any(coord => coord.GetArrayLength() > 2))
            {
                return true;
            }

            return false;
        }
        catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
        {
            // Intentionally generic: this only feeds a diagnostic higher-dimension-count statistic,
            // not the actual geometry insert path; malformed geometry JSON here safely degrades to
            // "no Z observed" rather than failing the import (the geometry conversion itself has its
            // own error handling/logging).
            return false;
        }
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
            if (z.HasValue && !double.IsNaN(z.Value))
            {
                writer.WriteNumberValue(z.Value);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    /// <summary>
    /// Projects an Esri coordinate array to <c>[x, y]</c> or <c>[x, y, z]</c>,
    /// preserving the optional Z ordinate (index 2) when present and finite.
    /// When <paramref name="carryZ"/> is <c>false</c> the third ordinate is an M
    /// (measure) value rather than Z and is not promoted to GeoJSON Z (#1981).
    /// </summary>
    private static double[] ExtractXyz(JsonElement coord, bool carryZ = true)
    {
        var x = coord[0].GetDouble();
        var y = coord[1].GetDouble();
        if (carryZ && coord.GetArrayLength() >= 3 && coord[2].ValueKind == JsonValueKind.Number)
        {
            var z = coord[2].GetDouble();
            if (!double.IsNaN(z))
            {
                return [x, y, z];
            }
        }

        return [x, y];
    }

    /// <summary>
    /// Writes a single GeoJSON coordinate, emitting the Z ordinate when the
    /// source coordinate carries one (length &gt;= 3).
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

    // Esri JSON coordinates round-trip through text/JSON parsing, so the "same" vertex
    // repeated as the ring's first and last point can differ by a few ULPs. Compare
    // within a tight tolerance rather than exact float equality to avoid appending a
    // near-duplicate closing vertex to an already-closed ring.
    private const double RingClosureTolerance = 1e-9;

    private static double[][] EnsureClosedRing(double[][] ring)
    {
        if (ring.Length == 0)
        {
            return ring;
        }

        var first = ring[0];
        var last = ring[^1];
        if (first.Length >= 2 && last.Length >= 2 &&
            Math.Abs(first[0] - last[0]) < RingClosureTolerance &&
            Math.Abs(first[1] - last[1]) < RingClosureTolerance)
        {
            return ring;
        }

        var closed = new double[ring.Length + 1][];
        Array.Copy(ring, closed, ring.Length);
        // Duplicate the first vertex verbatim (including any Z) to close the ring.
        closed[^1] = first;
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
        // Preserve each vertex verbatim (including any Z) while reversing winding,
        // so polygon orientation fixes don't drop the elevation ordinate (#1981).
        var reversed = ring.Reverse().ToArray();
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

    private static string BuildGeoJson(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        write(writer);
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

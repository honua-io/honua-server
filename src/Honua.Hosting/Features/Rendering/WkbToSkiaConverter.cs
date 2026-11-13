// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using SkiaSharp;

namespace Honua.Infrastructure.Rendering;

internal static class WkbToSkiaConverter
{
    // WKB geometry types
    private const int WkbPoint = 1;
    private const int WkbLineString = 2;
    private const int WkbPolygon = 3;
    private const int WkbMultiPoint = 4;
    private const int WkbMultiLineString = 5;
    private const int WkbMultiPolygon = 6;
    private const int WkbGeometryCollection = 7;

    private const int MaxGeometryNestingDepth = 32;

    private const int MinNestedPointBytes = 21;

    internal readonly struct GeometryConversionResult
    {
        public SKPath? Path { get; init; }

        public SKPoint[]? Points { get; init; }

        public bool IsPoint { get; init; }

        public bool IsPolygon { get; init; }
    }

    public static GeometryConversionResult Convert(byte[] wkb, Func<double, double, SKPoint> transform)
    {
        if (wkb == null || wkb.Length < 5)
        {
            return default;
        }

        try
        {
            var reader = new WkbReader(wkb);
            return ReadGeometry(ref reader, transform);
        }
        catch (InvalidDataException)
        {
            return default;
        }
    }

    public static bool TryConvertPoint(byte[]? wkb, Func<double, double, SKPoint> transform, out SKPoint point)
    {
        point = default;

        if (wkb == null || wkb.Length < 21)
        {
            return false;
        }

        var reader = new WkbReader(wkb);
        var byteOrder = reader.ReadByte();
        reader.IsLittleEndian = byteOrder == 1;

        var rawType = reader.ReadInt32();

        var hasSrid = (rawType & 0x20000000) != 0;
        var hasZ = (rawType & unchecked((int)0x80000000)) != 0;
        var hasM = (rawType & 0x40000000) != 0;

        var geometryType = rawType & 0xFFFF;

        if (geometryType >= 3000 && geometryType < 4000)
        {
            hasZ = true;
            hasM = true;
            geometryType -= 3000;
        }
        else if (geometryType >= 2000 && geometryType < 3000)
        {
            hasM = true;
            geometryType -= 2000;
        }
        else if (geometryType >= 1000 && geometryType < 2000)
        {
            hasZ = true;
            geometryType -= 1000;
        }

        if (geometryType != WkbPoint)
        {
            return false;
        }

        if (hasSrid)
        {
            if (wkb.Length < 25)
            {
                return false;
            }

            reader.ReadInt32();
        }

        var expectedLength = 1 + 4 + (hasSrid ? 4 : 0) + 16 + ((hasZ ? 1 : 0) + (hasM ? 1 : 0)) * 8;
        if (wkb.Length < expectedLength)
        {
            return false;
        }

        var (x, y) = reader.ReadCoordinate();
        point = transform(x, y);
        return true;
    }

    private static GeometryConversionResult ReadGeometry(ref WkbReader reader, Func<double, double, SKPoint> transform, int depth = 0)
    {
        if (depth > MaxGeometryNestingDepth)
        {
            throw new InvalidDataException("WKB geometry nesting exceeds the supported depth.");
        }

        var byteOrder = reader.ReadByte();
        reader.IsLittleEndian = byteOrder == 1;

        var rawType = reader.ReadInt32();

        // Detect EWKB flags (PostGIS extended WKB format)
        var hasSrid = (rawType & 0x20000000) != 0;
        var hasZ = (rawType & unchecked((int)0x80000000)) != 0;
        var hasM = (rawType & 0x40000000) != 0;

        // Mask out EWKB flag bits to get the base type
        var geometryType = rawType & 0xFFFF;

        // Handle ISO WKB type ranges: 1000-1999 = Z, 2000-2999 = M, 3000-3999 = ZM
        if (geometryType >= 3000 && geometryType < 4000)
        {
            hasZ = true;
            hasM = true;
            geometryType -= 3000;
        }
        else if (geometryType >= 2000 && geometryType < 3000)
        {
            hasM = true;
            geometryType -= 2000;
        }
        else if (geometryType >= 1000 && geometryType < 2000)
        {
            hasZ = true;
            geometryType -= 1000;
        }

        if (hasSrid)
        {
            reader.ReadInt32(); // skip SRID
        }

        var dimensions = 2 + (hasZ ? 1 : 0) + (hasM ? 1 : 0);

        return geometryType switch
        {
            WkbPoint => ReadPoint(ref reader, transform, dimensions),
            WkbLineString => ReadLineString(ref reader, transform, dimensions),
            WkbPolygon => ReadPolygon(ref reader, transform, dimensions),
            WkbMultiPoint => ReadMultiPoint(ref reader, transform, dimensions, depth),
            WkbMultiLineString => ReadMultiLineString(ref reader, transform, dimensions, depth),
            WkbMultiPolygon => ReadMultiPolygon(ref reader, transform, dimensions, depth),
            WkbGeometryCollection => ReadGeometryCollection(ref reader, transform, depth),
            _ => default
        };
    }

    // A negative element count is rejected; an oversized count is left to the later bounds check, and a preallocation must be clamped on its own.
    private static int ReadElementCount(ref WkbReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0)
        {
            throw new InvalidDataException("WKB element count is negative.");
        }

        return count;
    }

    private static GeometryConversionResult ReadPoint(ref WkbReader reader, Func<double, double, SKPoint> transform, int dimensions)
    {
        var (x, y) = reader.ReadCoordinate();
        SkipExtraDimensions(ref reader, dimensions);

        var point = transform(x, y);
        return new GeometryConversionResult
        {
            Points = [point],
            IsPoint = true
        };
    }

    private static GeometryConversionResult ReadLineString(ref WkbReader reader, Func<double, double, SKPoint> transform, int dimensions)
    {
        var numPoints = ReadElementCount(ref reader);
        if (numPoints == 0)
        {
            return default;
        }

        var path = new SKPath();
        try
        {
            var first = true;
            for (int i = 0; i < numPoints; i++)
            {
                var (x, y) = reader.ReadCoordinate();
                SkipExtraDimensions(ref reader, dimensions);

                var point = transform(x, y);
                if (first)
                {
                    path.MoveTo(point);
                    first = false;
                }
                else
                {
                    path.LineTo(point);
                }
            }

            return new GeometryConversionResult { Path = path };
        }
        catch
        {
            path.Dispose();
            throw;
        }
    }

    private static GeometryConversionResult ReadPolygon(ref WkbReader reader, Func<double, double, SKPoint> transform, int dimensions)
    {
        var numRings = ReadElementCount(ref reader);
        if (numRings == 0)
        {
            return default;
        }

        var path = new SKPath();
        try
        {
            for (int ring = 0; ring < numRings; ring++)
            {
                var numPoints = ReadElementCount(ref reader);
                if (numPoints == 0)
                {
                    continue;
                }

                var first = true;
                for (int i = 0; i < numPoints; i++)
                {
                    var (x, y) = reader.ReadCoordinate();
                    SkipExtraDimensions(ref reader, dimensions);

                    var point = transform(x, y);
                    if (first)
                    {
                        path.MoveTo(point);
                        first = false;
                    }
                    else
                    {
                        path.LineTo(point);
                    }
                }

                path.Close();
            }

            return new GeometryConversionResult { Path = path, IsPolygon = true };
        }
        catch
        {
            path.Dispose();
            throw;
        }
    }

    private static GeometryConversionResult ReadMultiPoint(ref WkbReader reader, Func<double, double, SKPoint> transform, int dimensions, int depth)
    {
        var numGeometries = ReadElementCount(ref reader);
        // Clamp list preallocation: a hostile payload can declare a huge count but only carry
        // MinNestedPointBytes bytes per nested geometry; cap to what the remaining buffer can
        // possibly hold, so we never allocate a multi-GB backing array.
        var remaining = reader.RemainingBytes;
        var safeCapacity = remaining > 0
            ? Math.Min(numGeometries, remaining / MinNestedPointBytes + 1)
            : numGeometries;
        var points = new List<SKPoint>(safeCapacity);

        for (int i = 0; i < numGeometries; i++)
        {
            var result = ReadGeometry(ref reader, transform, depth + 1);
            if (result.Points != null)
            {
                points.AddRange(result.Points);
            }
        }

        return new GeometryConversionResult
        {
            Points = [.. points],
            IsPoint = true
        };
    }

    private static GeometryConversionResult ReadMultiLineString(ref WkbReader reader, Func<double, double, SKPoint> transform, int dimensions, int depth)
    {
        var numGeometries = ReadElementCount(ref reader);
        var combinedPath = new SKPath();
        try
        {
            for (int i = 0; i < numGeometries; i++)
            {
                var result = ReadGeometry(ref reader, transform, depth + 1);
                using var resultPath = result.Path;
                if (resultPath != null)
                {
                    combinedPath.AddPath(resultPath);
                }
            }

            return new GeometryConversionResult { Path = combinedPath };
        }
        catch
        {
            combinedPath.Dispose();
            throw;
        }
    }

    private static GeometryConversionResult ReadMultiPolygon(ref WkbReader reader, Func<double, double, SKPoint> transform, int dimensions, int depth)
    {
        var numGeometries = ReadElementCount(ref reader);
        var combinedPath = new SKPath();
        try
        {
            for (int i = 0; i < numGeometries; i++)
            {
                var result = ReadGeometry(ref reader, transform, depth + 1);
                using var resultPath = result.Path;
                if (resultPath != null)
                {
                    combinedPath.AddPath(resultPath);
                }
            }

            return new GeometryConversionResult { Path = combinedPath, IsPolygon = true };
        }
        catch
        {
            combinedPath.Dispose();
            throw;
        }
    }

    private static GeometryConversionResult ReadGeometryCollection(ref WkbReader reader, Func<double, double, SKPoint> transform, int depth)
    {
        var numGeometries = ReadElementCount(ref reader);
        SKPath? combinedPath = new SKPath();
        try
        {
            var allPoints = new List<SKPoint>();
            var isPolygon = false;
            var isPoint = true;

            for (int i = 0; i < numGeometries; i++)
            {
                var result = ReadGeometry(ref reader, transform, depth + 1);
                using var resultPath = result.Path;
                if (resultPath != null)
                {
                    combinedPath.AddPath(resultPath);
                    isPoint = false;
                }

                if (result.Points != null)
                {
                    allPoints.AddRange(result.Points);
                }

                if (result.IsPolygon)
                {
                    isPolygon = true;
                }

                if (!result.IsPoint)
                {
                    isPoint = false;
                }
            }

            if (combinedPath.PointCount == 0)
            {
                combinedPath.Dispose();
                combinedPath = null;
            }

            return new GeometryConversionResult
            {
                Path = combinedPath,
                Points = allPoints.Count > 0 ? [.. allPoints] : null,
                IsPoint = isPoint && allPoints.Count > 0,
                IsPolygon = isPolygon
            };
        }
        catch
        {
            combinedPath?.Dispose();
            throw;
        }
    }

    private static void SkipExtraDimensions(ref WkbReader reader, int dimensions)
    {
        for (int i = 2; i < dimensions; i++)
        {
            reader.ReadDouble();
        }
    }

}

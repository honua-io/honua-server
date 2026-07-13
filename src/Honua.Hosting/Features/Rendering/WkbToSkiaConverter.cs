// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using SkiaSharp;

namespace Honua.Infrastructure.Rendering;

/// <summary>
/// Converts WKB (Well-Known Binary) geometry to SkiaSharp paths in pixel coordinates.
/// Handles coordinate transformation from geographic/projected coordinates to pixel space.
/// </summary>
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

    /// <summary>
    /// Maximum nesting depth for multi-geometries / geometry collections. Guards against
    /// hostile WKB payloads that drive unbounded recursion during tile rendering.
    /// </summary>
    private const int MaxGeometryNestingDepth = 32;

    /// <summary>
    /// Minimum encoded size of a nested point geometry (byte order + type + x + y).
    /// Used to clamp list preallocation from untrusted element counts.
    /// </summary>
    private const int MinNestedPointBytes = 21;

    /// <summary>
    /// Result of WKB geometry conversion, containing paths and/or point locations.
    /// </summary>
    internal readonly struct GeometryConversionResult
    {
        /// <summary>
        /// SkiaSharp path for lines and polygons.
        /// </summary>
        public SKPath? Path { get; init; }

        /// <summary>
        /// Point locations for point geometries (rendered as circles).
        /// </summary>
        public SKPoint[]? Points { get; init; }

        /// <summary>
        /// Whether this is a point geometry.
        /// </summary>
        public bool IsPoint { get; init; }

        /// <summary>
        /// Whether this is a polygon geometry (needs fill + stroke).
        /// </summary>
        public bool IsPolygon { get; init; }
    }

    /// <summary>
    /// Converts WKB geometry to SkiaSharp drawing primitives using the given coordinate transform.
    /// </summary>
    /// <param name="wkb">WKB geometry bytes</param>
    /// <param name="transform">Coordinate-to-pixel transform function</param>
    /// <returns>Conversion result with paths and/or points</returns>
    public static GeometryConversionResult Convert(byte[] wkb, Func<double, double, SKPoint> transform)
    {
        if (wkb == null || wkb.Length < 5)
        {
            return default;
        }

        try
        {
            var reader = new WkbReader(wkb);
            return ReadGeometry(reader, transform);
        }
        catch (InvalidDataException)
        {
            return default;
        }
    }

    /// <summary>
    /// Fast path for simple point geometries used by dense point-layer raster rendering.
    /// Returns false for non-point or malformed geometries.
    /// </summary>
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

        var x = reader.ReadDouble();
        var y = reader.ReadDouble();
        point = transform(x, y);
        return true;
    }

    private static GeometryConversionResult ReadGeometry(WkbReader reader, Func<double, double, SKPoint> transform, int depth = 0)
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
            WkbPoint => ReadPoint(reader, transform, dimensions),
            WkbLineString => ReadLineString(reader, transform, dimensions),
            WkbPolygon => ReadPolygon(reader, transform, dimensions),
            WkbMultiPoint => ReadMultiPoint(reader, transform, dimensions, depth),
            WkbMultiLineString => ReadMultiLineString(reader, transform, dimensions, depth),
            WkbMultiPolygon => ReadMultiPolygon(reader, transform, dimensions, depth),
            WkbGeometryCollection => ReadGeometryCollection(reader, transform, depth),
            _ => default
        };
    }

    /// <summary>
    /// Reads an element count, rejecting negative values from corrupted or hostile payloads.
    /// Oversized counts are caught later by the reader's bounds checks; preallocation from
    /// counts must be clamped separately (see <see cref="ReadMultiPoint"/>).
    /// </summary>
    private static int ReadElementCount(WkbReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0)
        {
            throw new InvalidDataException("WKB element count is negative.");
        }

        return count;
    }

    private static GeometryConversionResult ReadPoint(WkbReader reader, Func<double, double, SKPoint> transform, int dimensions)
    {
        var x = reader.ReadDouble();
        var y = reader.ReadDouble();
        SkipExtraDimensions(reader, dimensions);

        var point = transform(x, y);
        return new GeometryConversionResult
        {
            Points = [point],
            IsPoint = true
        };
    }

    private static GeometryConversionResult ReadLineString(WkbReader reader, Func<double, double, SKPoint> transform, int dimensions)
    {
        var numPoints = ReadElementCount(reader);
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
                var x = reader.ReadDouble();
                var y = reader.ReadDouble();
                SkipExtraDimensions(reader, dimensions);

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

    private static GeometryConversionResult ReadPolygon(WkbReader reader, Func<double, double, SKPoint> transform, int dimensions)
    {
        var numRings = ReadElementCount(reader);
        if (numRings == 0)
        {
            return default;
        }

        var path = new SKPath();
        try
        {
            for (int ring = 0; ring < numRings; ring++)
            {
                var numPoints = ReadElementCount(reader);
                if (numPoints == 0)
                {
                    continue;
                }

                var first = true;
                for (int i = 0; i < numPoints; i++)
                {
                    var x = reader.ReadDouble();
                    var y = reader.ReadDouble();
                    SkipExtraDimensions(reader, dimensions);

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

    private static GeometryConversionResult ReadMultiPoint(WkbReader reader, Func<double, double, SKPoint> transform, int dimensions, int depth)
    {
        var numGeometries = ReadElementCount(reader);
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
            var result = ReadGeometry(reader, transform, depth + 1);
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

    private static GeometryConversionResult ReadMultiLineString(WkbReader reader, Func<double, double, SKPoint> transform, int dimensions, int depth)
    {
        var numGeometries = ReadElementCount(reader);
        var combinedPath = new SKPath();
        try
        {
            for (int i = 0; i < numGeometries; i++)
            {
                var result = ReadGeometry(reader, transform, depth + 1);
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

    private static GeometryConversionResult ReadMultiPolygon(WkbReader reader, Func<double, double, SKPoint> transform, int dimensions, int depth)
    {
        var numGeometries = ReadElementCount(reader);
        var combinedPath = new SKPath();
        try
        {
            for (int i = 0; i < numGeometries; i++)
            {
                var result = ReadGeometry(reader, transform, depth + 1);
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

    private static GeometryConversionResult ReadGeometryCollection(WkbReader reader, Func<double, double, SKPoint> transform, int depth)
    {
        var numGeometries = ReadElementCount(reader);
        SKPath? combinedPath = new SKPath();
        try
        {
            var allPoints = new List<SKPoint>();
            var isPolygon = false;
            var isPoint = true;

            for (int i = 0; i < numGeometries; i++)
            {
                var result = ReadGeometry(reader, transform, depth + 1);
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

    private static void SkipExtraDimensions(WkbReader reader, int dimensions)
    {
        for (int i = 2; i < dimensions; i++)
        {
            reader.ReadDouble();
        }
    }

    /// <summary>
    /// Binary reader for WKB that handles byte order.
    /// </summary>
    internal sealed class WkbReader
    {
        private readonly byte[] _data;
        private int _position;

        public bool IsLittleEndian { get; set; }

        /// <summary>
        /// Bytes remaining in the buffer from the current read position.
        /// </summary>
        public int RemainingBytes => _data.Length - _position;

        public WkbReader(byte[] data)
        {
            _data = data;
            _position = 0;
        }

        public byte ReadByte()
        {
            EnsureCanRead(sizeof(byte));
            return _data[_position++];
        }

        public int ReadInt32()
        {
            EnsureCanRead(sizeof(int));
            var value = IsLittleEndian
                ? BitConverter.ToInt32(_data, _position)
                : BinaryPrimitives_ReadInt32BigEndian(_data.AsSpan(_position));
            _position += 4;
            return value;
        }

        public double ReadDouble()
        {
            EnsureCanRead(sizeof(double));
            var value = IsLittleEndian
                ? BitConverter.ToDouble(_data, _position)
                : BinaryPrimitives_ReadDoubleBigEndian(_data.AsSpan(_position));
            _position += 8;
            return value;
        }

        private void EnsureCanRead(int byteCount)
        {
            if (_position > _data.Length - byteCount)
            {
                throw new InvalidDataException("WKB payload ended unexpectedly.");
            }
        }

        private static int BinaryPrimitives_ReadInt32BigEndian(ReadOnlySpan<byte> source)
        {
            return (source[0] << 24) | (source[1] << 16) | (source[2] << 8) | source[3];
        }

        private static double BinaryPrimitives_ReadDoubleBigEndian(ReadOnlySpan<byte> source)
        {
            var bytes = new byte[8];
            for (int i = 0; i < 8; i++)
            {
                bytes[7 - i] = source[i];
            }

            return BitConverter.ToDouble(bytes, 0);
        }
    }
}

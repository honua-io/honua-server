// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using SkiaSharp;

namespace Honua.Infrastructure.Rendering;

internal ref partial struct WkbToSkiaDecoder(ReadOnlySpan<byte> wkb, Func<double, double, SKPoint> transform)
{
    private const int WkbPoint = 1;
    private const int WkbLineString = 2;
    private const int WkbPolygon = 3;
    private const int WkbMultiPoint = 4;
    private const int WkbMultiLineString = 5;
    private const int WkbMultiPolygon = 6;
    private const int WkbGeometryCollection = 7;
    private const int MaxGeometryNestingDepth = 32;
    private const int MinNestedPointBytes = 21;

    private WkbReader _reader = new(wkb);
    private readonly Func<double, double, SKPoint> _transform = transform;

    internal WkbToSkiaConverter.GeometryConversionResult ReadGeometry(int depth = 0)
    {
        EnsureDepth(depth);
        var header = ReadGeometryHeader();
        return ReadGeometryBody(header, depth);
    }

    private static void EnsureDepth(int depth)
    {
        if (depth > MaxGeometryNestingDepth)
        {
            throw new InvalidDataException("WKB geometry nesting exceeds the supported depth.");
        }
    }

    private WkbGeometryHeader ReadGeometryHeader()
    {
        var byteOrder = _reader.ReadByte();
        _reader.IsLittleEndian = byteOrder == 1;

        var rawType = _reader.ReadInt32();
        var hasSrid = (rawType & 0x20000000) != 0;
        var hasZ = (rawType & unchecked((int)0x80000000)) != 0;
        var hasM = (rawType & 0x40000000) != 0;
        var geometryType = rawType & 0xFFFF;

        if (geometryType is >= 3000 and < 4000)
        {
            hasZ = true;
            hasM = true;
            geometryType -= 3000;
        }
        else if (geometryType is >= 2000 and < 3000)
        {
            hasM = true;
            geometryType -= 2000;
        }
        else if (geometryType is >= 1000 and < 2000)
        {
            hasZ = true;
            geometryType -= 1000;
        }

        if (hasSrid)
        {
            _reader.ReadInt32();
        }

        var dimensions = 2 + (hasZ ? 1 : 0) + (hasM ? 1 : 0);
        return new WkbGeometryHeader(geometryType, dimensions);
    }

    private WkbToSkiaConverter.GeometryConversionResult ReadGeometryBody(WkbGeometryHeader header, int depth)
        => header.GeometryType switch
        {
            WkbPoint => ReadPoint(header.Dimensions),
            WkbLineString => ReadLineString(header.Dimensions),
            WkbPolygon => ReadPolygon(header.Dimensions),
            WkbMultiPoint => ReadMultiPoint(depth),
            WkbMultiLineString => ReadMultiLineString(depth),
            WkbMultiPolygon => ReadMultiPolygon(depth),
            WkbGeometryCollection => ReadGeometryCollection(depth),
            _ => default
        };

    private int ReadElementCount()
    {
        var count = _reader.ReadInt32();
        if (count < 0)
        {
            throw new InvalidDataException("WKB element count is negative.");
        }

        return count;
    }

    private WkbToSkiaConverter.GeometryConversionResult ReadPoint(int dimensions)
    {
        var (x, y) = _reader.ReadCoordinate();
        SkipExtraDimensions(dimensions);
        return new WkbToSkiaConverter.GeometryConversionResult
        {
            Points = [_transform(x, y)],
            IsPoint = true
        };
    }

    private WkbToSkiaConverter.GeometryConversionResult ReadLineString(int dimensions)
    {
        var numPoints = ReadElementCount();
        if (numPoints == 0)
        {
            return default;
        }

        var path = new SKPath();
        try
        {
            AppendLineStringPointsToPath(path, numPoints, dimensions);
            return new WkbToSkiaConverter.GeometryConversionResult { Path = path };
        }
        catch
        {
            path.Dispose();
            throw;
        }
    }

    private void AppendLineStringToPath(SKPath path, int dimensions)
    {
        var numPoints = ReadElementCount();
        if (numPoints > 0)
        {
            AppendLineStringPointsToPath(path, numPoints, dimensions);
        }
    }

    private void AppendLineStringPointsToPath(SKPath path, int numPoints, int dimensions)
    {
        for (var i = 0; i < numPoints; i++)
        {
            var (x, y) = _reader.ReadCoordinate();
            SkipExtraDimensions(dimensions);

            var point = _transform(x, y);
            if (i == 0)
            {
                path.MoveTo(point);
            }
            else
            {
                path.LineTo(point);
            }
        }
    }

    private WkbToSkiaConverter.GeometryConversionResult ReadPolygon(int dimensions)
    {
        var numRings = ReadElementCount();
        if (numRings == 0)
        {
            return default;
        }

        var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        try
        {
            AppendPolygonRingsToPath(path, numRings, dimensions);
            return new WkbToSkiaConverter.GeometryConversionResult { Path = path, IsPolygon = true };
        }
        catch
        {
            path.Dispose();
            throw;
        }
    }

    private bool AppendPolygonToPath(SKPath path, int dimensions)
    {
        var numRings = ReadElementCount();
        if (numRings == 0)
        {
            return false;
        }

        AppendPolygonRingsToPath(path, numRings, dimensions);
        return true;
    }

    private void AppendPolygonRingsToPath(SKPath path, int numRings, int dimensions)
    {
        for (var ring = 0; ring < numRings; ring++)
        {
            var numPoints = ReadElementCount();
            if (numPoints == 0)
            {
                continue;
            }

            AppendLineStringPointsToPath(path, numPoints, dimensions);
            path.Close();
        }
    }

    private WkbToSkiaConverter.GeometryConversionResult ReadMultiPoint(int depth)
    {
        var numGeometries = ReadElementCount();
        var remaining = _reader.RemainingBytes;
        var safeCapacity = remaining > 0
            ? Math.Min(numGeometries, remaining / MinNestedPointBytes + 1)
            : 0;
        var points = new List<SKPoint>(safeCapacity);

        AppendMultiPointToList(points, numGeometries, depth);

        return new WkbToSkiaConverter.GeometryConversionResult
        {
            Points = [.. points],
            IsPoint = true
        };
    }

    private void AppendMultiPointToList(List<SKPoint> points, int numGeometries, int depth)
    {
        for (var i = 0; i < numGeometries; i++)
        {
            EnsureDepth(depth + 1);
            var header = ReadGeometryHeader();
            if (header.GeometryType == WkbPoint)
            {
                var (x, y) = _reader.ReadCoordinate();
                SkipExtraDimensions(header.Dimensions);
                points.Add(_transform(x, y));
                continue;
            }

            var result = ReadGeometryBody(header, depth + 1);
            using var unusedPath = result.Path;
            if (result.Points != null)
            {
                points.AddRange(result.Points);
            }
        }
    }

    private void SkipExtraDimensions(int dimensions)
    {
        for (var i = 2; i < dimensions; i++)
        {
            _reader.ReadDouble();
        }
    }

    private readonly record struct WkbGeometryHeader(int GeometryType, int Dimensions);
}

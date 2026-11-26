// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using SkiaSharp;

namespace Honua.Infrastructure.Rendering;

internal ref partial struct WkbToSkiaDecoder
{
    private WkbToSkiaConverter.GeometryConversionResult ReadMultiLineString(int depth)
    {
        var combinedPath = new SKPath();
        try
        {
            AppendMultiLineStringChildren(combinedPath, depth);
            return new WkbToSkiaConverter.GeometryConversionResult { Path = combinedPath };
        }
        catch
        {
            combinedPath.Dispose();
            throw;
        }
    }

    private void AppendMultiLineStringChildren(SKPath combinedPath, int depth)
    {
        var numGeometries = ReadElementCount();
        for (var i = 0; i < numGeometries; i++)
        {
            EnsureDepth(depth + 1);
            var header = ReadGeometryHeader();
            if (header.GeometryType == WkbLineString)
            {
                AppendLineStringToPath(combinedPath, header.Dimensions);
                continue;
            }

            var result = ReadGeometryBody(header, depth + 1);
            using var resultPath = result.Path;
            if (resultPath != null)
            {
                combinedPath.AddPath(resultPath);
            }
        }
    }

    private WkbToSkiaConverter.GeometryConversionResult ReadMultiPolygon(int depth)
    {
        var combinedPath = new SKPath { FillType = SKPathFillType.EvenOdd };
        try
        {
            AppendMultiPolygonChildren(combinedPath, depth);
            return new WkbToSkiaConverter.GeometryConversionResult { Path = combinedPath, IsPolygon = true };
        }
        catch
        {
            combinedPath.Dispose();
            throw;
        }
    }

    private void AppendMultiPolygonChildren(SKPath combinedPath, int depth)
    {
        var numGeometries = ReadElementCount();
        for (var i = 0; i < numGeometries; i++)
        {
            EnsureDepth(depth + 1);
            var header = ReadGeometryHeader();
            if (header.GeometryType == WkbPolygon)
            {
                AppendPolygonToPath(combinedPath, header.Dimensions);
                continue;
            }

            var result = ReadGeometryBody(header, depth + 1);
            using var resultPath = result.Path;
            if (resultPath != null)
            {
                combinedPath.AddPath(resultPath);
            }
        }
    }

    private WkbToSkiaConverter.GeometryConversionResult ReadGeometryCollection(int depth)
    {
        var numGeometries = ReadElementCount();
        SKPath? combinedPath = new SKPath { FillType = SKPathFillType.EvenOdd };
        try
        {
            var state = new CollectionAccumulator(combinedPath);
            for (var i = 0; i < numGeometries; i++)
            {
                EnsureDepth(depth + 1);
                AppendCollectionChild(state, depth);
            }

            if (combinedPath.PointCount == 0)
            {
                combinedPath.Dispose();
                combinedPath = null;
            }

            return new WkbToSkiaConverter.GeometryConversionResult
            {
                Path = combinedPath,
                Points = state.Points.Count > 0 ? [.. state.Points] : null,
                IsPoint = state.IsPoint && state.Points.Count > 0,
                IsPolygon = state.IsPolygon
            };
        }
        catch
        {
            combinedPath?.Dispose();
            throw;
        }
    }

    private void AppendCollectionChild(CollectionAccumulator state, int depth)
    {
        var header = ReadGeometryHeader();
        switch (header.GeometryType)
        {
            case WkbPoint:
                var (x, y) = _reader.ReadCoordinate();
                SkipExtraDimensions(header.Dimensions);
                state.Points.Add(_transform(x, y));
                return;
            case WkbLineString:
                AppendLineStringToPath(state.CombinedPath, header.Dimensions);
                state.IsPoint = false;
                return;
            case WkbPolygon:
                if (AppendPolygonToPath(state.CombinedPath, header.Dimensions))
                {
                    state.IsPolygon = true;
                }

                state.IsPoint = false;
                return;
            case WkbMultiPoint:
                AppendMultiPointToList(state.Points, ReadElementCount(), depth + 1);
                return;
            case WkbMultiLineString:
                AppendMultiLineStringChildren(state.CombinedPath, depth + 1);
                state.IsPoint = false;
                return;
            case WkbMultiPolygon:
                AppendMultiPolygonChildren(state.CombinedPath, depth + 1);
                state.IsPolygon = true;
                state.IsPoint = false;
                return;
            default:
                MergeFallbackChild(state, header, depth + 1);
                return;
        }
    }

    private void MergeFallbackChild(CollectionAccumulator state, WkbGeometryHeader header, int depth)
    {
        var result = ReadGeometryBody(header, depth);
        using var resultPath = result.Path;
        if (resultPath != null)
        {
            state.CombinedPath.AddPath(resultPath);
            state.IsPoint = false;
        }

        if (result.Points != null)
        {
            state.Points.AddRange(result.Points);
        }

        if (result.IsPolygon)
        {
            state.IsPolygon = true;
        }

        if (!result.IsPoint)
        {
            state.IsPoint = false;
        }
    }

    private sealed class CollectionAccumulator(SKPath combinedPath)
    {
        public SKPath CombinedPath { get; } = combinedPath;

        public List<SKPoint> Points { get; } = [];

        public bool IsPolygon { get; set; }

        public bool IsPoint { get; set; } = true;
    }
}

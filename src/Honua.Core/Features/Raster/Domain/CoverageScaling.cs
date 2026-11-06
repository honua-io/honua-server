// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

public readonly record struct CoverageScalingResult
{
    public required int Width { get; init; }

    public required int Height { get; init; }
}

public static class CoverageScaling
{
    public static bool TryResolveFactor(int baseWidth, int baseHeight, double factor, out CoverageScalingResult result)
    {
        result = default;
        if (!IsPositiveGrid(baseWidth, baseHeight) || !IsPositiveFactor(factor))
        {
            return false;
        }

        return TryScaleDimension(baseWidth, factor, out var width) &&
            TryScaleDimension(baseHeight, factor, out var height) &&
            TryCreate(width, height, out result);
    }

    public static bool TryResolveAxes(
        int baseWidth,
        int baseHeight,
        double? xFactor,
        double? yFactor,
        out CoverageScalingResult result)
    {
        result = default;
        if (!IsPositiveGrid(baseWidth, baseHeight))
        {
            return false;
        }

        if (xFactor is null && yFactor is null)
        {
            return false;
        }

        if ((xFactor.HasValue && !IsPositiveFactor(xFactor.Value)) ||
            (yFactor.HasValue && !IsPositiveFactor(yFactor.Value)))
        {
            return false;
        }

        return TryScaleDimension(baseWidth, xFactor ?? 1, out var width) &&
            TryScaleDimension(baseHeight, yFactor ?? 1, out var height) &&
            TryCreate(width, height, out result);
    }

    public static bool TryResolveExtent(
        int baseWidth,
        int baseHeight,
        int? xSize,
        int? ySize,
        out CoverageScalingResult result)
    {
        result = default;
        if (!IsPositiveGrid(baseWidth, baseHeight))
        {
            return false;
        }

        if (xSize is null && ySize is null)
        {
            return false;
        }

        if ((xSize.HasValue && xSize.Value <= 0) || (ySize.HasValue && ySize.Value <= 0))
        {
            return false;
        }

        return TryCreate(xSize ?? baseWidth, ySize ?? baseHeight, out result);
    }

    private static bool IsPositiveGrid(int width, int height) => width > 0 && height > 0;

    private static bool IsPositiveFactor(double factor) => double.IsFinite(factor) && factor > 0;

    private static bool TryScaleDimension(int baseSize, double factor, out int dimension)
    {
        dimension = 0;
        var scaled = Math.Round(baseSize * factor, MidpointRounding.AwayFromZero);
        if (!double.IsFinite(scaled) || scaled > int.MaxValue)
        {
            return false;
        }

        dimension = Math.Max(1, (int)scaled);
        return true;
    }

    private static bool TryCreate(int width, int height, out CoverageScalingResult result)
    {
        if (width <= 0 || height <= 0)
        {
            result = default;
            return false;
        }

        result = new CoverageScalingResult { Width = width, Height = height };
        return true;
    }
}

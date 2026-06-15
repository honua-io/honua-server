// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// Identifies which kind of WCS 2.0 Scaling extension request produced a
/// <see cref="CoverageScalingResult"/>.
/// </summary>
public enum CoverageScalingKind
{
    /// <summary>
    /// A uniform scale factor applied to every spatial axis (SCALEFACTOR).
    /// </summary>
    Factor,

    /// <summary>
    /// A per-axis scale factor (SCALEAXES).
    /// </summary>
    Axes,

    /// <summary>
    /// An explicit per-axis output grid size (SCALEEXTENT).
    /// </summary>
    Extent
}

/// <summary>
/// Resolved output grid dimensions for a WCS 2.0 Scaling extension request.
/// </summary>
public readonly record struct CoverageScalingResult
{
    /// <summary>
    /// Output width in pixels for the x/E/Long axis.
    /// </summary>
    public required int Width { get; init; }

    /// <summary>
    /// Output height in pixels for the y/N/Lat axis.
    /// </summary>
    public required int Height { get; init; }
}

/// <summary>
/// Pure arithmetic for the WCS 2.0 Scaling extension (SCALEFACTOR, SCALEAXES,
/// SCALEEXTENT). Protocol adapters parse the request parameters and supply the
/// base grid dimensions (the source grid after any SUBSET/BBOX trim); this
/// resolver maps those into explicit output width/height in pixels so the shared
/// raster export pipeline can resample consistently with SCALESIZE.
/// </summary>
public static class CoverageScaling
{
    /// <summary>
    /// Resolves a uniform scale factor against the base grid dimensions.
    /// </summary>
    /// <param name="baseWidth">Base grid width in pixels (must be positive).</param>
    /// <param name="baseHeight">Base grid height in pixels (must be positive).</param>
    /// <param name="factor">Uniform scale factor (must be positive).</param>
    /// <param name="result">The resolved output dimensions when successful.</param>
    /// <returns><c>true</c> when the inputs are valid and produce a non-empty grid.</returns>
    public static bool TryResolveFactor(int baseWidth, int baseHeight, double factor, out CoverageScalingResult result)
    {
        result = default;
        if (!IsPositiveGrid(baseWidth, baseHeight) || !IsPositiveFactor(factor))
        {
            return false;
        }

        return TryCreate(ScaleDimension(baseWidth, factor), ScaleDimension(baseHeight, factor), out result);
    }

    /// <summary>
    /// Resolves per-axis scale factors against the base grid dimensions. A
    /// <c>null</c> factor leaves that axis unscaled.
    /// </summary>
    /// <param name="baseWidth">Base grid width in pixels (must be positive).</param>
    /// <param name="baseHeight">Base grid height in pixels (must be positive).</param>
    /// <param name="xFactor">Scale factor for the x/E/Long axis, or <c>null</c> to leave it unscaled.</param>
    /// <param name="yFactor">Scale factor for the y/N/Lat axis, or <c>null</c> to leave it unscaled.</param>
    /// <param name="result">The resolved output dimensions when successful.</param>
    /// <returns><c>true</c> when the inputs are valid and produce a non-empty grid.</returns>
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

        var width = xFactor.HasValue ? ScaleDimension(baseWidth, xFactor.Value) : baseWidth;
        var height = yFactor.HasValue ? ScaleDimension(baseHeight, yFactor.Value) : baseHeight;
        return TryCreate(width, height, out result);
    }

    /// <summary>
    /// Resolves explicit per-axis output grid sizes (SCALEEXTENT). A <c>null</c>
    /// extent leaves that axis at its base size.
    /// </summary>
    /// <param name="baseWidth">Base grid width in pixels (must be positive).</param>
    /// <param name="baseHeight">Base grid height in pixels (must be positive).</param>
    /// <param name="xSize">Target output width for the x/E/Long axis, or <c>null</c> to keep the base width.</param>
    /// <param name="ySize">Target output height for the y/N/Lat axis, or <c>null</c> to keep the base height.</param>
    /// <param name="result">The resolved output dimensions when successful.</param>
    /// <returns><c>true</c> when the inputs are valid and produce a non-empty grid.</returns>
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

    private static int ScaleDimension(int baseSize, double factor)
        => Math.Max(1, (int)Math.Round(baseSize * factor, MidpointRounding.AwayFromZero));

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

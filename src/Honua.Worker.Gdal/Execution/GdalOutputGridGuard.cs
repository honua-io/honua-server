// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Admission control that bounds the attacker-controlled REQUESTED OUTPUT grid of
/// the rasterize / interpolate / resample executors BEFORE the GDAL subprocess
/// spawns (#2782, #2793).
///
/// <para>
/// <c>gdal_rasterize -ts &lt;width&gt; &lt;height&gt;</c> and
/// <c>gdal_grid -outsize &lt;width&gt; &lt;height&gt;</c> take an explicit output
/// canvas size straight from caller input. A request can supply a tiny vector input
/// yet ask for an enormous output raster — GDAL then allocates the
/// width×height×bands×dtype OUTPUT grid → OOM. <see cref="TryAdmit"/> bounds that
/// EXPLICIT pixel grid. This is the OUTPUT-side companion to the INPUT
/// decompression-bomb bound enforced by <see cref="GdalRasterDimensionGuard"/>
/// (#2766/#2780): both reuse the same <see cref="GdalWorkerOptions"/> width / height /
/// pixel caps so the output canvas cannot exceed what a legitimate input raster is
/// allowed to be.
/// </para>
///
/// <para>
/// The caller-controlled RESOLUTION paths that resolve to <c>-tr</c> (a target cell
/// size rather than an explicit pixel grid) are bounded by <see cref="TryAdmitResolution"/>
/// (#2793): the output pixel count = input extent ÷ target cell size, so a tiny cell
/// size over a wide extent yields billions of output pixels. This covers the
/// <c>conversion.rasterize</c> <c>cellSize</c> → <c>-tr</c> branch (extent from the
/// vector payload envelope) and the <c>raster.resample</c>
/// <see cref="GdalRasterResampleJobExecutor"/> <c>gdalwarp -tr</c> path (extent =
/// declared input pixel dimensions × ModelPixelScale). Both derive the extent from a
/// cheap header / payload read, then apply the same width / height / pixel / decoded-byte
/// caps as <see cref="TryAdmit"/>. When no real extent can be derived (an
/// un-georeferenced raster or a degenerate/point envelope) the resolution bound admits
/// and the <see cref="GdalWorkerOptions.ToolTimeout"/> /
/// <see cref="GdalWorkerOptions.MaxArtifactBytes"/> ceilings remain the backstop.
/// </para>
/// </summary>
internal static class GdalOutputGridGuard
{
    /// <summary>
    /// Admits or rejects an explicitly requested output grid (<paramref name="width"/>
    /// × <paramref name="height"/>, in pixels) against the configured width / height /
    /// pixel-count caps. Returns <c>false</c> with a caller-facing
    /// <paramref name="error"/> when any cap is exceeded, so the executor can fail the
    /// job with a clear validation message before spawning the GDAL tool.
    /// </summary>
    public static bool TryAdmit(long width, long height, GdalWorkerOptions options, out string error)
    {
        ArgumentNullException.ThrowIfNull(options);
        error = "";

        if (width < 1 || height < 1)
        {
            error = "requested output grid width and height must both be positive";
            return false;
        }

        if (width > options.MaxRasterWidth)
        {
            error = $"requested output grid width {width.ToString(CultureInfo.InvariantCulture)} exceeds configured MaxRasterWidth={options.MaxRasterWidth.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        if (height > options.MaxRasterHeight)
        {
            error = $"requested output grid height {height.ToString(CultureInfo.InvariantCulture)} exceeds configured MaxRasterHeight={options.MaxRasterHeight.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        // width and height are each already bounded by the int-typed MaxRasterWidth /
        // MaxRasterHeight caps above, so the product cannot exceed int.MaxValue² and can
        // never overflow Int64 — no checked-multiply / overflow guard is needed here.
        long pixels = width * height;

        if (pixels > options.MaxRasterPixels)
        {
            error = $"requested output grid pixel count {pixels.ToString(CultureInfo.InvariantCulture)} (width×height) exceeds configured MaxRasterPixels={options.MaxRasterPixels.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        // Estimated fully-decoded OUTPUT footprint. gdal_rasterize (-ts) and gdal_grid
        // (-outsize) emit a single-band Float64 grid (8 bytes/pixel), so the decoded
        // output size is pixels × 8. Compare via division to avoid an Int64 overflow on
        // the multiply when the pixel caps are configured very high.
        const long BytesPerFloat64Pixel = 8L;
        if (pixels > (double)options.MaxDecodedRasterBytes / BytesPerFloat64Pixel)
        {
            error = $"estimated output grid size {pixels.ToString(CultureInfo.InvariantCulture)} pixels × 8 bytes/pixel (single-band Float64) exceeds configured MaxDecodedRasterBytes={options.MaxDecodedRasterBytes.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Admits or rejects a RESOLUTION-derived output grid (#2793). The caller supplies a
    /// target cell size (the GDAL <c>-tr</c> flag) instead of an explicit pixel grid, so
    /// the output dimensions are derived as <c>ceil(extent ÷ cellSize)</c> per axis from
    /// the input extent (the input raster's ground extent for <c>raster.resample</c>; the
    /// vector payload envelope for <c>conversion.rasterize</c>). The derived width /
    /// height / pixel-count / decoded-byte footprint is then bounded by the same
    /// <see cref="GdalWorkerOptions"/> caps <see cref="TryAdmit"/> applies, so a tiny
    /// cell size over a wide extent is refused BEFORE the GDAL tool spawns and allocates
    /// the output grid. Returns <c>false</c> with a caller-facing <paramref name="error"/>
    /// on an over-cap grid.
    ///
    /// <para>
    /// A non-finite or non-positive <paramref name="extentX"/> / <paramref name="extentY"/>
    /// signals that no real ground extent could be derived (an un-georeferenced raster, or
    /// a degenerate / single-point envelope): there is nothing to bound on the resolution
    /// axis, so the grid is admitted and the input-dimension caps plus the
    /// <see cref="GdalWorkerOptions.ToolTimeout"/> / <see cref="GdalWorkerOptions.MaxArtifactBytes"/>
    /// ceilings remain the backstop. Cell size is validated positive+finite at the executor
    /// boundary and re-guarded here defensively.
    /// </para>
    /// </summary>
    public static bool TryAdmitResolution(
        double extentX,
        double extentY,
        double cellSizeX,
        double cellSizeY,
        GdalWorkerOptions options,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(options);
        error = "";

        if (!IsPositiveFinite(cellSizeX) || !IsPositiveFinite(cellSizeY))
        {
            error = "target cell size must be a positive finite number";
            return false;
        }

        // No derivable ground extent → nothing to bound on the resolution axis; admit.
        if (!IsPositiveFinite(extentX) || !IsPositiveFinite(extentY))
        {
            return true;
        }

        // One output pixel per target cell across the extent, rounded up — the grid
        // gdalwarp / gdal_rasterize materialize for -tr.
        double width = Math.Ceiling(extentX / cellSizeX);
        double height = Math.Ceiling(extentY / cellSizeY);

        if (width > options.MaxRasterWidth)
        {
            error = $"derived output grid width {Format(width)} (input extent {Format(extentX)} ÷ target cell size {Format(cellSizeX)}) exceeds configured MaxRasterWidth={options.MaxRasterWidth.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        if (height > options.MaxRasterHeight)
        {
            error = $"derived output grid height {Format(height)} (input extent {Format(extentY)} ÷ target cell size {Format(cellSizeY)}) exceeds configured MaxRasterHeight={options.MaxRasterHeight.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        // width and height are each already <= the int-typed width/height caps here, so
        // the product is <= int.MaxValue² and is represented exactly by a Double for the
        // comparisons below — no Int64 overflow is possible.
        double pixels = width * height;

        if (pixels > options.MaxRasterPixels)
        {
            error = $"derived output grid pixel count {Format(pixels)} (input extent ÷ target cell size) exceeds configured MaxRasterPixels={options.MaxRasterPixels.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        // gdal_rasterize / gdalwarp -tr output footprint estimated as single-band Float64
        // (8 bytes/pixel), mirroring TryAdmit; the width / height / pixel caps are the
        // primary bound and hold regardless of the true output band count / dtype.
        const long BytesPerFloat64Pixel = 8L;
        // Divide in double space (widen the long numerator) rather than truncating
        // integer division first: MaxDecodedRasterBytes is not guaranteed to be a
        // multiple of 8, and a floored integer threshold would reject a pixel count
        // that is actually still within the configured byte budget.
        if (pixels > (double)options.MaxDecodedRasterBytes / BytesPerFloat64Pixel)
        {
            error = $"estimated output grid size {Format(pixels)} pixels × 8 bytes/pixel (single-band Float64) exceeds configured MaxDecodedRasterBytes={options.MaxDecodedRasterBytes.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        return true;
    }

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0d;

    private static string Format(double value) => value.ToString("F0", CultureInfo.InvariantCulture);
}

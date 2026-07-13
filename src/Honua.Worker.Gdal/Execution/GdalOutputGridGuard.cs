// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Admission control that bounds the attacker-controlled REQUESTED OUTPUT grid of
/// the rasterize / interpolate executors BEFORE the GDAL subprocess spawns (#2782).
///
/// <para>
/// <c>gdal_rasterize -ts &lt;width&gt; &lt;height&gt;</c> and
/// <c>gdal_grid -outsize &lt;width&gt; &lt;height&gt;</c> take an explicit output
/// canvas size straight from caller input. A request can supply a tiny vector input
/// yet ask for an enormous output raster — GDAL then allocates the
/// width×height×bands×dtype OUTPUT grid → OOM. This is the OUTPUT-side companion to
/// the INPUT decompression-bomb bound enforced by
/// <see cref="GdalRasterDimensionGuard"/> (#2766/#2780): both reuse the same
/// <see cref="GdalWorkerOptions"/> width / height / pixel caps so the output canvas
/// cannot exceed what a legitimate input raster is allowed to be.
/// </para>
///
/// <para>
/// The guard bounds only the EXPLICIT pixel grid (<c>width</c>+<c>height</c>). Every
/// resolution / target-cell-size path that resolves to <c>-tr</c> derives its pixel
/// count from an input extent that is not known without parsing the payload, so it is
/// NOT bounded here. This unbounded class covers the rasterize / interpolate
/// <c>cellSize</c> → <c>-tr</c> paths AND the <c>raster.resample</c>
/// <see cref="GdalRasterResampleJobExecutor"/> <c>gdalwarp -tr</c> path (target cell
/// size straight from caller input, output pixel count = input extent ÷ cell size).
/// For all of them the <see cref="GdalWorkerOptions.ToolTimeout"/> and
/// <see cref="GdalWorkerOptions.MaxArtifactBytes"/> ceilings remain the backstop; a
/// dedicated <c>-tr</c> bound is tracked as a follow-up.
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
        if (pixels > options.MaxDecodedRasterBytes / BytesPerFloat64Pixel)
        {
            error = $"estimated output grid size {pixels.ToString(CultureInfo.InvariantCulture)} pixels × 8 bytes/pixel (single-band Float64) exceeds configured MaxDecodedRasterBytes={options.MaxDecodedRasterBytes.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        return true;
    }
}

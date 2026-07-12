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
/// The guard bounds only the EXPLICIT pixel grid (<c>width</c>+<c>height</c>). The
/// resolution path (<c>cellSize</c> → <c>-tr</c>) derives its pixel count from the
/// input layer extent, which is not known without parsing the vector payload, so it
/// is not bounded here; the <see cref="GdalWorkerOptions.ToolTimeout"/> and
/// <see cref="GdalWorkerOptions.MaxArtifactBytes"/> ceilings remain the backstop for
/// that path.
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

        long pixels;
        try
        {
            pixels = checked(width * height);
        }
        catch (OverflowException)
        {
            error = "requested output grid dimensions overflow the pixel-count bound";
            return false;
        }

        if (pixels > options.MaxRasterPixels)
        {
            error = $"requested output grid pixel count {pixels.ToString(CultureInfo.InvariantCulture)} (width×height) exceeds configured MaxRasterPixels={options.MaxRasterPixels.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        return true;
    }
}

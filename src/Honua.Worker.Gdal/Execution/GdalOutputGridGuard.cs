// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Worker.Gdal.Execution;

internal static class GdalOutputGridGuard
{
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

    public static bool TryAdmitResolution(
        double extentX,
        double extentY,
        double cellSizeX,
        double cellSizeY,
        GdalWorkerOptions options,
        out string error)
        => TryAdmitResolutionWithFootprint(
            extentX,
            extentY,
            cellSizeX,
            cellSizeY,
            bytesPerPixel: 8,
            options,
            out error);

    public static bool TryAdmitResolutionWithFootprint(
        double extentX,
        double extentY,
        double cellSizeX,
        double cellSizeY,
        long bytesPerPixel,
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

        if (bytesPerPixel <= 0)
        {
            error = "estimated output bytes per pixel must be positive";
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

        // Rasterize/grid callers use the single-band Float64 default; resample supplies
        // the source band count and sample width that gdalwarp preserves.
        // Divide in double space (widen the long numerator) rather than truncating
        // integer division first: MaxDecodedRasterBytes is not guaranteed to be a
        // multiple of the output footprint, and a floored integer threshold would reject a
        // pixel count that is actually still within the configured byte budget.
        if (pixels > (double)options.MaxDecodedRasterBytes / bytesPerPixel)
        {
            error = $"estimated output grid size {Format(pixels)} pixels × {bytesPerPixel.ToString(CultureInfo.InvariantCulture)} bytes/pixel exceeds configured MaxDecodedRasterBytes={options.MaxDecodedRasterBytes.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        return true;
    }

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0d;

    private static string Format(double value) => value.ToString("F0", CultureInfo.InvariantCulture);
}

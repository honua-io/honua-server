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

        if (!double.IsFinite(extentX) || !double.IsFinite(extentY))
        {
            error = "input extent must be finite";
            return false;
        }

        // A zero/degenerate finite extent (single point or empty envelope) has no span to bound.
        if (extentX <= 0d || extentY <= 0d)
        {
            return true;
        }

        // One output pixel per target cell across the extent, rounded up — the grid
        // gdalwarp / gdal_rasterize materialize for -tr.
        double width = Math.Ceiling(extentX / cellSizeX);
        double height = Math.Ceiling(extentY / cellSizeY);

        if (!double.IsFinite(width) || width > options.MaxRasterWidth)
        {
            error = $"derived output grid width {Format(width)} (input extent {Format(extentX)} ÷ target cell size {Format(cellSizeX)}) exceeds configured MaxRasterWidth={options.MaxRasterWidth.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        if (!double.IsFinite(height) || height > options.MaxRasterHeight)
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

    private const double EarthRadiusMeters = 6_378_137.0;
    private const double DegreesToRadians = Math.PI / 180.0;
    private const double PolarStereographicScaleFactor = 0.97276901289;

    internal readonly record struct ReprojectGridCheck(
        GdalRasterInput Source,
        string? SourceSrs,
        string TargetSrs,
        GdalWorkerOptions Options);

    private readonly record struct ProjectedGridEstimate(
        double ExtentX,
        double ExtentY,
        double CellSizeX,
        double CellSizeY);

    public static bool TryAdmitReproject(ReprojectGridCheck check, out string error)
    {
        ArgumentNullException.ThrowIfNull(check.Options);
        error = "";

        if (!TryResolveReprojectSource(check.Source, out var dims, out var hasBounds, out var bounds))
        {
            return true;
        }

        var bytesPerPixel = ComputeBytesPerPixel(dims);
        if (!TryAdmitResolutionWithFootprint(
                dims.Width, dims.Height, 1d, 1d, bytesPerPixel, check.Options, out error))
        {
            return false;
        }

        if (!hasBounds)
        {
            return true;
        }

        var sourceEpsg = ParseEpsgCode(check.SourceSrs) ?? bounds.EpsgCode;
        var targetEpsg = ParseEpsgCode(check.TargetSrs);
        if (!TryEstimateProjectedGrid(bounds, sourceEpsg, targetEpsg, out var estimate, out error))
        {
            return string.IsNullOrEmpty(error);
        }

        return TryAdmitResolutionWithFootprint(
            estimate.ExtentX,
            estimate.ExtentY,
            estimate.CellSizeX,
            estimate.CellSizeY,
            bytesPerPixel,
            check.Options,
            out error);
    }

    private struct MosaicGridAccumulator
    {
        public bool HasBounds;
        public bool HasScale;
        public double UnionMinX;
        public double UnionMinY;
        public double UnionMaxX;
        public double UnionMaxY;
        public double MaxExtentX;
        public double MaxExtentY;
        public double MinScaleX;
        public double MinScaleY;
        public long MaxBytesPerPixel;

        public static MosaicGridAccumulator Create() => new()
        {
            UnionMinX = double.PositiveInfinity,
            UnionMinY = double.PositiveInfinity,
            UnionMaxX = double.NegativeInfinity,
            UnionMaxY = double.NegativeInfinity,
            MinScaleX = double.PositiveInfinity,
            MinScaleY = double.PositiveInfinity,
        };

        public void Include(ReadOnlySpan<byte> source)
        {
            if (!GdalRasterDimensionGuard.TryReadRasterDimensions(source, out var dims))
            {
                return;
            }

            MaxBytesPerPixel = Math.Max(MaxBytesPerPixel, ComputeBytesPerPixel(dims));
            if (GdalRasterDimensionGuard.TryReadGeoTiffBounds(source, dims, out var bounds))
            {
                HasBounds = true;
                UnionMinX = Math.Min(UnionMinX, bounds.MinX);
                UnionMinY = Math.Min(UnionMinY, bounds.MinY);
                UnionMaxX = Math.Max(UnionMaxX, bounds.MaxX);
                UnionMaxY = Math.Max(UnionMaxY, bounds.MaxY);
                MinScaleX = Math.Min(MinScaleX, bounds.ScaleX);
                MinScaleY = Math.Min(MinScaleY, bounds.ScaleY);
                return;
            }

            GdalRasterDimensionGuard.ReadGeoTiffPixelScale(source, out var sx, out var sy);
            HasScale = true;
            MinScaleX = Math.Min(MinScaleX, sx);
            MinScaleY = Math.Min(MinScaleY, sy);
            MaxExtentX = Math.Max(MaxExtentX, dims.Width * sx);
            MaxExtentY = Math.Max(MaxExtentY, dims.Height * sy);
        }
    }

    public static bool TryAdmitMosaic(
        IReadOnlyList<byte[]> sources,
        GdalWorkerOptions options,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        error = "";

        var acc = MosaicGridAccumulator.Create();
        for (var i = 0; i < sources.Count; i++)
        {
            acc.Include(sources[i]);
        }

        if (acc.MaxBytesPerPixel <= 0L)
        {
            return true;
        }

        if (acc.HasBounds)
        {
            return TryAdmitResolutionWithFootprint(
                acc.UnionMaxX - acc.UnionMinX,
                acc.UnionMaxY - acc.UnionMinY,
                acc.MinScaleX,
                acc.MinScaleY,
                acc.MaxBytesPerPixel,
                options,
                out error);
        }

        return !acc.HasScale || TryAdmitResolutionWithFootprint(
            acc.MaxExtentX,
            acc.MaxExtentY,
            acc.MinScaleX,
            acc.MinScaleY,
            acc.MaxBytesPerPixel,
            options,
            out error);
    }

    private static bool TryResolveReprojectSource(
        GdalRasterInput source,
        out GdalRasterDimensionGuard.RasterDimensions dims,
        out bool hasBounds,
        out GdalRasterDimensionGuard.RasterGeoBounds bounds)
    {
        dims = default;
        hasBounds = false;
        bounds = default;

        if (source.InlineBytes is { } inlineBytes)
        {
            if (!GdalRasterDimensionGuard.TryReadRasterDimensions(inlineBytes, out dims))
            {
                return false;
            }

            hasBounds = GdalRasterDimensionGuard.TryReadGeoTiffBounds(inlineBytes, dims, out bounds);
            return true;
        }

        if (source.DeclaredDimensions is not { } declared)
        {
            return false;
        }

        dims = new GdalRasterDimensionGuard.RasterDimensions(
            declared.Width,
            declared.Height,
            declared.BandCount,
            declared.BitsPerSample);
        return true;
    }

    private static bool TryEstimateProjectedGrid(
        GdalRasterDimensionGuard.RasterGeoBounds bounds,
        int? sourceEpsg,
        int? targetEpsg,
        out ProjectedGridEstimate estimate,
        out string error)
    {
        estimate = default;
        error = "";

        if (!sourceEpsg.HasValue || !targetEpsg.HasValue || sourceEpsg.Value == targetEpsg.Value)
        {
            return false;
        }

        if (IsWebMercatorEpsg(targetEpsg.Value) && IsGeographicEpsg(sourceEpsg.Value))
        {
            return TryEstimateGeographicToWebMercator(bounds, out estimate, out error);
        }

        if (IsWebMercatorEpsg(targetEpsg.Value) && IsPolarStereographicEpsg(sourceEpsg.Value))
        {
            return TryEstimatePolarToWebMercator(bounds, out estimate, out error);
        }

        if (IsGeographicEpsg(targetEpsg.Value) && IsWebMercatorEpsg(sourceEpsg.Value))
        {
            return TryEstimateWebMercatorToGeographic(bounds, out estimate);
        }

        return false;
    }

    private static bool TryEstimateGeographicToWebMercator(
        GdalRasterDimensionGuard.RasterGeoBounds bounds,
        out ProjectedGridEstimate estimate,
        out string error)
    {
        estimate = default;
        error = "";

        if (bounds.MinY <= -90d || bounds.MaxY >= 90d)
        {
            error = "derived output grid for polar-to-WebMercator reprojection is unbounded at latitude +/-90 degrees";
            return false;
        }

        var extentX = Math.Abs(MercatorX(bounds.MaxX) - MercatorX(bounds.MinX));
        var extentY = Math.Abs(MercatorY(bounds.MaxY) - MercatorY(bounds.MinY));
        var minAbsLat = bounds.MinY <= 0d && bounds.MaxY >= 0d
            ? 0d
            : Math.Min(Math.Abs(bounds.MinY), Math.Abs(bounds.MaxY));
        var cellX = EarthRadiusMeters * bounds.ScaleX * DegreesToRadians;
        var cellY = EarthRadiusMeters * bounds.ScaleY * DegreesToRadians / Math.Cos(minAbsLat * DegreesToRadians);
        var targetCell = Math.Min(cellX, cellY);
        estimate = new ProjectedGridEstimate(extentX, extentY, targetCell, targetCell);
        return true;
    }

    private static bool TryEstimatePolarToWebMercator(
        GdalRasterDimensionGuard.RasterGeoBounds bounds,
        out ProjectedGridEstimate estimate,
        out string error)
    {
        estimate = default;
        error = "";

        var closestX = Math.Clamp(0d, bounds.MinX, bounds.MaxX);
        var closestY = Math.Clamp(0d, bounds.MinY, bounds.MaxY);
        var minRadius = double.Hypot(closestX, closestY);
        if (minRadius <= 1e-6)
        {
            error = "derived output grid for polar-to-WebMercator reprojection is unbounded when source extent covers a pole";
            return false;
        }

        var maxRadius = Math.Max(
            Math.Max(double.Hypot(bounds.MinX, bounds.MinY), double.Hypot(bounds.MaxX, bounds.MinY)),
            Math.Max(double.Hypot(bounds.MinX, bounds.MaxY), double.Hypot(bounds.MaxX, bounds.MaxY)));
        var maxAbsLat = PolarRadiusToLatitude(minRadius);
        var minAbsLat = PolarRadiusToLatitude(maxRadius);
        if (maxAbsLat >= 90d)
        {
            error = "derived output grid for polar-to-WebMercator reprojection is unbounded near a pole";
            return false;
        }

        var extentY = Math.Abs(MercatorY(maxAbsLat) - MercatorY(minAbsLat));
        var extentX = Math.Min(2d * Math.PI * EarthRadiusMeters, Math.Max(bounds.MaxX - bounds.MinX, bounds.MaxY - bounds.MinY));
        var targetCell = Math.Min(bounds.ScaleX, bounds.ScaleY);
        estimate = new ProjectedGridEstimate(extentX, extentY, targetCell, targetCell);
        return true;
    }

    private static bool TryEstimateWebMercatorToGeographic(
        GdalRasterDimensionGuard.RasterGeoBounds bounds,
        out ProjectedGridEstimate estimate)
    {
        var minLon = bounds.MinX / (EarthRadiusMeters * DegreesToRadians);
        var maxLon = bounds.MaxX / (EarthRadiusMeters * DegreesToRadians);
        var minLat = InverseMercatorY(bounds.MinY);
        var maxLat = InverseMercatorY(bounds.MaxY);
        var cellX = bounds.ScaleX / (EarthRadiusMeters * DegreesToRadians);
        var cellY = bounds.ScaleY / (EarthRadiusMeters * DegreesToRadians);
        var targetCell = Math.Min(cellX, cellY);
        estimate = new ProjectedGridEstimate(
            Math.Abs(maxLon - minLon),
            Math.Abs(maxLat - minLat),
            targetCell,
            targetCell);
        return true;
    }

    private static long ComputeBytesPerPixel(GdalRasterDimensionGuard.RasterDimensions dims)
        => dims.Bands > 0 && dims.BitsPerSample > 0
            ? checked((long)dims.Bands * ((dims.BitsPerSample + 7L) / 8L))
            : 8L;

    private static int? ParseEpsgCode(string? srs)
    {
        if (string.IsNullOrWhiteSpace(srs))
        {
            return null;
        }

        var trimmed = srs.Trim();
        if (trimmed.StartsWith("EPSG:", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[5..];
        }

        return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) && code > 0
            ? code
            : null;
    }

    private static bool IsWebMercatorEpsg(int epsg)
        => epsg is 3857 or 3785 or 900913 or 102100 or 102113;

    private static bool IsGeographicEpsg(int epsg)
        => epsg is 4326 or 4269 or 4267 or 4258 or 4322 or 4019 or 4030;

    private static bool IsPolarStereographicEpsg(int epsg)
        => epsg is 3413 or 3031 or 3995 or 3411 or 3412 or 3976 or 5936;

    private static double MercatorX(double lonDegrees)
        => EarthRadiusMeters * lonDegrees * DegreesToRadians;

    private static double MercatorY(double latDegrees)
        => EarthRadiusMeters * Math.Log(Math.Tan((Math.PI / 4.0) + (latDegrees * DegreesToRadians / 2.0)));

    private static double InverseMercatorY(double yMeters)
        => ((2.0 * Math.Atan(Math.Exp(yMeters / EarthRadiusMeters))) - (Math.PI / 2.0)) / DegreesToRadians;

    private static double PolarRadiusToLatitude(double radiusMeters)
        => Math.Clamp(
            90.0 - (2.0 * Math.Atan(radiusMeters / (2.0 * EarthRadiusMeters * PolarStereographicScaleFactor)) / DegreesToRadians),
            0.0,
            90.0);

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0d;

    private static string Format(double value) => value.ToString("F0", CultureInfo.InvariantCulture);
}

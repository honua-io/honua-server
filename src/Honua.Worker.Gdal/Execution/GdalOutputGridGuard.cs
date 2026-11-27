// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Shared.Models;

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

    internal sealed class GridAdmissionDecision
    {
        public bool IsAdmitted { get; init; }

        public string Error { get; init; } = "";

        public static GridAdmissionDecision Admitted { get; } = new() { IsAdmitted = true };

        public static GridAdmissionDecision Rejected(string error) => new() { IsAdmitted = false, Error = error };
    }

    internal sealed class ReprojectGridCheck
    {
        public GdalRasterInput Source { get; init; }

        public string? SourceSrs { get; init; }

        public required string TargetSrs { get; init; }

        public required GdalWorkerOptions Options { get; init; }
    }

    private sealed class ResolvedReprojectSource
    {
        public GdalRasterDimensionGuard.RasterDimensions Dimensions { get; init; }

        public GeoTiffBounds? Bounds { get; init; }
    }

    private sealed class ProjectedBoundsOutcome
    {
        public GeoTiffBounds? Bounds { get; init; }

        public string? Error { get; init; }

        public static ProjectedBoundsOutcome Unsupported { get; } = new();

        public static ProjectedBoundsOutcome FromBounds(GeoTiffBounds bounds) => new() { Bounds = bounds };

        public static ProjectedBoundsOutcome Failed(string error) => new() { Error = error };
    }

    private sealed class CrsTransformRequest
    {
        public required GeoTiffBounds SourceBounds { get; init; }

        public GdalRasterDimensionGuard.RasterDimensions Dimensions { get; init; }

        public int SourceEpsg { get; init; }

        public int TargetEpsg { get; init; }
    }

    private sealed class GridResolutionRequest
    {
        public double ExtentX { get; init; }

        public double ExtentY { get; init; }

        public double CellSizeX { get; init; }

        public double CellSizeY { get; init; }

        public long BytesPerPixel { get; init; }

        public static GridResolutionRequest ForPixels(
            GdalRasterDimensionGuard.RasterDimensions dims,
            long bytesPerPixel) => new()
            {
                ExtentX = dims.Width,
                ExtentY = dims.Height,
                CellSizeX = 1d,
                CellSizeY = 1d,
                BytesPerPixel = bytesPerPixel,
            };

        public static GridResolutionRequest FromBounds(GeoTiffBounds bounds, long bytesPerPixel) => new()
        {
            ExtentX = bounds.MaxX - bounds.MinX,
            ExtentY = bounds.MaxY - bounds.MinY,
            CellSizeX = bounds.ScaleX,
            CellSizeY = bounds.ScaleY,
            BytesPerPixel = bytesPerPixel,
        };
    }

    private sealed class MosaicGridAccumulator
    {
        private bool _hasBounds;
        private bool _hasScale;
        private int? _targetEpsg;
        private double _unionMinX = double.PositiveInfinity;
        private double _unionMinY = double.PositiveInfinity;
        private double _unionMaxX = double.NegativeInfinity;
        private double _unionMaxY = double.NegativeInfinity;
        private double _maxExtentX;
        private double _maxExtentY;
        private double _minScaleX = double.PositiveInfinity;
        private double _minScaleY = double.PositiveInfinity;

        public long MaxBytesPerPixel { get; private set; }

        public string? RejectionError { get; private set; }

        public void Include(ReadOnlySpan<byte> source)
        {
            if (RejectionError is not null
                || !GdalRasterDimensionGuard.TryReadRasterDimensions(source, out var dims))
            {
                return;
            }

            MaxBytesPerPixel = Math.Max(MaxBytesPerPixel, ComputeBytesPerPixel(dims));
            var bounds = GdalGeoTiffBoundsReader.ReadBounds(source, dims);
            if (bounds is not null)
            {
                IncludeGeoreferencedSource(bounds, dims);
                return;
            }

            GdalRasterDimensionGuard.ReadGeoTiffPixelScale(source, out var sx, out var sy);
            _hasScale = true;
            _minScaleX = Math.Min(_minScaleX, sx);
            _minScaleY = Math.Min(_minScaleY, sy);
            _maxExtentX = Math.Max(_maxExtentX, dims.Width * sx);
            _maxExtentY = Math.Max(_maxExtentY, dims.Height * sy);
        }

        public GridAdmissionDecision Evaluate(GdalWorkerOptions options)
        {
            if (RejectionError is not null)
            {
                return GridAdmissionDecision.Rejected(RejectionError);
            }

            if (MaxBytesPerPixel <= 0L)
            {
                return GridAdmissionDecision.Admitted;
            }

            if (_hasBounds)
            {
                return EvaluateResolution(
                    new GridResolutionRequest
                    {
                        ExtentX = _unionMaxX - _unionMinX,
                        ExtentY = _unionMaxY - _unionMinY,
                        CellSizeX = _minScaleX,
                        CellSizeY = _minScaleY,
                        BytesPerPixel = MaxBytesPerPixel,
                    },
                    options);
            }

            return _hasScale
                ? EvaluateResolution(
                    new GridResolutionRequest
                    {
                        ExtentX = _maxExtentX,
                        ExtentY = _maxExtentY,
                        CellSizeX = _minScaleX,
                        CellSizeY = _minScaleY,
                        BytesPerPixel = MaxBytesPerPixel,
                    },
                    options)
                : GridAdmissionDecision.Admitted;
        }

        private void IncludeGeoreferencedSource(
            GeoTiffBounds bounds,
            GdalRasterDimensionGuard.RasterDimensions dims)
        {
            var normalizedSource = NormalizeEpsg(bounds.EpsgCode);
            if (!_targetEpsg.HasValue && normalizedSource.HasValue)
            {
                _targetEpsg = normalizedSource;
            }

            if (!normalizedSource.HasValue || !_targetEpsg.HasValue || normalizedSource.Value == _targetEpsg.Value)
            {
                UnionBounds(bounds);
                return;
            }

            var transformed = TransformBounds(new CrsTransformRequest
            {
                SourceBounds = bounds,
                Dimensions = dims,
                SourceEpsg = normalizedSource.Value,
                TargetEpsg = _targetEpsg.Value,
            });
            if (transformed.Error is not null)
            {
                RejectionError = transformed.Error;
                return;
            }

            if (transformed.Bounds is not null)
            {
                UnionBounds(transformed.Bounds);
            }
        }

        private void UnionBounds(GeoTiffBounds bounds)
        {
            _hasBounds = true;
            _unionMinX = Math.Min(_unionMinX, bounds.MinX);
            _unionMinY = Math.Min(_unionMinY, bounds.MinY);
            _unionMaxX = Math.Max(_unionMaxX, bounds.MaxX);
            _unionMaxY = Math.Max(_unionMaxY, bounds.MaxY);
            _minScaleX = Math.Min(_minScaleX, bounds.ScaleX);
            _minScaleY = Math.Min(_minScaleY, bounds.ScaleY);
        }
    }

    public static GridAdmissionDecision EvaluateReproject(ReprojectGridCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(check.Options);

        var source = ResolveReprojectSource(check.Source);
        if (source is null)
        {
            return GridAdmissionDecision.Admitted;
        }

        var bytesPerPixel = ComputeBytesPerPixel(source.Dimensions);
        var baseDecision = EvaluateResolution(GridResolutionRequest.ForPixels(source.Dimensions, bytesPerPixel), check.Options);
        if (!baseDecision.IsAdmitted || source.Bounds is null)
        {
            return baseDecision;
        }

        return EvaluateProjectedBounds(check, source, bytesPerPixel);
    }

    private static GridAdmissionDecision EvaluateProjectedBounds(
        ReprojectGridCheck check,
        ResolvedReprojectSource source,
        long bytesPerPixel)
    {
        var sourceEpsg = NormalizeEpsg(ParseEpsgCode(check.SourceSrs) ?? source.Bounds!.EpsgCode);
        var targetEpsg = NormalizeEpsg(ParseEpsgCode(check.TargetSrs));
        if (!sourceEpsg.HasValue || !targetEpsg.HasValue || sourceEpsg.Value == targetEpsg.Value)
        {
            return GridAdmissionDecision.Admitted;
        }

        var projected = TransformBounds(new CrsTransformRequest
        {
            SourceBounds = source.Bounds!,
            Dimensions = source.Dimensions,
            SourceEpsg = sourceEpsg.Value,
            TargetEpsg = targetEpsg.Value,
        });
        if (projected.Error is not null)
        {
            return GridAdmissionDecision.Rejected(projected.Error);
        }

        return projected.Bounds is null
            ? GridAdmissionDecision.Admitted
            : EvaluateResolution(GridResolutionRequest.FromBounds(projected.Bounds, bytesPerPixel), check.Options);
    }

    public static GridAdmissionDecision EvaluateMosaic(
        IReadOnlyList<byte[]> sources,
        GdalWorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);

        var accumulator = new MosaicGridAccumulator();
        for (var i = 0; i < sources.Count; i++)
        {
            accumulator.Include(sources[i]);
        }

        return accumulator.Evaluate(options);
    }

    private static GridAdmissionDecision EvaluateResolution(
        GridResolutionRequest request,
        GdalWorkerOptions options)
        => TryAdmitResolutionWithFootprint(
            request.ExtentX,
            request.ExtentY,
            request.CellSizeX,
            request.CellSizeY,
            request.BytesPerPixel,
            options,
            out var error)
            ? GridAdmissionDecision.Admitted
            : GridAdmissionDecision.Rejected(error);

    private static ResolvedReprojectSource? ResolveReprojectSource(GdalRasterInput source)
    {
        if (source.InlineBytes is { } inlineBytes)
        {
            if (!GdalRasterDimensionGuard.TryReadRasterDimensions(inlineBytes, out var dims))
            {
                return null;
            }

            return new ResolvedReprojectSource
            {
                Dimensions = dims,
                Bounds = GdalGeoTiffBoundsReader.ReadBounds(inlineBytes, dims),
            };
        }

        if (source.DeclaredDimensions is not { } declared)
        {
            return null;
        }

        return new ResolvedReprojectSource
        {
            Dimensions = new GdalRasterDimensionGuard.RasterDimensions(
                declared.Width,
                declared.Height,
                declared.BandCount,
                declared.BitsPerSample),
        };
    }

    private static ProjectedBoundsOutcome TransformBounds(CrsTransformRequest request)
    {
        if (SpatialReferenceExtensions.IsWebMercatorSrid(request.TargetEpsg)
            && GeographicSridClassifier.IsGeographicSrid(request.SourceEpsg))
        {
            return TransformGeographicToWebMercator(request);
        }

        if (SpatialReferenceExtensions.IsWebMercatorSrid(request.TargetEpsg)
            && IsPolarStereographicEpsg(request.SourceEpsg))
        {
            return TransformPolarToWebMercator(request);
        }

        if (GeographicSridClassifier.IsGeographicSrid(request.TargetEpsg)
            && SpatialReferenceExtensions.IsWebMercatorSrid(request.SourceEpsg))
        {
            return TransformWebMercatorToGeographic(request);
        }

        return ProjectedBoundsOutcome.Unsupported;
    }

    private static ProjectedBoundsOutcome TransformGeographicToWebMercator(CrsTransformRequest request)
    {
        var bounds = request.SourceBounds;
        if (bounds.MinY <= -90d || bounds.MaxY >= 90d)
        {
            return ProjectedBoundsOutcome.Failed(
                "derived output grid for polar-to-WebMercator reprojection is unbounded at latitude +/-90 degrees");
        }

        var minX = MercatorX(bounds.MinX);
        var maxX = MercatorX(bounds.MaxX);
        var minY = MercatorY(bounds.MinY);
        var maxY = MercatorY(bounds.MaxY);
        var cellSize = SuggestedDiagonalCellSize(maxX - minX, maxY - minY, request.Dimensions);
        return ProjectedBoundsOutcome.FromBounds(new GeoTiffBounds
        {
            MinX = Math.Min(minX, maxX),
            MinY = Math.Min(minY, maxY),
            MaxX = Math.Max(minX, maxX),
            MaxY = Math.Max(minY, maxY),
            ScaleX = cellSize,
            ScaleY = cellSize,
            EpsgCode = request.TargetEpsg,
        });
    }

    private static ProjectedBoundsOutcome TransformPolarToWebMercator(CrsTransformRequest request)
    {
        var bounds = request.SourceBounds;
        var minRadius = double.Hypot(Math.Clamp(0d, bounds.MinX, bounds.MaxX), Math.Clamp(0d, bounds.MinY, bounds.MaxY));
        if (minRadius <= 1e-6)
        {
            return ProjectedBoundsOutcome.Failed(
                "derived output grid for polar-to-WebMercator reprojection is unbounded when source extent covers a pole");
        }

        var maxRadius = MaxCornerRadius(bounds);
        var maxAbsLat = PolarRadiusToLatitude(minRadius);
        var minAbsLat = PolarRadiusToLatitude(maxRadius);
        if (maxAbsLat >= 90d)
        {
            return ProjectedBoundsOutcome.Failed(
                "derived output grid for polar-to-WebMercator reprojection is unbounded near a pole");
        }

        var minY = MercatorY(minAbsLat);
        var maxY = MercatorY(maxAbsLat);
        var cornerCellSize = PolarCornerDiagonalCellSize(bounds, request.Dimensions);
        var extentX = Math.Min(2d * Math.PI * EarthRadiusMeters, Math.Max(bounds.MaxX - bounds.MinX, bounds.MaxY - bounds.MinY));
        return ProjectedBoundsOutcome.FromBounds(new GeoTiffBounds
        {
            MinX = -extentX / 2d,
            MinY = minY,
            MaxX = extentX / 2d,
            MaxY = maxY,
            ScaleX = cornerCellSize,
            ScaleY = cornerCellSize,
            EpsgCode = request.TargetEpsg,
        });
    }

    private static ProjectedBoundsOutcome TransformWebMercatorToGeographic(CrsTransformRequest request)
    {
        var bounds = request.SourceBounds;
        var minX = InverseMercatorX(bounds.MinX);
        var maxX = InverseMercatorX(bounds.MaxX);
        var minY = InverseMercatorY(bounds.MinY);
        var maxY = InverseMercatorY(bounds.MaxY);
        var cellSize = SuggestedDiagonalCellSize(maxX - minX, maxY - minY, request.Dimensions);
        return ProjectedBoundsOutcome.FromBounds(new GeoTiffBounds
        {
            MinX = Math.Min(minX, maxX),
            MinY = Math.Min(minY, maxY),
            MaxX = Math.Max(minX, maxX),
            MaxY = Math.Max(minY, maxY),
            ScaleX = cellSize,
            ScaleY = cellSize,
            EpsgCode = request.TargetEpsg,
        });
    }

    // Mirrors GDAL's GDALSuggestedWarpOutput2 diagonal-preserving pixel size.
    private static double SuggestedDiagonalCellSize(
        double extentX,
        double extentY,
        GdalRasterDimensionGuard.RasterDimensions dims)
    {
        var sourceDiagonal = double.Hypot(dims.Width, dims.Height);
        var targetDiagonal = double.Hypot(Math.Abs(extentX), Math.Abs(extentY));
        return sourceDiagonal > 0d && targetDiagonal > 0d ? targetDiagonal / sourceDiagonal : 1d;
    }

    private static double PolarCornerDiagonalCellSize(
        GeoTiffBounds bounds,
        GdalRasterDimensionGuard.RasterDimensions dims)
    {
        var c0 = ProjectPolarPointToWebMercator(bounds.MinX, bounds.MinY);
        var c1 = ProjectPolarPointToWebMercator(bounds.MaxX, bounds.MinY);
        var c2 = ProjectPolarPointToWebMercator(bounds.MinX, bounds.MaxY);
        var c3 = ProjectPolarPointToWebMercator(bounds.MaxX, bounds.MaxY);
        var diag1 = double.Hypot(c3.X - c0.X, c3.Y - c0.Y);
        var diag2 = double.Hypot(c2.X - c1.X, c2.Y - c1.Y);
        var sourceDiagonal = double.Hypot(dims.Width, dims.Height);
        var minDiagonal = Math.Min(diag1, diag2);
        return sourceDiagonal > 0d && double.IsFinite(minDiagonal) && minDiagonal > 0d
            ? minDiagonal / sourceDiagonal
            : Math.Min(bounds.ScaleX, bounds.ScaleY);
    }

    private static (double X, double Y) ProjectPolarPointToWebMercator(double x, double y)
    {
        var lonDeg = Math.Atan2(x, -y) / DegreesToRadians;
        var latDeg = PolarRadiusToLatitude(double.Hypot(x, y));
        return (MercatorX(lonDeg), MercatorY(latDeg));
    }

    private static double MaxCornerRadius(GeoTiffBounds bounds)
        => Math.Max(
            Math.Max(double.Hypot(bounds.MinX, bounds.MinY), double.Hypot(bounds.MaxX, bounds.MinY)),
            Math.Max(double.Hypot(bounds.MinX, bounds.MaxY), double.Hypot(bounds.MaxX, bounds.MaxY)));

    private static long ComputeBytesPerPixel(GdalRasterDimensionGuard.RasterDimensions dims)
        => dims.Bands > 0 && dims.BitsPerSample > 0
            ? checked((long)dims.Bands * ((dims.BitsPerSample + 7L) / 8L))
            : 8L;

    private static int? NormalizeEpsg(int? epsg)
        => epsg.HasValue ? SpatialReferenceExtensions.NormalizeWebMercatorSrid(epsg.Value) : null;

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

    private static bool IsPolarStereographicEpsg(int epsg)
        => epsg is 3413 or 3031 or 3995 or 3411 or 3412 or 3976 or 5936;

    private static double MercatorX(double lonDegrees)
        => EarthRadiusMeters * lonDegrees * DegreesToRadians;

    private static double MercatorY(double latDegrees)
        => EarthRadiusMeters * Math.Log(Math.Tan((Math.PI / 4.0) + (latDegrees * DegreesToRadians / 2.0)));

    private static double InverseMercatorX(double xMeters)
        => xMeters / (EarthRadiusMeters * DegreesToRadians);

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

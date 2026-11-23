// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

internal static class ImageServerSensorModel
{
    public static double? TryReadOffNadirAngle(RasterSensorMetadata? metadata)
    {
        if (metadata?.ExteriorOrientationJson is not { Length: > 0 } json)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (TryGetDouble(root, "offNadirAngle", out var angle) ||
                TryGetDouble(root, "off_nadir_angle", out angle))
            {
                // Off-nadir angle is unsigned (0 = straight down); normalise sign defensively.
                return Math.Abs(angle);
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // When the full RPC polynomial is absent, image-to-ground falls back to the offset and scale affine terms, and a payload without those terms is not a model.
    public static RpcModel? TryReadRpc(RasterSensorMetadata? metadata)
    {
        if (metadata?.RpcJson is not { Length: > 0 } json)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!TryReadAny(root, out var sampleOffset, "sampleOffset", "sampOff", "samp_off") ||
                !TryReadAny(root, out var lineOffset, "lineOffset", "lineOff", "line_off") ||
                !TryReadAny(root, out var longOffset, "longOffset", "longOff", "long_off") ||
                !TryReadAny(root, out var latOffset, "latOffset", "latOff", "lat_off") ||
                !TryReadAny(root, out var sampleScale, "sampleScale", "sampScale", "samp_scale") ||
                !TryReadAny(root, out var lineScale, "lineScale", "lineScl", "line_scale") ||
                !TryReadAny(root, out var longScale, "longScale", "longScl", "long_scale") ||
                !TryReadAny(root, out var latScale, "latScale", "latScl", "lat_scale"))
            {
                return null;
            }

            if (NumericTolerance.IsEffectivelyZero(sampleScale) ||
                NumericTolerance.IsEffectivelyZero(lineScale) ||
                NumericTolerance.IsEffectivelyZero(longScale) ||
                NumericTolerance.IsEffectivelyZero(latScale))
            {
                // Zero (or near-zero) scales would make the normalisation degenerate
                // (divide-by-zero, or a division blown up by a near-zero denominator).
                return null;
            }

            var heightOffset = TryReadAny(root, out var hOff, "heightOffset", "heightOff", "height_off")
                ? hOff
                : 0d;
            var heightScale = TryReadAny(root, out var hScale, "heightScale", "heightScl", "height_scale") &&
                !NumericTolerance.IsEffectivelyZero(hScale)
                ? hScale
                : 1d;

            var lineNumCoeff = TryReadCoefficientArray(
                root, "lineNumCoeff", "line_num_coeff", "lineNumCoefs", "line_num_coefs");
            var lineDenCoeff = TryReadCoefficientArray(
                root, "lineDenCoeff", "line_den_coeff", "lineDenCoefs", "line_den_coefs");
            var sampNumCoeff = TryReadCoefficientArray(
                root, "sampNumCoeff", "samp_num_coeff", "sampleNumCoeff", "sample_num_coeff", "sampNumCoefs", "samp_num_coefs");
            var sampDenCoeff = TryReadCoefficientArray(
                root, "sampDenCoeff", "samp_den_coeff", "sampleDenCoeff", "sample_den_coeff", "sampDenCoefs", "samp_den_coefs");

            return new RpcModel(
                SampleOffset: sampleOffset,
                LineOffset: lineOffset,
                LongitudeOffset: longOffset,
                LatitudeOffset: latOffset,
                SampleScale: sampleScale,
                LineScale: lineScale,
                LongitudeScale: longScale,
                LatitudeScale: latScale,
                HeightOffset: heightOffset,
                HeightScale: heightScale,
                LineNumCoeff: lineNumCoeff,
                LineDenCoeff: lineDenCoeff,
                SampNumCoeff: sampNumCoeff,
                SampDenCoeff: sampDenCoeff);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static IReadOnlyList<PhotogrammetricControlPoint> ReadControlPoints(
        RasterSensorMetadata? metadata,
        int? defaultReferenceSrid = null)
    {
        if (metadata?.ExteriorOrientationJson is not { Length: > 0 } json)
        {
            return Array.Empty<PhotogrammetricControlPoint>();
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetArray(root, out var array, "controlPoints", "tiePoints", "gcps"))
            {
                return Array.Empty<PhotogrammetricControlPoint>();
            }

            var points = new List<PhotogrammetricControlPoint>(array.GetArrayLength());
            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object ||
                    !TryGetObject(element, out var imageElement, "imagePoint", "sourcePoint") ||
                    !TryGetObject(element, out var referenceElement, "referencePoint", "targetPoint", "groundPoint"))
                {
                    continue;
                }

                if (!TryGetDouble(imageElement, "x", out var imageX) ||
                    !TryGetDouble(imageElement, "y", out var imageY) ||
                    !TryGetDouble(referenceElement, "x", out var referenceX) ||
                    !TryGetDouble(referenceElement, "y", out var referenceY))
                {
                    continue;
                }

                double? referenceZ = TryGetDouble(referenceElement, "z", out var z) ? z : null;
                var referenceSrid = ImageServerGeometryHelpers.ReadWkid(referenceElement) ?? defaultReferenceSrid;
                var imageSrid = ImageServerGeometryHelpers.ReadWkid(imageElement);

                points.Add(new PhotogrammetricControlPoint(
                    imageX, imageY, imageSrid,
                    referenceX, referenceY, referenceZ, referenceSrid));
            }

            return points;
        }
        catch (JsonException)
        {
            return Array.Empty<PhotogrammetricControlPoint>();
        }
    }

    public static SunGeometry? TryReadSunGeometry(RasterSensorMetadata? metadata)
    {
        if (metadata?.ExteriorOrientationJson is not { Length: > 0 } json)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            double? elevation = TryReadAny(root, out var e, "sunElevation", "sun_elevation", "sunElevationAngle", "solarElevation", "sun_elevation_angle")
                ? e
                : null;
            double? azimuth = TryReadAny(root, out var a, "sunAzimuth", "sun_azimuth", "solarAzimuth", "sun_azimuth_angle")
                ? a
                : null;

            // Illumination / look vector fallback: derive the elevation (and azimuth when absent)
            // from a {x, y, z} sun-direction vector when no explicit angle is supplied.
            if (elevation is null &&
                TryGetObject(root, out var vector, "sunDirection", "illuminationVector", "sunVector") &&
                TryGetDouble(vector, "x", out var vx) &&
                TryGetDouble(vector, "y", out var vy) &&
                TryGetDouble(vector, "z", out var vz))
            {
                var magnitude = Math.Sqrt((vx * vx) + (vy * vy) + (vz * vz));
                if (magnitude > 0)
                {
                    elevation = RadiansToDegrees(Math.Asin(Math.Clamp(Math.Abs(vz) / magnitude, -1d, 1d)));
                    azimuth ??= NormalizeBearingDegrees(RadiansToDegrees(Math.Atan2(vx, vy)));
                }
            }

            // Elevation must be a usable angle above the horizon; 0°/90° make tan(θ) degenerate,
            // and out-of-range values are not honest sun geometry.
            if (elevation is not { } elevationDegrees || elevationDegrees <= 0d || elevationDegrees >= 90d)
            {
                return null;
            }

            return new SunGeometry(elevationDegrees, azimuth);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double RadiansToDegrees(double radians) => radians * 180d / Math.PI;

    private static double NormalizeBearingDegrees(double degrees)
    {
        var normalized = degrees % 360d;
        return normalized < 0d ? normalized + 360d : normalized;
    }

    private static bool TryGetArray(JsonElement root, out JsonElement array, params string[] names)
    {
        var nameIndex = 0;
        while (nameIndex < names.Length)
        {
            if (root.TryGetProperty(names[nameIndex], out var candidate) &&
                candidate.ValueKind == JsonValueKind.Array)
            {
                array = candidate;
                return true;
            }

            nameIndex++;
        }

        array = default;
        return false;
    }

    private static bool TryGetObject(JsonElement root, out JsonElement value, params string[] names)
    {
        var nameIndex = 0;
        while (nameIndex < names.Length)
        {
            if (root.TryGetProperty(names[nameIndex], out var candidate) &&
                candidate.ValueKind == JsonValueKind.Object)
            {
                value = candidate;
                return true;
            }

            nameIndex++;
        }

        value = default;
        return false;
    }

    private static bool TryReadAny(JsonElement root, out double value, params string[] names)
    {
        var nameIndex = 0;
        while (nameIndex < names.Length)
        {
            if (TryGetDouble(root, names[nameIndex], out value))
            {
                return true;
            }

            nameIndex++;
        }

        value = 0;
        return false;
    }

    private static bool TryGetDouble(JsonElement root, string name, out double value)
    {
        value = 0;
        if (!root.TryGetProperty(name, out var element))
        {
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Number when element.TryGetDouble(out value):
                return true;
            case JsonValueKind.String when double.TryParse(
                element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value):
                return true;
            default:
                return false;
        }
    }

    private static double[]? TryReadCoefficientArray(JsonElement root, params string[] names)
    {
        for (var i = 0; i < names.Length; i++)
        {
            if (!root.TryGetProperty(names[i], out var element))
            {
                continue;
            }

            if (element.ValueKind == JsonValueKind.Array && element.GetArrayLength() == RpcModel.PolynomialTermCount)
            {
                var coeffs = new double[RpcModel.PolynomialTermCount];
                var valid = true;
                var idx = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var d))
                    {
                        coeffs[idx++] = d;
                    }
                    else if (item.ValueKind == JsonValueKind.String &&
                             double.TryParse(item.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                    {
                        coeffs[idx++] = parsed;
                    }
                    else
                    {
                        valid = false;
                        break;
                    }
                }

                if (valid && idx == RpcModel.PolynomialTermCount)
                {
                    return coeffs;
                }
            }
            else if (element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } text)
            {
                var parts = text.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == RpcModel.PolynomialTermCount)
                {
                    var coeffs = new double[RpcModel.PolynomialTermCount];
                    var valid = true;
                    for (var j = 0; j < RpcModel.PolynomialTermCount; j++)
                    {
                        if (!double.TryParse(parts[j], NumberStyles.Float, CultureInfo.InvariantCulture, out coeffs[j]))
                        {
                            valid = false;
                            break;
                        }
                    }

                    if (valid)
                    {
                        return coeffs;
                    }
                }
            }
        }

        return null;
    }
}

// These are stored control points, not points derived by feature matching.
internal readonly record struct PhotogrammetricControlPoint(
    double ImageX,
    double ImageY,
    int? ImageSrid,
    double ReferenceX,
    double ReferenceY,
    double? ReferenceZ,
    int? ReferenceSrid);

internal readonly record struct SunGeometry(double SunElevationDegrees, double? SunAzimuthDegrees);

internal readonly record struct RpcModel(
    double SampleOffset,
    double LineOffset,
    double LongitudeOffset,
    double LatitudeOffset,
    double SampleScale,
    double LineScale,
    double LongitudeScale,
    double LatitudeScale,
    double HeightOffset = 0d,
    double HeightScale = 1d,
    double[]? LineNumCoeff = null,
    double[]? LineDenCoeff = null,
    double[]? SampNumCoeff = null,
    double[]? SampDenCoeff = null)
{
    public const int PolynomialTermCount = 20;

    private static readonly double[] UnitDenominator =
    [
        1d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d,
        0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d, 0d
    ];

    public bool HasPolynomialCoefficients =>
        LineNumCoeff is { Length: PolynomialTermCount } &&
        SampNumCoeff is { Length: PolynomialTermCount };

    public (double Longitude, double Latitude) ImageToGround(double sample, double line, double? height = null)
    {
        var normalizedSample = (sample - SampleOffset) / SampleScale;
        var normalizedLine = (line - LineOffset) / LineScale;

        if (!HasPolynomialCoefficients)
        {
            var longitude = (normalizedSample * LongitudeScale) + LongitudeOffset;
            var latitude = (normalizedLine * LatitudeScale) + LatitudeOffset;
            return (longitude, latitude);
        }

        var normalizedHeight = height.HasValue && !NumericTolerance.IsEffectivelyZero(HeightScale)
            ? (height.Value - HeightOffset) / HeightScale
            : 0d;

        var lineDen = LineDenCoeff is { Length: PolynomialTermCount } ld ? ld : UnitDenominator;
        var sampDen = SampDenCoeff is { Length: PolynomialTermCount } sd ? sd : UnitDenominator;

        // 2D Newton-Raphson inversion of the RPC00B rational cubic polynomials.
        var l = 0d;
        var p = 0d;
        for (var iteration = 0; iteration < 15; iteration++)
        {
            var (sampRatio, dSampDl, dSampDp) = EvaluateRatioAndPartials(SampNumCoeff!, sampDen, l, p, normalizedHeight);
            var (lineRatio, dLineDl, dLineDp) = EvaluateRatioAndPartials(LineNumCoeff!, lineDen, l, p, normalizedHeight);

            var errSamp = sampRatio - normalizedSample;
            var errLine = lineRatio - normalizedLine;
            if (Math.Abs(errSamp) + Math.Abs(errLine) < 1e-12)
            {
                break;
            }

            var det = (dSampDl * dLineDp) - (dSampDp * dLineDl);
            if (Math.Abs(det) < 1e-15)
            {
                break;
            }

            var dl = ((dLineDp * errSamp) - (dSampDp * errLine)) / det;
            var dp = ((-dLineDl * errSamp) + (dSampDl * errLine)) / det;
            l -= dl;
            p -= dp;

            if (Math.Abs(dl) + Math.Abs(dp) < 1e-12)
            {
                break;
            }
        }

        return ((l * LongitudeScale) + LongitudeOffset, (p * LatitudeScale) + LatitudeOffset);
    }

    public (double Sample, double Line) GroundToImage(double longitude, double latitude, double? height = null)
    {
        var normalizedLongitude = (longitude - LongitudeOffset) / LongitudeScale;
        var normalizedLatitude = (latitude - LatitudeOffset) / LatitudeScale;

        if (!HasPolynomialCoefficients)
        {
            var sample = (normalizedLongitude * SampleScale) + SampleOffset;
            var line = (normalizedLatitude * LineScale) + LineOffset;
            return (sample, line);
        }

        var normalizedHeight = height.HasValue && !NumericTolerance.IsEffectivelyZero(HeightScale)
            ? (height.Value - HeightOffset) / HeightScale
            : 0d;

        var lineDen = LineDenCoeff is { Length: PolynomialTermCount } ld ? ld : UnitDenominator;
        var sampDen = SampDenCoeff is { Length: PolynomialTermCount } sd ? sd : UnitDenominator;

        var sampNumVal = EvaluatePolynomial(SampNumCoeff!, normalizedLongitude, normalizedLatitude, normalizedHeight);
        var sampDenVal = EvaluatePolynomial(sampDen, normalizedLongitude, normalizedLatitude, normalizedHeight);
        var lineNumVal = EvaluatePolynomial(LineNumCoeff!, normalizedLongitude, normalizedLatitude, normalizedHeight);
        var lineDenVal = EvaluatePolynomial(lineDen, normalizedLongitude, normalizedLatitude, normalizedHeight);

        var normalizedSamp = NumericTolerance.IsEffectivelyZero(sampDenVal) ? sampNumVal : sampNumVal / sampDenVal;
        var normalizedLn = NumericTolerance.IsEffectivelyZero(lineDenVal) ? lineNumVal : lineNumVal / lineDenVal;

        return ((normalizedSamp * SampleScale) + SampleOffset, (normalizedLn * LineScale) + LineOffset);
    }

    // Standard NITF RPC00B 20-term cubic polynomial in normalized (L=longitude, P=latitude, H=height).
    internal static double EvaluatePolynomial(double[] c, double l, double p, double h)
    {
        var ll = l * l;
        var pp = p * p;
        var hh = h * h;

        return c[0]
            + (c[1] * l)
            + (c[2] * p)
            + (c[3] * h)
            + (c[4] * l * p)
            + (c[5] * l * h)
            + (c[6] * p * h)
            + (c[7] * ll)
            + (c[8] * pp)
            + (c[9] * hh)
            + (c[10] * p * l * h)
            + (c[11] * l * ll)
            + (c[12] * l * pp)
            + (c[13] * l * hh)
            + (c[14] * ll * p)
            + (c[15] * p * pp)
            + (c[16] * p * hh)
            + (c[17] * ll * h)
            + (c[18] * pp * h)
            + (c[19] * h * hh);
    }

    private static (double Value, double Dl, double Dp) EvaluatePolynomialWithPartials(
        double[] c,
        double l,
        double p,
        double h)
    {
        var ll = l * l;
        var pp = p * p;
        var hh = h * h;

        var val = EvaluatePolynomial(c, l, p, h);
        var dl = c[1]
            + (c[4] * p)
            + (c[5] * h)
            + (2d * c[7] * l)
            + (c[10] * p * h)
            + (3d * c[11] * ll)
            + (c[12] * pp)
            + (c[13] * hh)
            + (2d * c[14] * l * p)
            + (2d * c[17] * l * h);
        var dp = c[2]
            + (c[4] * l)
            + (c[6] * h)
            + (2d * c[8] * p)
            + (c[10] * l * h)
            + (2d * c[12] * l * p)
            + (c[14] * ll)
            + (3d * c[15] * pp)
            + (c[16] * hh)
            + (2d * c[18] * p * h);

        return (val, dl, dp);
    }

    private static (double Ratio, double DRatioDl, double DRatioDp) EvaluateRatioAndPartials(
        double[] numCoeff,
        double[] denCoeff,
        double l,
        double p,
        double h)
    {
        var (num, dNumDl, dNumDp) = EvaluatePolynomialWithPartials(numCoeff, l, p, h);
        var (den, dDenDl, dDenDp) = EvaluatePolynomialWithPartials(denCoeff, l, p, h);
        if (NumericTolerance.IsEffectivelyZero(den))
        {
            return (num, dNumDl, dNumDp);
        }

        var invDen = 1d / den;
        var invDenSq = invDen * invDen;
        var ratio = num * invDen;
        var dRatioDl = ((dNumDl * den) - (num * dDenDl)) * invDenSq;
        var dRatioDp = ((dNumDp * den) - (num * dDenDp)) * invDenSq;
        return (ratio, dRatioDl, dRatioDp);
    }
}

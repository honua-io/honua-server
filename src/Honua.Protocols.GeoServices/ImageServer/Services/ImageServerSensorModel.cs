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

            return new RpcModel(
                SampleOffset: sampleOffset,
                LineOffset: lineOffset,
                LongitudeOffset: longOffset,
                LatitudeOffset: latOffset,
                SampleScale: sampleScale,
                LineScale: lineScale,
                LongitudeScale: longScale,
                LatitudeScale: latScale);
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
                var referenceSrid = ReadWkid(referenceElement) ?? defaultReferenceSrid;
                var imageSrid = ReadWkid(imageElement);

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

    private static int? ReadWkid(JsonElement element)
    {
        if (!element.TryGetProperty("spatialReference", out var sr) || sr.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (sr.TryGetProperty("latestWkid", out var latest) &&
            latest.ValueKind == JsonValueKind.Number &&
            latest.TryGetInt32(out var latestWkid))
        {
            return latestWkid;
        }

        if (sr.TryGetProperty("wkid", out var wkid) &&
            wkid.ValueKind == JsonValueKind.Number &&
            wkid.TryGetInt32(out var wkidValue))
        {
            return wkidValue;
        }

        return null;
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
    double LatitudeScale)
{
    public (double Longitude, double Latitude) ImageToGround(double sample, double line)
    {
        var normalizedSample = (sample - SampleOffset) / SampleScale;
        var normalizedLine = (line - LineOffset) / LineScale;
        var longitude = (normalizedSample * LongitudeScale) + LongitudeOffset;
        var latitude = (normalizedLine * LatitudeScale) + LatitudeOffset;
        return (longitude, latitude);
    }

    public (double Sample, double Line) GroundToImage(double longitude, double latitude)
    {
        var normalizedLongitude = (longitude - LongitudeOffset) / LongitudeScale;
        var normalizedLatitude = (latitude - LatitudeOffset) / LatitudeScale;
        var sample = (normalizedLongitude * SampleScale) + SampleOffset;
        var line = (normalizedLatitude * LineScale) + LineOffset;
        return (sample, line);
    }
}

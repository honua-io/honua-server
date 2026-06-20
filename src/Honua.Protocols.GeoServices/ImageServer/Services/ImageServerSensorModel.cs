// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

/// <summary>
/// Parses the JSON payloads carried by <see cref="RasterSensorMetadata"/> into the strongly
/// typed sensor primitives the ImageServer orientation-ranked find (#1880) and image-coordinate
/// system project warp (#1881) paths consume. The payloads are kept as raw JSON in the catalog so
/// the model stays extensible; this helper isolates the parsing so the handlers stay thin.
/// </summary>
internal static class ImageServerSensorModel
{
    /// <summary>
    /// Reads the off-nadir angle (degrees from straight-down) from the exterior-orientation
    /// payload. Accepts <c>offNadirAngle</c> or <c>off_nadir_angle</c>. Returns <c>null</c> when
    /// the payload is missing, malformed, or carries no off-nadir field.
    /// </summary>
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

    /// <summary>
    /// Reads the RPC (Rational Polynomial Coefficients) image↔ground model from the RPC payload.
    /// Returns <c>null</c> when the payload is missing or does not carry the minimum offset/scale
    /// terms needed for an affine RPC normalisation. Only the offset/scale normalisation terms are
    /// required for the first-increment image↔map mapping; the full 80-coefficient polynomial is
    /// optional and, when absent, the model falls back to the offset/scale affine relationship.
    /// </summary>
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
                !TryReadAny(root, out var lineScale, "lineScale", "lineScale", "line_scale") ||
                !TryReadAny(root, out var longScale, "longScale", "longScale", "long_scale") ||
                !TryReadAny(root, out var latScale, "latScale", "latScale", "lat_scale"))
            {
                return null;
            }

            if (sampleScale == 0 || lineScale == 0 || longScale == 0 || latScale == 0)
            {
                // Zero scales would make the normalisation degenerate (divide-by-zero).
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

    private static bool TryReadAny(JsonElement root, out double value, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetDouble(root, name, out value))
            {
                return true;
            }
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

/// <summary>
/// First-order RPC image↔ground model expressed through the standard RPC offset/scale
/// normalisation terms. The image space is (sample, line) in pixels; the ground space is
/// (longitude, latitude) in degrees. The forward (ground→image) and inverse (image→ground)
/// transforms below use the linear offset/scale relationship — the affine core of the RPC
/// normalisation — which is exact for the first increment; higher-order polynomial correction
/// is deferred.
/// </summary>
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
    /// <summary>
    /// Maps an image (sample, line) pixel coordinate to a ground (longitude, latitude) coordinate
    /// using the offset/scale normalisation.
    /// </summary>
    /// <param name="sample">Pixel column.</param>
    /// <param name="line">Pixel row.</param>
    /// <returns>Ground longitude/latitude in degrees.</returns>
    public (double Longitude, double Latitude) ImageToGround(double sample, double line)
    {
        var normalizedSample = (sample - SampleOffset) / SampleScale;
        var normalizedLine = (line - LineOffset) / LineScale;
        var longitude = (normalizedSample * LongitudeScale) + LongitudeOffset;
        var latitude = (normalizedLine * LatitudeScale) + LatitudeOffset;
        return (longitude, latitude);
    }

    /// <summary>
    /// Maps a ground (longitude, latitude) coordinate to an image (sample, line) pixel coordinate
    /// using the offset/scale normalisation (inverse of <see cref="ImageToGround"/>).
    /// </summary>
    /// <param name="longitude">Ground longitude in degrees.</param>
    /// <param name="latitude">Ground latitude in degrees.</param>
    /// <returns>Image sample/line in pixels.</returns>
    public (double Sample, double Line) GroundToImage(double longitude, double latitude)
    {
        var normalizedLongitude = (longitude - LongitudeOffset) / LongitudeScale;
        var normalizedLatitude = (latitude - LatitudeOffset) / LatitudeScale;
        var sample = (normalizedLongitude * SampleScale) + SampleOffset;
        var line = (normalizedLatitude * LineScale) + LineOffset;
        return (sample, line);
    }
}

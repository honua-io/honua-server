// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Shared.Models;

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

    /// <summary>
    /// Reads pre-registered photogrammetric control points (ground control points / tie points)
    /// from the exterior-orientation payload. Control points live under a <c>controlPoints</c>
    /// array (aliases <c>tiePoints</c>, <c>gcps</c>); each entry pairs an <c>imagePoint</c>
    /// (aliases <c>sourcePoint</c>) in pixel space with a <c>referencePoint</c> (aliases
    /// <c>targetPoint</c>, <c>groundPoint</c>) in ground/map space. See ADR-0064 for the schema.
    /// Entries missing either point or with non-numeric coordinates are skipped defensively.
    /// Returns an empty list when the payload is missing, malformed, or carries no valid pairs —
    /// callers treat that as "no control points modeled" and return an honest 501 rather than
    /// fabricating tie points (automatic feature matching is out of scope, ADR-0064).
    /// </summary>
    /// <param name="metadata">The raster's sensor metadata, or <c>null</c>.</param>
    /// <param name="defaultReferenceSrid">
    /// Fallback SRID applied to reference points that carry no explicit spatial reference
    /// (typically the raster SRID).
    /// </param>
    /// <returns>The parsed control points; empty when none are modeled.</returns>
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

    private static bool TryGetArray(JsonElement root, out JsonElement array, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out array) && array.ValueKind == JsonValueKind.Array)
            {
                return true;
            }
        }

        array = default;
        return false;
    }

    private static bool TryGetObject(JsonElement root, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object)
            {
                return true;
            }
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
        // Not rewritten as .Where(...): this is a first-match short-circuit over the
        // Try-pattern (bool + out), not a pure filter — a LINQ equivalent would need an
        // intermediate nullable projection and would be harder to read than the loop.
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
/// A pre-registered photogrammetric control point pairing an image (pixel-space) location with
/// a reference (ground/map-space) location, parsed from the raster's exterior-orientation
/// payload (ADR-0064). Backs the honest <c>computeTiePoints</c> pass-through: these are stored
/// control points, never values derived by feature matching.
/// </summary>
/// <param name="ImageX">Image sample/column coordinate (pixel space).</param>
/// <param name="ImageY">Image line/row coordinate (pixel space).</param>
/// <param name="ImageSrid">Optional spatial reference of the image point; normally none (pixel space).</param>
/// <param name="ReferenceX">Reference/ground X coordinate.</param>
/// <param name="ReferenceY">Reference/ground Y coordinate.</param>
/// <param name="ReferenceZ">Optional reference/ground Z (elevation) coordinate.</param>
/// <param name="ReferenceSrid">Spatial reference of the reference point (defaults to the raster SRID).</param>
internal readonly record struct PhotogrammetricControlPoint(
    double ImageX,
    double ImageY,
    int? ImageSrid,
    double ReferenceX,
    double ReferenceY,
    double? ReferenceZ,
    int? ReferenceSrid);

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

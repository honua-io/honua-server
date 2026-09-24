// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Shared.Models;

/// <summary>
/// Normalizes a geographic longitude/latitude envelope, including spans that cross
/// the antimeridian and longitudes written outside -180..180.
/// </summary>
public static class GeographicEnvelope
{
    /// <summary>
    /// A normalized envelope. When <see cref="CrossesAntimeridian"/> is true,
    /// <see cref="West"/> is the eastern piece's west edge and <see cref="East"/> is
    /// the western piece's east edge, so west is greater than east.
    /// </summary>
    /// <param name="West">Western longitude of a single envelope, or the eastern piece's west edge when the span crosses the antimeridian.</param>
    /// <param name="South">Southern latitude.</param>
    /// <param name="East">Eastern longitude of a single envelope, or the western piece's east edge when the span crosses the antimeridian.</param>
    /// <param name="North">Northern latitude.</param>
    /// <param name="CrossesAntimeridian">True when the span must be tested as two envelopes, one on each side of ±180.</param>
    public readonly record struct Normalized(
        double West,
        double South,
        double East,
        double North,
        bool CrossesAntimeridian);

    /// <summary>
    /// Normalizes an envelope into the -180..180 longitude domain.
    /// A reversed span (west greater than east) inside that domain crosses the antimeridian.
    /// An unwrapped span such as 170..190 is folded back across ±180.
    /// A span wider than 360 degrees is rejected.
    /// </summary>
    /// <param name="minX">Minimum longitude, which may be greater than <paramref name="maxX"/> or outside -180..180.</param>
    /// <param name="minY">Minimum latitude.</param>
    /// <param name="maxX">Maximum longitude.</param>
    /// <param name="maxY">Maximum latitude.</param>
    /// <param name="normalized">The normalized envelope when the method returns true.</param>
    /// <param name="error">Why the envelope was rejected, when the method returns false.</param>
    /// <returns>True when the envelope is a usable geographic window.</returns>
    public static bool TryNormalize(
        double minX,
        double minY,
        double maxX,
        double maxY,
        out Normalized normalized,
        out string? error)
    {
        normalized = default;
        error = null;

        if (!double.IsFinite(minX) || !double.IsFinite(minY) || !double.IsFinite(maxX) || !double.IsFinite(maxY))
        {
            error = "Envelope coordinates must be finite.";
            return false;
        }

        if (minY > maxY)
        {
            error = "Envelope latitude range is invalid.";
            return false;
        }

        if (minY < -90d || maxY > 90d)
        {
            error = "Geographic latitude values must be between -90 and 90 degrees.";
            return false;
        }

        var width = maxX - minX;
        if (width > 360d)
        {
            error = "Geographic longitude span must not exceed 360 degrees.";
            return false;
        }

        if (width < 0d)
        {
            if (minX < -180d || minX > 180d || maxX < -180d || maxX > 180d)
            {
                error = "Geographic longitude values must be between -180 and 180 degrees.";
                return false;
            }

            normalized = new Normalized(minX, minY, maxX, maxY, CrossesAntimeridian: true);
            return true;
        }

        // Wider spans were rejected above, so this is exactly the full-world boundary.
        if (width >= 360d)
        {
            normalized = new Normalized(-180d, minY, 180d, maxY, CrossesAntimeridian: false);
            return true;
        }

        var west = WrapLongitude(minX);
        var east = west + width;
        if (east > 180d)
        {
            normalized = new Normalized(west, minY, east - 360d, maxY, CrossesAntimeridian: true);
            return true;
        }

        normalized = new Normalized(west, minY, east, maxY, CrossesAntimeridian: false);
        return true;
    }

    /// <summary>
    /// Folds a longitude into the half-open range [-180, 180).
    /// </summary>
    /// <param name="longitude">Longitude in degrees, possibly outside -180..180.</param>
    /// <returns>The equivalent longitude in [-180, 180).</returns>
    public static double WrapLongitude(double longitude)
        => longitude - (360d * Math.Floor((longitude + 180d) / 360d));
}

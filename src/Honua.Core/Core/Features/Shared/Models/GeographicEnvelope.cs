// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Shared.Models;

public static class GeographicEnvelope
{
    // When the span crosses the antimeridian, west is the eastern piece's west edge and is greater than east.
    public readonly record struct Normalized(
        double West,
        double South,
        double East,
        double North,
        bool CrossesAntimeridian);

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

    public static double WrapLongitude(double longitude)
        => longitude - (360d * Math.Floor((longitude + 180d) / 360d));
}

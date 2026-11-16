// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Scene.Generation;

public static class GeodesicError
{
    public static double RootGeometricError(
        double west,
        double south,
        double east,
        double north,
        double minHeight,
        double maxHeight)
    {
        var lonSpanDegrees = east >= west ? east - west : east + 360.0 - west;
        var lonSpanRad = lonSpanDegrees * Math.PI / 180.0;
        var latSpanRad = (north - south) * Math.PI / 180.0;
        var midLatRad = (south + north) * 0.5 * Math.PI / 180.0;
        var sinMid = Math.Sin(midLatRad);
        var eccentricitySquared = EcefCoordinateTransform.WgsEccentricitySquared;
        var curvature = Math.Pow(1.0 - (eccentricitySquared * sinMid * sinMid), 1.5);
        var primeVertical = EcefCoordinateTransform.WgsSemiMajorAxis
            / Math.Sqrt(1.0 - (eccentricitySquared * sinMid * sinMid));
        var meridional = EcefCoordinateTransform.WgsSemiMajorAxis * (1.0 - eccentricitySquared) / curvature;

        var lonMeters = Math.Abs(lonSpanRad) * primeVertical * Math.Cos(midLatRad);
        var latMeters = Math.Abs(latSpanRad) * meridional;
        var heightMeters = Math.Max(0.0, maxHeight - minHeight);

        var diagonal = Math.Sqrt(lonMeters * lonMeters + latMeters * latMeters + heightMeters * heightMeters);
        return Math.Max(1.0, Math.Round(diagonal, 6, MidpointRounding.AwayFromZero));
    }

    public static double RootGeometricError(double[] boundsDegrees, double minHeight, double maxHeight)
    {
        ArgumentNullException.ThrowIfNull(boundsDegrees);
        if (boundsDegrees.Length != 4)
        {
            throw new ArgumentException("boundsDegrees must have exactly 4 elements.", nameof(boundsDegrees));
        }

        return RootGeometricError(
            boundsDegrees[0],
            boundsDegrees[1],
            boundsDegrees[2],
            boundsDegrees[3],
            minHeight,
            maxHeight);
    }
}

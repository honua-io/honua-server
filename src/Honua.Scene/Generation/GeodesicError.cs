// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Scene.Generation;

/// <summary>
/// Shared geodesy for sizing a 3D Tiles root geometric error from a WGS-84
/// bounding extent. Previously this cos(midLatitude)-corrected degree-span-to-
/// meters diagonal was copy-implemented across the point-cloud, BIM/BSL, and I3S
/// builders (each with subtly different floor/rounding/height handling); a single
/// helper keeps the geodesy identical across all scene builders.
/// </summary>
public static class GeodesicError
{
    /// <summary>
    /// Computes the root geometric error (meters) as the 3D diagonal of an extent.
    /// </summary>
    /// <remarks>
    /// A longitude span with the western bound greater than the eastern bound is
    /// measured the short way across the antimeridian. Longitude meters use the
    /// prime-vertical radius at mid-latitude; latitude meters use the meridional
    /// radius. The vertical
    /// (min/max height) extent is included in the diagonal so a tall, geographically
    /// small dataset (e.g. a single skyscraper or a cliff mesh) is not understated.
    /// The result is rounded to 6 decimals (AwayFromZero) for deterministic output
    /// and floored at a positive value so a degenerate extent (a single point, or
    /// all geometry co-located) still declares a refinement budget — a zero root
    /// error defeats screen-space-error refinement and can leave a client never
    /// scheduling the root tile.
    /// </remarks>
    /// <param name="west">Western bound in degrees.</param>
    /// <param name="south">Southern bound in degrees.</param>
    /// <param name="east">Eastern bound in degrees.</param>
    /// <param name="north">Northern bound in degrees.</param>
    /// <param name="minHeight">Minimum height in meters.</param>
    /// <param name="maxHeight">Maximum height in meters.</param>
    /// <returns>The floored, rounded root geometric error in meters.</returns>
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

    /// <summary>
    /// Convenience overload accepting a 4-element <c>[west, south, east, north]</c>
    /// bounds array (the shape the partitioner and builders pass around) plus the
    /// vertical extent.
    /// </summary>
    /// <param name="boundsDegrees">Extent as <c>[west, south, east, north]</c> in degrees.</param>
    /// <param name="minHeight">Minimum height in meters.</param>
    /// <param name="maxHeight">Maximum height in meters.</param>
    /// <returns>The floored, rounded root geometric error in meters.</returns>
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

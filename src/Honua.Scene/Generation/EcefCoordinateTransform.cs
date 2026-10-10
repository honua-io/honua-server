// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Scene.Generation;

/// <summary>
/// In-process WGS-84 geographic to ECEF (EPSG:4978) coordinate transform.
/// Used by the 3D Tiles generation pipeline to encode tile vertex positions
/// in the Cartesian space CesiumJS expects for tileset content.
/// </summary>
/// <remarks>
/// Implements the standard ellipsoidal-to-Cartesian formula using the WGS-84
/// ellipsoid (a = 6 378 137.0 m, f = 1/298.257223563). Heights are
/// ellipsoidal in meters; null heights are treated as zero. The implementation
/// is deterministic, branch-light, and allocation-free.
/// </remarks>
public static class EcefCoordinateTransform
{
    /// <summary>Converts a finite ECEF point back to WGS-84 longitude, latitude and ellipsoidal height.</summary>
    public static (double Longitude, double Latitude, double Height) FromEcef(double x, double y, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
        {
            throw new ArgumentException("ECEF coordinates must be finite.");
        }

        var horizontal = Math.Sqrt(x * x + y * y);
        var semiMinor = WgsSemiMajorAxis * (1 - 1 / WgsInverseFlattening);
        if (horizontal < 0.000001)
        {
            if (Math.Abs(z) < 0.000001)
            {
                throw new ArgumentException("The earth centre has no geographic coordinate.");
            }

            return (0, z > 0 ? 90 : -90, Math.Abs(z) - semiMinor);
        }

        var latitude = Math.Atan2(z, horizontal * (1 - WgsEccentricitySquared));
        for (var iteration = 0; iteration < 12; iteration++)
        {
            var sine = Math.Sin(latitude);
            var normal = WgsSemiMajorAxis / Math.Sqrt(1 - WgsEccentricitySquared * sine * sine);
            var next = Math.Atan2(z + WgsEccentricitySquared * normal * sine, horizontal);
            if (Math.Abs(next - latitude) < 1e-14)
            {
                latitude = next;
                break;
            }

            latitude = next;
        }

        var sinLatitude = Math.Sin(latitude);
        var height = horizontal * Math.Cos(latitude) + z * sinLatitude
            - WgsSemiMajorAxis * Math.Sqrt(1 - WgsEccentricitySquared * sinLatitude * sinLatitude);
        return (Math.Atan2(y, x) * 180 / Math.PI, latitude * 180 / Math.PI, height);
    }
    /// <summary>WGS-84 semi-major axis in meters.</summary>
    public const double WgsSemiMajorAxis = 6_378_137.0;

    /// <summary>WGS-84 inverse flattening (1/f).</summary>
    public const double WgsInverseFlattening = 298.257223563;

    /// <summary>WGS-84 first eccentricity squared (e^2 = 2f - f^2).</summary>
    public static readonly double WgsEccentricitySquared = 2.0 / WgsInverseFlattening
        - 1.0 / (WgsInverseFlattening * WgsInverseFlattening);

    /// <summary>
    /// Projects a geographic coordinate (lon, lat in degrees, h in meters) to
    /// ECEF Cartesian coordinates (X, Y, Z in meters).
    /// </summary>
    public static (double X, double Y, double Z) ToEcef(double longitudeDeg, double latitudeDeg, double heightMeters)
    {
        var lonRad = longitudeDeg * Math.PI / 180.0;
        var latRad = latitudeDeg * Math.PI / 180.0;
        var sinLat = Math.Sin(latRad);
        var cosLat = Math.Cos(latRad);
        var cosLon = Math.Cos(lonRad);
        var sinLon = Math.Sin(lonRad);

        var n = WgsSemiMajorAxis / Math.Sqrt(1.0 - WgsEccentricitySquared * sinLat * sinLat);
        var x = (n + heightMeters) * cosLat * cosLon;
        var y = (n + heightMeters) * cosLat * sinLon;
        var z = (n * (1.0 - WgsEccentricitySquared) + heightMeters) * sinLat;
        return (x, y, z);
    }
}

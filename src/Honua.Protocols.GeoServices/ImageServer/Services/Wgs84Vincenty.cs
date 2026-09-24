// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.GeoServices.ImageServer.Services;

/// <summary>
/// Vincenty inverse and direct on the WGS 84 ellipsoid. Used by ImageServer
/// ground mensuration. Near-antipodal pairs that do not converge fall back to a
/// mean-radius haversine so the result stays finite.
/// </summary>
internal static class Wgs84Vincenty
{
    private const double SemiMajorAxis = 6_378_137.0;
    private const double InverseFlattening = 298.257223563;
    private const double Flattening = 1.0 / InverseFlattening;
    private const double SemiMinorAxis = SemiMajorAxis * (1.0 - Flattening);
    private const double MeanRadius = 6_371_008.8;
    private const int MaxIterations = 100;
    private const double Convergence = 1e-12;

    public static double DistanceMeters(double lon1Deg, double lat1Deg, double lon2Deg, double lat2Deg)
    {
        if (TryInverse(lon1Deg, lat1Deg, lon2Deg, lat2Deg, out var distance, out _))
        {
            return distance;
        }

        return HaversineMeters(lon1Deg, lat1Deg, lon2Deg, lat2Deg);
    }

    public static double InitialBearingDegrees(double lon1Deg, double lat1Deg, double lon2Deg, double lat2Deg)
    {
        if (TryInverse(lon1Deg, lat1Deg, lon2Deg, lat2Deg, out _, out var bearing))
        {
            return bearing < 0d ? bearing + 360d : bearing;
        }

        return HaversineBearingDegrees(lon1Deg, lat1Deg, lon2Deg, lat2Deg);
    }

    /// <summary>
    /// Point at <paramref name="distanceMeters"/> from the start along the geodesic
    /// whose initial bearing is <paramref name="bearingDegrees"/>.
    /// </summary>
    public static (double Lon, double Lat) Direct(
        double lonDeg,
        double latDeg,
        double bearingDegrees,
        double distanceMeters)
    {
        var alpha1 = DegreesToRadians(bearingDegrees);
        var sinAlpha1 = Math.Sin(alpha1);
        var cosAlpha1 = Math.Cos(alpha1);
        var tanU1 = (1.0 - Flattening) * Math.Tan(DegreesToRadians(latDeg));
        var cosU1 = 1.0 / Math.Sqrt(1.0 + (tanU1 * tanU1));
        var sinU1 = tanU1 * cosU1;
        var sigma1 = Math.Atan2(tanU1, cosAlpha1);
        var sinAlpha = cosU1 * sinAlpha1;
        var cosSquaredAlpha = 1.0 - (sinAlpha * sinAlpha);
        var uSquared = cosSquaredAlpha * ((SemiMajorAxis * SemiMajorAxis) - (SemiMinorAxis * SemiMinorAxis))
            / (SemiMinorAxis * SemiMinorAxis);
        var aCoeff = 1.0 + (uSquared / 16384.0 * (4096.0 + (uSquared * (-768.0 + (uSquared * (320.0 - (175.0 * uSquared)))))));
        var bCoeff = uSquared / 1024.0 * (256.0 + (uSquared * (-128.0 + (uSquared * (74.0 - (47.0 * uSquared))))));
        var sigma = distanceMeters / (SemiMinorAxis * aCoeff);
        double sigmaPrev;
        double cos2SigmaM = 0;
        double sinSigma = 0;
        double cosSigma = 0;
        for (var i = 0; i < MaxIterations; i++)
        {
            sigmaPrev = sigma;
            var twoSigmaM = (2.0 * sigma1) + sigma;
            cos2SigmaM = Math.Cos(twoSigmaM);
            sinSigma = Math.Sin(sigma);
            cosSigma = Math.Cos(sigma);
            var deltaSigma = bCoeff * sinSigma * (cos2SigmaM
                + (bCoeff / 4.0 * ((cosSigma * (-1.0 + (2.0 * cos2SigmaM * cos2SigmaM)))
                    - (bCoeff / 6.0 * cos2SigmaM * (-3.0 + (4.0 * sinSigma * sinSigma)) * (-3.0 + (4.0 * cos2SigmaM * cos2SigmaM))))));
            sigma = (distanceMeters / (SemiMinorAxis * aCoeff)) + deltaSigma;
            if (Math.Abs(sigma - sigmaPrev) < Convergence)
            {
                break;
            }
        }

        var lat2 = Math.Atan2(
            (sinU1 * cosSigma) + (cosU1 * sinSigma * cosAlpha1),
            (1.0 - Flattening) * Math.Sqrt((sinAlpha * sinAlpha) + Math.Pow((sinU1 * sinSigma) - (cosU1 * cosSigma * cosAlpha1), 2)));
        var lambda = Math.Atan2(
            sinSigma * sinAlpha1,
            (cosU1 * cosSigma) - (sinU1 * sinSigma * cosAlpha1));
        var cCoeff = Flattening / 16.0 * cosSquaredAlpha * (2.0 + (Flattening * (4.0 - (3.0 * cosSquaredAlpha))));
        var capitalL = lambda - ((1.0 - cCoeff) * Flattening * sinAlpha * (sigma
            + (cCoeff * sinSigma * (cos2SigmaM + (cCoeff * cosSigma * (-1.0 + (2.0 * cos2SigmaM * cos2SigmaM)))))));
        var lon2 = DegreesToRadians(lonDeg) + capitalL;
        return (WrapLongitude(RadiansToDegrees(lon2)), RadiansToDegrees(lat2));
    }

    private static bool TryInverse(
        double lon1Deg,
        double lat1Deg,
        double lon2Deg,
        double lat2Deg,
        out double distance,
        out double bearingDegrees)
    {
        distance = 0;
        bearingDegrees = 0;
        var phi1 = DegreesToRadians(lat1Deg);
        var phi2 = DegreesToRadians(lat2Deg);
        var l = DegreesToRadians(NormalizeLongitudeDelta(lon2Deg - lon1Deg));
        if (Math.Abs(l) < 1e-15 && Math.Abs(phi1 - phi2) < 1e-15)
        {
            return true;
        }

        var u1 = Math.Atan((1.0 - Flattening) * Math.Tan(phi1));
        var u2 = Math.Atan((1.0 - Flattening) * Math.Tan(phi2));
        var sinU1 = Math.Sin(u1);
        var cosU1 = Math.Cos(u1);
        var sinU2 = Math.Sin(u2);
        var cosU2 = Math.Cos(u2);
        var lambda = l;
        double sinSigma = 0;
        double cosSigma = 0;
        double sigma = 0;
        double sinAlpha = 0;
        double cosSquaredAlpha = 0;
        double cos2SigmaM = 0;
        for (var i = 0; i < MaxIterations; i++)
        {
            var sinLambda = Math.Sin(lambda);
            var cosLambda = Math.Cos(lambda);
            sinSigma = Math.Sqrt(
                Math.Pow(cosU2 * sinLambda, 2)
                + Math.Pow((cosU1 * sinU2) - (sinU1 * cosU2 * cosLambda), 2));
            if (sinSigma == 0)
            {
                return true;
            }

            cosSigma = (sinU1 * sinU2) + (cosU1 * cosU2 * cosLambda);
            sigma = Math.Atan2(sinSigma, cosSigma);
            sinAlpha = cosU1 * cosU2 * sinLambda / sinSigma;
            cosSquaredAlpha = 1.0 - (sinAlpha * sinAlpha);
            cos2SigmaM = cosSquaredAlpha == 0
                ? 0
                : cosSigma - (2.0 * sinU1 * sinU2 / cosSquaredAlpha);
            var c = Flattening / 16.0 * cosSquaredAlpha * (2.0 + (Flattening * (4.0 - (3.0 * cosSquaredAlpha))));
            var lambdaPrev = lambda;
            lambda = l + ((1.0 - c) * Flattening * sinAlpha * (sigma
                + (c * sinSigma * (cos2SigmaM + (c * cosSigma * (-1.0 + (2.0 * cos2SigmaM * cos2SigmaM)))))));
            if (Math.Abs(lambda - lambdaPrev) < Convergence)
            {
                var uSquared = cosSquaredAlpha * ((SemiMajorAxis * SemiMajorAxis) - (SemiMinorAxis * SemiMinorAxis))
                    / (SemiMinorAxis * SemiMinorAxis);
                var aCoeff = 1.0 + (uSquared / 16384.0 * (4096.0 + (uSquared * (-768.0 + (uSquared * (320.0 - (175.0 * uSquared)))))));
                var bCoeff = uSquared / 1024.0 * (256.0 + (uSquared * (-128.0 + (uSquared * (74.0 - (47.0 * uSquared))))));
                var deltaSigma = bCoeff * sinSigma * (cos2SigmaM
                    + (bCoeff / 4.0 * ((cosSigma * (-1.0 + (2.0 * cos2SigmaM * cos2SigmaM)))
                        - (bCoeff / 6.0 * cos2SigmaM * (-3.0 + (4.0 * sinSigma * sinSigma)) * (-3.0 + (4.0 * cos2SigmaM * cos2SigmaM))))));
                distance = SemiMinorAxis * aCoeff * (sigma - deltaSigma);
                bearingDegrees = RadiansToDegrees(Math.Atan2(
                    cosU2 * Math.Sin(lambda),
                    (cosU1 * sinU2) - (sinU1 * cosU2 * Math.Cos(lambda))));
                return double.IsFinite(distance);
            }
        }

        return false;
    }

    private static double HaversineMeters(double lon1, double lat1, double lon2, double lat2)
    {
        var phi1 = DegreesToRadians(lat1);
        var phi2 = DegreesToRadians(lat2);
        var dPhi = DegreesToRadians(lat2 - lat1);
        var dLambda = DegreesToRadians(NormalizeLongitudeDelta(lon2 - lon1));
        var a = (Math.Sin(dPhi / 2) * Math.Sin(dPhi / 2))
            + (Math.Cos(phi1) * Math.Cos(phi2) * Math.Sin(dLambda / 2) * Math.Sin(dLambda / 2));
        return 2d * MeanRadius * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1d - a));
    }

    private static double HaversineBearingDegrees(double lon1, double lat1, double lon2, double lat2)
    {
        var phi1 = DegreesToRadians(lat1);
        var phi2 = DegreesToRadians(lat2);
        var dLambda = DegreesToRadians(NormalizeLongitudeDelta(lon2 - lon1));
        var y = Math.Sin(dLambda) * Math.Cos(phi2);
        var x = (Math.Cos(phi1) * Math.Sin(phi2)) - (Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLambda));
        var degrees = RadiansToDegrees(Math.Atan2(y, x));
        return degrees < 0d ? degrees + 360d : degrees;
    }

    private static double NormalizeLongitudeDelta(double delta)
    {
        delta %= 360d;
        if (delta >= 180d)
        {
            delta -= 360d;
        }
        else if (delta < -180d)
        {
            delta += 360d;
        }

        return delta;
    }

    private static double WrapLongitude(double lon)
    {
        lon %= 360d;
        if (lon >= 180d)
        {
            lon -= 360d;
        }
        else if (lon < -180d)
        {
            lon += 360d;
        }

        return lon;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;

    private static double RadiansToDegrees(double radians) => radians * 180d / Math.PI;
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;
using NetTopologySuite.Geometries;

namespace Honua.Geoprocessing.Execution;

internal static class GeodesicGeometryOperations
{
    private const double Wgs84SemiMajorMeters = 6_378_137.0;
    private const double Wgs84Flattening = 1.0 / 298.257223563;
    private const double Wgs84SemiMinorMeters = Wgs84SemiMajorMeters * (1.0 - Wgs84Flattening);
    private const double Wgs84MeanRadiusMeters = 6_371_008.7714;
    private const double Wgs84AuthalicRadiusMeters = 6_371_007.1809;
    private const double DegreesToRadians = Math.PI / 180.0;
    private const double RadiansToDegrees = 180.0 / Math.PI;
    private const int MaxVincentyIterations = 200;
    private const double VincentyTolerance = 1e-12;

    internal static bool IsGeographicSrid(int srid) => GeographicSridClassifier.IsGeographicSrid(srid);

    internal static double ComputeEllipsoidalDistanceMeters(
        double lat1Deg,
        double lon1Deg,
        double lat2Deg,
        double lon2Deg)
    {
        if (!double.IsFinite(lat1Deg) || !double.IsFinite(lon1Deg)
            || !double.IsFinite(lat2Deg) || !double.IsFinite(lon2Deg))
        {
            return double.NaN;
        }

        var phi1 = lat1Deg * DegreesToRadians;
        var phi2 = lat2Deg * DegreesToRadians;
        var deltaLon = NormalizeLongitudeRadians((lon2Deg - lon1Deg) * DegreesToRadians);

        if (Math.Abs(phi1 - phi2) < 1e-15 && Math.Abs(deltaLon) < 1e-15)
        {
            return 0.0;
        }

        var u1 = Math.Atan((1.0 - Wgs84Flattening) * Math.Tan(phi1));
        var u2 = Math.Atan((1.0 - Wgs84Flattening) * Math.Tan(phi2));
        var sinU1 = Math.Sin(u1);
        var cosU1 = Math.Cos(u1);
        var sinU2 = Math.Sin(u2);
        var cosU2 = Math.Cos(u2);

        var lambda = deltaLon;
        double sinSigma = 0;
        double cosSigma = 0;
        double sigma = 0;
        double cosSqAlpha = 0;
        double cos2SigmaM = 0;

        var converged = false;
        for (var iteration = 0; iteration < MaxVincentyIterations; iteration++)
        {
            var sinLambda = Math.Sin(lambda);
            var cosLambda = Math.Cos(lambda);
            var term1 = cosU2 * sinLambda;
            var term2 = cosU1 * sinU2 - sinU1 * cosU2 * cosLambda;
            sinSigma = Math.Sqrt(term1 * term1 + term2 * term2);
            if (sinSigma == 0.0)
            {
                return 0.0;
            }

            cosSigma = sinU1 * sinU2 + cosU1 * cosU2 * cosLambda;
            sigma = Math.Atan2(sinSigma, cosSigma);
            var sinAlpha = cosU1 * cosU2 * sinLambda / sinSigma;
            cosSqAlpha = 1.0 - sinAlpha * sinAlpha;
            cos2SigmaM = cosSqAlpha == 0.0
                ? 0.0
                : cosSigma - 2.0 * sinU1 * sinU2 / cosSqAlpha;

            var c = Wgs84Flattening / 16.0 * cosSqAlpha * (4.0 + Wgs84Flattening * (4.0 - 3.0 * cosSqAlpha));
            var previousLambda = lambda;
            lambda = deltaLon + (1.0 - c) * Wgs84Flattening * sinAlpha
                * (sigma + c * sinSigma * (cos2SigmaM + c * cosSigma * (-1.0 + 2.0 * cos2SigmaM * cos2SigmaM)));

            if (Math.Abs(lambda - previousLambda) < VincentyTolerance)
            {
                converged = true;
                break;
            }
        }

        if (!converged)
        {
            // Vincenty does not converge for near-antipodal pairs; fall back to spherical Haversine.
            return ComputeHaversineDistanceMeters(lat1Deg, lon1Deg, lat2Deg, lon2Deg);
        }

        var uSq = cosSqAlpha * (Wgs84SemiMajorMeters * Wgs84SemiMajorMeters - Wgs84SemiMinorMeters * Wgs84SemiMinorMeters)
            / (Wgs84SemiMinorMeters * Wgs84SemiMinorMeters);
        var aCoeff = 1.0 + uSq / 16384.0 * (4096.0 + uSq * (-768.0 + uSq * (320.0 - 175.0 * uSq)));
        var bCoeff = uSq / 1024.0 * (256.0 + uSq * (-128.0 + uSq * (74.0 - 47.0 * uSq)));
        var deltaSigma = bCoeff * sinSigma
            * (cos2SigmaM + bCoeff / 4.0
                * (cosSigma * (-1.0 + 2.0 * cos2SigmaM * cos2SigmaM)
                    - bCoeff / 6.0 * cos2SigmaM * (-3.0 + 4.0 * sinSigma * sinSigma)
                        * (-3.0 + 4.0 * cos2SigmaM * cos2SigmaM)));

        return Wgs84SemiMinorMeters * aCoeff * (sigma - deltaSigma);
    }

    internal static double ComputeHaversineDistanceMeters(
        double lat1Deg,
        double lon1Deg,
        double lat2Deg,
        double lon2Deg)
    {
        var phi1 = lat1Deg * DegreesToRadians;
        var phi2 = lat2Deg * DegreesToRadians;
        var dPhi = (lat2Deg - lat1Deg) * DegreesToRadians;
        var dLambda = NormalizeLongitudeRadians((lon2Deg - lon1Deg) * DegreesToRadians);

        var sinHalfPhi = Math.Sin(dPhi * 0.5);
        var sinHalfLambda = Math.Sin(dLambda * 0.5);
        var h = sinHalfPhi * sinHalfPhi + Math.Cos(phi1) * Math.Cos(phi2) * sinHalfLambda * sinHalfLambda;
        h = Math.Clamp(h, 0.0, 1.0);
        return 2.0 * Wgs84MeanRadiusMeters * Math.Asin(Math.Sqrt(h));
    }

    internal static double ComputeGeodesicLengthMeters(Geometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (geometry.IsEmpty)
        {
            return 0.0;
        }

        return geometry switch
        {
            LineString line => ComputeCoordinateSequenceLengthMeters(line.CoordinateSequence),
            Polygon polygon => ComputePolygonPerimeterMeters(polygon),
            GeometryCollection collection => SumCollection(collection, ComputeGeodesicLengthMeters),
            _ => 0.0,
        };
    }

    internal static double ComputeGeodesicAreaSquareMeters(Geometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (geometry.IsEmpty)
        {
            return 0.0;
        }

        return geometry switch
        {
            Polygon polygon => ComputePolygonAreaSquareMeters(polygon),
            GeometryCollection collection => SumCollection(collection, ComputeGeodesicAreaSquareMeters),
            _ => 0.0,
        };
    }

    internal static Geometry BufferGeodesic(Geometry geometry, double distanceMeters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        cancellationToken.ThrowIfCancellationRequested();

        var centroid = geometry.Centroid;
        var originLon = centroid.IsEmpty ? 0.0 : centroid.X;
        var originLat = centroid.IsEmpty ? 0.0 : Math.Clamp(centroid.Y, -89.9, 89.9);
        var cosOriginLat = Math.Max(1e-6, Math.Cos(originLat * DegreesToRadians));
        var metersPerDegLat = 111_132.92 - 559.82 * Math.Cos(2.0 * originLat * DegreesToRadians)
            + 1.175 * Math.Cos(4.0 * originLat * DegreesToRadians);
        var metersPerDegLon = 111_412.84 * cosOriginLat - 93.5 * Math.Cos(3.0 * originLat * DegreesToRadians);

        var local = geometry.Copy();
        local.Apply(new CoordinateTransformFilter((lon, lat) =>
        {
            var dLon = WrapLongitudeDeltaDegrees(lon - originLon);
            var dLat = lat - originLat;
            return (dLon * metersPerDegLon, dLat * metersPerDegLat);
        }));

        var bufferedLocal = GeometryBufferJobExecutor.BufferCooperatively(local, distanceMeters, cancellationToken);
        var bufferedGeo = bufferedLocal.Copy();
        bufferedGeo.Apply(new CoordinateTransformFilter((x, y) =>
        {
            var lat = Math.Clamp(originLat + y / metersPerDegLat, -90.0, 90.0);
            var lon = NormalizeLongitudeDegrees(originLon + x / metersPerDegLon);
            return (lon, lat);
        }));
        bufferedGeo.SRID = geometry.SRID;
        return bufferedGeo;
    }

    internal static bool TryNormalizeLinearUnit(string? unit, out string normalized, out double metersPerUnit)
    {
        if (string.IsNullOrWhiteSpace(unit))
        {
            normalized = "meters";
            metersPerUnit = 1.0;
            return true;
        }

        switch (unit.Trim().ToLowerInvariant())
        {
            case "m" or "meter" or "meters" or "metre" or "metres":
                normalized = "meters";
                metersPerUnit = 1.0;
                return true;
            case "km" or "kilometer" or "kilometers" or "kilometre" or "kilometres":
                normalized = "kilometers";
                metersPerUnit = 1_000.0;
                return true;
            case "ft" or "foot" or "feet":
                normalized = "feet";
                metersPerUnit = 0.3048;
                return true;
            case "yd" or "yard" or "yards":
                normalized = "yards";
                metersPerUnit = 0.9144;
                return true;
            case "mi" or "mile" or "miles":
                normalized = "miles";
                metersPerUnit = 1_609.344;
                return true;
            case "nmi" or "nautical-mile" or "nautical-miles" or "nautical_miles":
                normalized = "nautical-miles";
                metersPerUnit = 1_852.0;
                return true;
            default:
                normalized = "";
                metersPerUnit = 0.0;
                return false;
        }
    }

    internal static bool TryNormalizeAreaUnit(string? unit, out string normalized, out double squareMetersPerUnit)
    {
        if (string.IsNullOrWhiteSpace(unit))
        {
            normalized = "square-meters";
            squareMetersPerUnit = 1.0;
            return true;
        }

        switch (unit.Trim().ToLowerInvariant())
        {
            case "m2" or "sqm" or "square-meter" or "square-meters" or "square_meters" or "square-metre" or "square-metres":
                normalized = "square-meters";
                squareMetersPerUnit = 1.0;
                return true;
            case "km2" or "sqkm" or "square-kilometer" or "square-kilometers" or "square_kilometers":
                normalized = "square-kilometers";
                squareMetersPerUnit = 1_000_000.0;
                return true;
            case "ha" or "hectare" or "hectares":
                normalized = "hectares";
                squareMetersPerUnit = 10_000.0;
                return true;
            case "ac" or "acre" or "acres":
                normalized = "acres";
                squareMetersPerUnit = 4_046.8564224;
                return true;
            case "ft2" or "sqft" or "square-foot" or "square-feet" or "square_feet":
                normalized = "square-feet";
                squareMetersPerUnit = 0.09290304;
                return true;
            case "mi2" or "sqmi" or "square-mile" or "square-miles" or "square_miles":
                normalized = "square-miles";
                squareMetersPerUnit = 2_589_988.110336;
                return true;
            default:
                normalized = "";
                squareMetersPerUnit = 0.0;
                return false;
        }
    }

    private static double ComputePolygonPerimeterMeters(Polygon polygon)
    {
        var total = ComputeCoordinateSequenceLengthMeters(polygon.ExteriorRing.CoordinateSequence);
        for (var i = 0; i < polygon.NumInteriorRings; i++)
        {
            total += ComputeCoordinateSequenceLengthMeters(polygon.GetInteriorRingN(i).CoordinateSequence);
        }

        return total;
    }

    private static double ComputeCoordinateSequenceLengthMeters(CoordinateSequence sequence)
    {
        if (sequence.Count < 2)
        {
            return 0.0;
        }

        var total = 0.0;
        for (var i = 1; i < sequence.Count; i++)
        {
            total += ComputeEllipsoidalDistanceMeters(
                sequence.GetY(i - 1),
                sequence.GetX(i - 1),
                sequence.GetY(i),
                sequence.GetX(i));
        }

        return total;
    }

    private static double ComputePolygonAreaSquareMeters(Polygon polygon)
    {
        var area = ComputeRingAreaSquareMeters(polygon.ExteriorRing.CoordinateSequence);
        for (var i = 0; i < polygon.NumInteriorRings; i++)
        {
            area -= ComputeRingAreaSquareMeters(polygon.GetInteriorRingN(i).CoordinateSequence);
        }

        return Math.Max(0.0, area);
    }

    private static double ComputeRingAreaSquareMeters(CoordinateSequence ring)
    {
        var count = ring.Count;
        if (count < 4)
        {
            return 0.0;
        }

        var total = 0.0;
        for (var i = 0; i < count - 1; i++)
        {
            var lon1 = ring.GetX(i) * DegreesToRadians;
            var lat1 = ring.GetY(i) * DegreesToRadians;
            var lon2 = ring.GetX(i + 1) * DegreesToRadians;
            var lat2 = ring.GetY(i + 1) * DegreesToRadians;
            var dLon = NormalizeLongitudeRadians(lon2 - lon1);
            total += dLon * (2.0 + Math.Sin(lat1) + Math.Sin(lat2));
        }

        return Math.Abs(total) * Wgs84AuthalicRadiusMeters * Wgs84AuthalicRadiusMeters * 0.5;
    }

    private static double SumCollection(GeometryCollection collection, Func<Geometry, double> measure)
    {
        var total = 0.0;
        for (var i = 0; i < collection.NumGeometries; i++)
        {
            total += measure(collection.GetGeometryN(i));
        }

        return total;
    }

    private static double NormalizeLongitudeRadians(double radians)
    {
        while (radians > Math.PI)
        {
            radians -= 2.0 * Math.PI;
        }

        while (radians < -Math.PI)
        {
            radians += 2.0 * Math.PI;
        }

        return radians;
    }

    private static double WrapLongitudeDeltaDegrees(double delta)
    {
        while (delta > 180.0)
        {
            delta -= 360.0;
        }

        while (delta < -180.0)
        {
            delta += 360.0;
        }

        return delta;
    }

    private static double NormalizeLongitudeDegrees(double lon)
    {
        while (lon > 180.0)
        {
            lon -= 360.0;
        }

        while (lon < -180.0)
        {
            lon += 360.0;
        }

        return lon;
    }

    private sealed class CoordinateTransformFilter(Func<double, double, (double X, double Y)> transform)
        : ICoordinateSequenceFilter
    {
        public bool Done => false;

        public bool GeometryChanged => true;

        public void Filter(CoordinateSequence seq, int i)
        {
            var (x, y) = transform(seq.GetX(i), seq.GetY(i));
            seq.SetX(i, x);
            seq.SetY(i, y);
        }
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.Operation.Union;

namespace Honua.Geoprocessing.Execution;

internal static class GeodesicGeometryOperations
{
    private const double Wgs84SemiMajorAxis = 6_378_137.0;
    private const double Wgs84Flattening = 1.0 / 298.257223563;
    private const double Wgs84SemiMinorAxis = Wgs84SemiMajorAxis * (1.0 - Wgs84Flattening);
    private const double Wgs84EccentricitySquared = (2.0 * Wgs84Flattening) - (Wgs84Flattening * Wgs84Flattening);
    private static readonly double Wgs84Eccentricity = Math.Sqrt(Wgs84EccentricitySquared);
    private const double DegreesToRadians = Math.PI / 180.0;
    private const double RadiansToDegrees = 180.0 / Math.PI;

    private static readonly double[] GaussNodes =
    [
        -0.8611363115940526,
        -0.3399810435848563,
        0.3399810435848563,
        0.8611363115940526
    ];

    private static readonly double[] GaussWeights =
    [
        0.3478548451374538,
        0.6521451548625461,
        0.6521451548625461,
        0.3478548451374538
    ];

    public static bool IsGeographicSrid(int srid)
        => srid > 0 && SpatialReference.Create(srid).IsGeographic;

    public static bool TryParseLengthUnitFactorToMeters(string? rawUnit, out double toMetersFactor, out string canonicalUnit)
    {
        if (string.IsNullOrWhiteSpace(rawUnit))
        {
            toMetersFactor = 1.0;
            canonicalUnit = "meters";
            return false;
        }

        switch (rawUnit.Trim().ToLowerInvariant())
        {
            case "m" or "meter" or "meters" or "metre" or "metres":
                toMetersFactor = 1.0;
                canonicalUnit = "meters";
                return true;
            case "km" or "kilometer" or "kilometers" or "kilometre" or "kilometres":
                toMetersFactor = 1_000.0;
                canonicalUnit = "kilometers";
                return true;
            case "ft" or "foot" or "feet":
                toMetersFactor = 0.3048;
                canonicalUnit = "feet";
                return true;
            case "mi" or "mile" or "miles":
                toMetersFactor = 1_609.344;
                canonicalUnit = "miles";
                return true;
            default:
                toMetersFactor = 1.0;
                canonicalUnit = string.Empty;
                return false;
        }
    }

    public static bool TryParseAreaUnitFactorFromSquareMeters(string? rawUnit, out double fromSquareMetersFactor, out string canonicalUnit)
    {
        if (string.IsNullOrWhiteSpace(rawUnit))
        {
            fromSquareMetersFactor = 1.0;
            canonicalUnit = "square-meters";
            return false;
        }

        switch (rawUnit.Trim().ToLowerInvariant())
        {
            case "m2" or "sqm" or "sq-m" or "square-meter" or "square-meters" or "square_meters" or "squaremeters" or "meters" or "meter":
                fromSquareMetersFactor = 1.0;
                canonicalUnit = "square-meters";
                return true;
            case "km2" or "sqkm" or "sq-km" or "square-kilometer" or "square-kilometers" or "square_kilometers" or "squarekilometers":
                fromSquareMetersFactor = 1e-6;
                canonicalUnit = "square-kilometers";
                return true;
            default:
                fromSquareMetersFactor = 1.0;
                canonicalUnit = string.Empty;
                return false;
        }
    }

    public static double ComputeGeodesicLengthMeters(Geometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (geometry.IsEmpty)
        {
            return 0.0;
        }

        return geometry switch
        {
            Point or MultiPoint => 0.0,
            LineString line => ComputeSequenceLengthMeters(line.CoordinateSequence),
            Polygon polygon => ComputePolygonPerimeterMeters(polygon),
            GeometryCollection collection => ComputeCollectionLengthMeters(collection),
            _ => 0.0
        };
    }

    public static double ComputeGeodesicAreaSquareMeters(Geometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (geometry.IsEmpty)
        {
            return 0.0;
        }

        return geometry switch
        {
            Polygon polygon => ComputePolygonAreaSquareMeters(polygon),
            MultiPolygon multiPolygon => ComputeMultiPolygonAreaSquareMeters(multiPolygon),
            GeometryCollection collection => ComputeCollectionAreaSquareMeters(collection),
            _ => 0.0
        };
    }

    public static Geometry BufferGeodesic(Geometry geometry, double distanceMeters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        cancellationToken.ThrowIfCancellationRequested();

        if (geometry.IsEmpty)
        {
            return geometry.Copy();
        }

        if (geometry.NumGeometries <= 1)
        {
            return BufferSingleGeodesic(geometry, distanceMeters, cancellationToken);
        }

        var parts = new List<Geometry>(geometry.NumGeometries);
        for (var i = 0; i < geometry.NumGeometries; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var part = geometry.GetGeometryN(i);
            if (part is null || part.IsEmpty)
            {
                continue;
            }

            var bufferedPart = BufferSingleGeodesic(part, distanceMeters, cancellationToken);
            if (bufferedPart is { IsEmpty: false })
            {
                parts.Add(bufferedPart);
            }
        }

        if (parts.Count == 0)
        {
            return geometry.Factory.CreateEmpty((Dimension)geometry.Dimension);
        }

        var result = parts.Count == 1 ? parts[0] : UnaryUnionOp.Union(parts);
        result.SRID = geometry.SRID;
        return result;
    }

    private static Geometry BufferSingleGeodesic(Geometry geometry, double distanceMeters, CancellationToken cancellationToken)
    {
        var centroid = geometry.Centroid.Coordinate;
        var originLonDeg = double.IsFinite(centroid.X) ? centroid.X : 0.0;
        var originLatDeg = double.IsFinite(centroid.Y) ? Math.Clamp(centroid.Y, -89.999999, 89.999999) : 0.0;

        var (metersPerDegreeLon, metersPerDegreeLat) = ComputeLocalMetersPerDegree(originLatDeg);

        var editor = new GeometryEditor(geometry.Factory);
        var toLocalMeters = editor.Edit(
            geometry,
            new LocalTangentForwardOperation(originLonDeg, originLatDeg, metersPerDegreeLon, metersPerDegreeLat));

        var bufferedMeters = GeometryBufferJobExecutor.BufferCooperatively(toLocalMeters, distanceMeters, cancellationToken);
        if (bufferedMeters is null || bufferedMeters.IsEmpty)
        {
            return geometry.Factory.CreateEmpty(Dimension.Surface);
        }

        var backToDegrees = editor.Edit(
            bufferedMeters,
            new LocalTangentInverseOperation(originLonDeg, originLatDeg, metersPerDegreeLon, metersPerDegreeLat));
        backToDegrees.SRID = geometry.SRID;
        return backToDegrees;
    }

    private static (double MetersPerDegreeLon, double MetersPerDegreeLat) ComputeLocalMetersPerDegree(double latitudeDeg)
    {
        var latRad = Math.Clamp(latitudeDeg, -89.999999, 89.999999) * DegreesToRadians;
        var sinLat = Math.Sin(latRad);
        var cosLat = Math.Max(1e-9, Math.Cos(latRad));
        var denom = 1.0 - (Wgs84EccentricitySquared * sinLat * sinLat);
        var sqrtDenom = Math.Sqrt(denom);

        // Prime vertical radius of curvature N(φ) and meridional radius of curvature M(φ).
        var primeVerticalN = Wgs84SemiMajorAxis / sqrtDenom;
        var meridionalM = Wgs84SemiMajorAxis * (1.0 - Wgs84EccentricitySquared) / (denom * sqrtDenom);

        var metersPerDegreeLon = primeVerticalN * cosLat * DegreesToRadians;
        var metersPerDegreeLat = meridionalM * DegreesToRadians;
        return (metersPerDegreeLon, metersPerDegreeLat);
    }

    private static double ComputeCollectionLengthMeters(GeometryCollection collection)
    {
        var total = 0.0;
        for (var i = 0; i < collection.NumGeometries; i++)
        {
            total += ComputeGeodesicLengthMeters(collection.GetGeometryN(i));
        }

        return total;
    }

    private static double ComputePolygonPerimeterMeters(Polygon polygon)
    {
        var total = ComputeSequenceLengthMeters(polygon.ExteriorRing.CoordinateSequence);
        for (var i = 0; i < polygon.NumInteriorRings; i++)
        {
            total += ComputeSequenceLengthMeters(polygon.GetInteriorRingN(i).CoordinateSequence);
        }

        return total;
    }

    private static double ComputeSequenceLengthMeters(CoordinateSequence? sequence)
    {
        if (sequence is null || sequence.Count < 2)
        {
            return 0.0;
        }

        var total = 0.0;
        var prevLon = sequence.GetX(0);
        var prevLat = sequence.GetY(0);
        for (var i = 1; i < sequence.Count; i++)
        {
            var currLon = sequence.GetX(i);
            var currLat = sequence.GetY(i);
            total += ComputeEllipsoidalDistanceMeters(prevLon, prevLat, currLon, currLat);
            prevLon = currLon;
            prevLat = currLat;
        }

        return total;
    }

    private static double ComputeMultiPolygonAreaSquareMeters(MultiPolygon multiPolygon)
    {
        var total = 0.0;
        for (var i = 0; i < multiPolygon.NumGeometries; i++)
        {
            total += ComputeGeodesicAreaSquareMeters(multiPolygon.GetGeometryN(i));
        }

        return total;
    }

    private static double ComputeCollectionAreaSquareMeters(GeometryCollection collection)
    {
        var total = 0.0;
        for (var i = 0; i < collection.NumGeometries; i++)
        {
            total += ComputeGeodesicAreaSquareMeters(collection.GetGeometryN(i));
        }

        return total;
    }

    private static double ComputePolygonAreaSquareMeters(Polygon polygon)
    {
        if (polygon.IsEmpty)
        {
            return 0.0;
        }

        var area = ComputeRingAreaSquareMeters(polygon.ExteriorRing.CoordinateSequence);
        for (var i = 0; i < polygon.NumInteriorRings; i++)
        {
            area -= ComputeRingAreaSquareMeters(polygon.GetInteriorRingN(i).CoordinateSequence);
        }

        return Math.Max(0.0, area);
    }

    private static double ComputeRingAreaSquareMeters(CoordinateSequence? sequence)
    {
        if (sequence is null || sequence.Count < 3)
        {
            return 0.0;
        }

        var signedArea = 0.0;
        var count = sequence.Count;
        for (var i = 0; i < count - 1; i++)
        {
            var lon1 = sequence.GetX(i) * DegreesToRadians;
            var lat1 = Math.Clamp(sequence.GetY(i), -90.0, 90.0) * DegreesToRadians;
            var lon2 = sequence.GetX(i + 1) * DegreesToRadians;
            var lat2 = Math.Clamp(sequence.GetY(i + 1), -90.0, 90.0) * DegreesToRadians;

            var dLon = WrapDeltaLongitudeRadians(lon2 - lon1);
            if (Math.Abs(dLon) < 1e-18)
            {
                continue;
            }

            // Integrate F(φ) = ∫ M(φ) N(φ) cos(φ) dφ over the segment using 4-point
            // Gauss-Legendre quadrature so the WGS-84 ellipsoidal surface integral is exact.
            var midLat = 0.5 * (lat1 + lat2);
            var halfDLat = 0.5 * (lat2 - lat1);
            var segmentIntegral = 0.0;
            for (var q = 0; q < GaussNodes.Length; q++)
            {
                var lat = midLat + (halfDLat * GaussNodes[q]);
                segmentIntegral += GaussWeights[q] * EllipsoidalAreaLatitudePrimitive(lat);
            }

            signedArea += 0.5 * dLon * segmentIntegral;
        }

        return Math.Abs(signedArea);
    }

    private static double EllipsoidalAreaLatitudePrimitive(double latRad)
    {
        var sinLat = Math.Sin(latRad);
        var eSin = Wgs84Eccentricity * sinLat;
        var oneMinusESinSq = 1.0 - (eSin * eSin);

        var term1 = sinLat / oneMinusESinSq;
        var term2 = (0.5 / Wgs84Eccentricity) * Math.Log((1.0 + eSin) / (1.0 - eSin));
        return 0.5 * Wgs84SemiMajorAxis * Wgs84SemiMajorAxis * (1.0 - Wgs84EccentricitySquared) * (term1 + term2);
    }

    private static double WrapDeltaLongitudeRadians(double dLon)
    {
        while (dLon > Math.PI)
        {
            dLon -= 2.0 * Math.PI;
        }

        while (dLon < -Math.PI)
        {
            dLon += 2.0 * Math.PI;
        }

        return dLon;
    }

    internal static double ComputeEllipsoidalDistanceMeters(double lon1Deg, double lat1Deg, double lon2Deg, double lat2Deg)
    {
        if (Math.Abs(lon1Deg - lon2Deg) <= 1e-12 && Math.Abs(lat1Deg - lat2Deg) <= 1e-12)
        {
            return 0.0;
        }

        var phi1 = Math.Clamp(lat1Deg, -90.0, 90.0) * DegreesToRadians;
        var phi2 = Math.Clamp(lat2Deg, -90.0, 90.0) * DegreesToRadians;
        var deltaLambda = WrapDeltaLongitudeRadians((lon2Deg - lon1Deg) * DegreesToRadians);

        var u1 = Math.Atan((1.0 - Wgs84Flattening) * Math.Tan(phi1));
        var u2 = Math.Atan((1.0 - Wgs84Flattening) * Math.Tan(phi2));
        var sinU1 = Math.Sin(u1);
        var cosU1 = Math.Cos(u1);
        var sinU2 = Math.Sin(u2);
        var cosU2 = Math.Cos(u2);

        var lambda = deltaLambda;
        double sinSigma = 0.0, cosSigma = 0.0, sigma = 0.0, cosSqAlpha = 0.0, cos2SigmaM = 0.0;

        for (var iter = 0; iter < 100; iter++)
        {
            var sinLambda = Math.Sin(lambda);
            var cosLambda = Math.Cos(lambda);
            var t1 = cosU2 * sinLambda;
            var t2 = (cosU1 * sinU2) - (sinU1 * cosU2 * cosLambda);
            sinSigma = Math.Sqrt((t1 * t1) + (t2 * t2));
            if (Math.Abs(sinSigma) <= 1e-15)
            {
                return 0.0;
            }

            cosSigma = (sinU1 * sinU2) + (cosU1 * cosU2 * cosLambda);
            sigma = Math.Atan2(sinSigma, cosSigma);
            var sinAlpha = cosU1 * cosU2 * sinLambda / sinSigma;
            cosSqAlpha = 1.0 - (sinAlpha * sinAlpha);
            cos2SigmaM = cosSqAlpha > 1e-12
                ? cosSigma - (2.0 * sinU1 * sinU2 / cosSqAlpha)
                : 0.0;

            var c = (Wgs84Flattening / 16.0) * cosSqAlpha * (4.0 + (Wgs84Flattening * (4.0 - (3.0 * cosSqAlpha))));
            var nextLambda = deltaLambda + ((1.0 - c) * Wgs84Flattening * sinAlpha *
                (sigma + (c * sinSigma * (cos2SigmaM + (c * cosSigma * (-1.0 + (2.0 * cos2SigmaM * cos2SigmaM)))))));

            if (Math.Abs(nextLambda - lambda) <= 1e-12)
            {
                lambda = nextLambda;
                break;
            }

            lambda = nextLambda;
        }

        var uSq = cosSqAlpha * ((Wgs84SemiMajorAxis * Wgs84SemiMajorAxis) - (Wgs84SemiMinorAxis * Wgs84SemiMinorAxis))
            / (Wgs84SemiMinorAxis * Wgs84SemiMinorAxis);
        var bigA = 1.0 + ((uSq / 16384.0) * (4096.0 + (uSq * (-768.0 + (uSq * (320.0 - (175.0 * uSq)))))));
        var bigB = (uSq / 1024.0) * (256.0 + (uSq * (-128.0 + (uSq * (74.0 - (47.0 * uSq))))));
        var deltaSigma = bigB * sinSigma * (cos2SigmaM + ((bigB / 4.0) *
            ((cosSigma * (-1.0 + (2.0 * cos2SigmaM * cos2SigmaM))) -
             ((bigB / 6.0) * cos2SigmaM * (-3.0 + (4.0 * sinSigma * sinSigma)) * (-3.0 + (4.0 * cos2SigmaM * cos2SigmaM))))));

        var distance = Wgs84SemiMinorAxis * bigA * (sigma - deltaSigma);
        return double.IsFinite(distance) && distance >= 0.0 ? distance : 0.0;
    }

    private sealed class LocalTangentForwardOperation(
        double originLonDeg,
        double originLatDeg,
        double metersPerDegreeLon,
        double metersPerDegreeLat) : GeometryEditor.CoordinateOperation
    {
        public override Coordinate[] Edit(Coordinate[] coordinates, Geometry geometry)
        {
            ArgumentNullException.ThrowIfNull(coordinates);
            var projected = new Coordinate[coordinates.Length];
            for (var i = 0; i < coordinates.Length; i++)
            {
                var source = coordinates[i];
                var copy = source.Copy();
                copy.X = (source.X - originLonDeg) * metersPerDegreeLon;
                copy.Y = (source.Y - originLatDeg) * metersPerDegreeLat;
                projected[i] = copy;
            }

            return projected;
        }
    }

    private sealed class LocalTangentInverseOperation(
        double originLonDeg,
        double originLatDeg,
        double metersPerDegreeLon,
        double metersPerDegreeLat) : GeometryEditor.CoordinateOperation
    {
        public override Coordinate[] Edit(Coordinate[] coordinates, Geometry geometry)
        {
            ArgumentNullException.ThrowIfNull(coordinates);
            var unprojected = new Coordinate[coordinates.Length];
            for (var i = 0; i < coordinates.Length; i++)
            {
                var source = coordinates[i];
                var copy = source.Copy();
                copy.X = originLonDeg + (source.X / metersPerDegreeLon);
                copy.Y = Math.Clamp(originLatDeg + (source.Y / metersPerDegreeLat), -90.0, 90.0);
                unprojected[i] = copy;
            }

            return unprojected;
        }
    }
}

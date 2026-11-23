// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

// Ground measure is geodesic, because Web Mercator and degree coordinates make a planar distance wrong by a large factor; only a projected system already in meters uses planar meters.
internal static class ImageServerMensurationMath
{
    internal const double MeanEarthRadiusMeters = 6371008.8;
    internal const double AuthalicEarthRadiusMeters = 6371007.1809;

    internal enum MeasureSpace
    {
        Geodesic,

        PlanarMeters,
    }

    internal static bool IsGeographicSrid(int srid)
        => GeographicSridClassifier.IsGeographicOrUnlistedGeographicRangeSrid(srid);

    internal static bool TryConvertToLonLat(double x, double y, int srid, out double lon, out double lat)
        => TryConvertToLonLat(x, y, srid, IsGeographicSrid(srid), out lon, out lat);

    internal static bool TryConvertToLonLat(double x, double y, int srid, bool sridIsGeographic, out double lon, out double lat)
    {
        if (SpatialReferenceExtensions.NormalizeWebMercatorSrid(srid) == 3857)
        {
            (lon, lat) = WebMercatorMath.WebMercatorToLonLat(x, y);
            return true;
        }

        if (sridIsGeographic)
        {
            lon = x;
            lat = y;
            return true;
        }

        lon = 0d;
        lat = 0d;
        return false;
    }

    internal static double GeodesicDistanceMeters(double lon1, double lat1, double lon2, double lat2)
        => Wgs84Vincenty.DistanceMeters(lon1, lat1, lon2, lat2);

    internal static double ShadowHeightMeters(double shadowLengthMeters, double sunElevationDegrees)
        => shadowLengthMeters * Math.Tan(DegreesToRadians(sunElevationDegrees));

    internal static double PlanarDistanceMeters(double x1, double y1, double x2, double y2)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    internal static double InitialBearingDegrees(double lon1, double lat1, double lon2, double lat2)
        => Wgs84Vincenty.InitialBearingDegrees(lon1, lat1, lon2, lat2);

    internal static double PlanarBearingDegrees(double x1, double y1, double x2, double y2)
    {
        var degrees = RadiansToDegrees(Math.Atan2(x2 - x1, y2 - y1));
        return NormalizeBearing(degrees);
    }

    internal static double GeodesicRingAreaSquareMeters(IReadOnlyList<(double Lon, double Lat)> ring)
    {
        if (ring.Count < 3)
        {
            return 0d;
        }

        var lons = new double[ring.Count];
        var lats = new double[ring.Count];
        for (var i = 0; i < ring.Count; i++)
        {
            lons[i] = ring[i].Lon;
            lats[i] = ring[i].Lat;
        }

        UnwrapLongitudesInPlace(lons);
        var originLon = 0d;
        var originLat = 0d;
        for (var i = 0; i < lons.Length; i++)
        {
            originLon += lons[i];
            originLat += lats[i];
        }

        originLon /= lons.Length;
        originLat /= lats.Length;

        const double segmentMeters = 10_000d;
        var densified = new List<(double X, double Y)>(ring.Count * 4);
        for (var i = 0; i < ring.Count; i++)
        {
            var j = (i + 1) % ring.Count;
            var start = (lons[i], lats[i]);
            var end = (lons[j], lats[j]);
            var edgeLength = Wgs84Vincenty.DistanceMeters(start.Item1, start.Item2, end.Item1, end.Item2);
            var steps = Math.Max(1, (int)Math.Ceiling(edgeLength / segmentMeters));
            var bearing = Wgs84Vincenty.InitialBearingDegrees(start.Item1, start.Item2, end.Item1, end.Item2);
            for (var step = 0; step < steps; step++)
            {
                var point = step == 0
                    ? start
                    : Wgs84Vincenty.Direct(start.Item1, start.Item2, bearing, edgeLength * step / steps);
                var distance = Wgs84Vincenty.DistanceMeters(originLon, originLat, point.Item1, point.Item2);
                var azimuth = DegreesToRadians(
                    Wgs84Vincenty.InitialBearingDegrees(originLon, originLat, point.Item1, point.Item2));
                var halfAngle = Math.Min(distance / (2d * AuthalicEarthRadiusMeters), Math.PI / 2d);
                var rho = 2d * AuthalicEarthRadiusMeters * Math.Sin(halfAngle);
                densified.Add((rho * Math.Sin(azimuth), rho * Math.Cos(azimuth)));
            }
        }

        return PlanarRingAreaSquareMeters(densified);
    }

    internal static double GeodesicPolygonAreaSquareMeters(
        IReadOnlyList<IReadOnlyList<(double Lon, double Lat)>> rings)
    {
        if (rings.Count == 0)
        {
            return 0d;
        }

        var exteriorRing = rings[0];
        var total = GeodesicRingAreaSquareMeters(exteriorRing);
        for (var i = 1; i < rings.Count; i++)
        {
            var ring = rings[i];
            var ringArea = GeodesicRingAreaSquareMeters(ring);
            if (IsInteriorHoleRing(ring, exteriorRing, unwrapLongitudes: true))
            {
                total -= ringArea;
            }
            else
            {
                total += ringArea;
            }
        }

        return Math.Max(0d, total);
    }

    internal static double PlanarPolygonAreaSquareMeters(
        IReadOnlyList<IReadOnlyList<(double X, double Y)>> rings)
    {
        if (rings.Count == 0)
        {
            return 0d;
        }

        var exteriorRing = rings[0];
        var total = PlanarRingAreaSquareMeters(exteriorRing);
        for (var i = 1; i < rings.Count; i++)
        {
            var ring = rings[i];
            var ringArea = PlanarRingAreaSquareMeters(ring);
            if (IsInteriorHoleRing(ring, exteriorRing, unwrapLongitudes: false))
            {
                total -= ringArea;
            }
            else
            {
                total += ringArea;
            }
        }

        return Math.Max(0d, total);
    }

    internal static bool IsInteriorHoleRing(
        IReadOnlyList<(double X, double Y)> candidateRing,
        IReadOnlyList<(double X, double Y)> exteriorRing,
        bool unwrapLongitudes)
    {
        if (candidateRing.Count < 3 || exteriorRing.Count < 3)
        {
            return true;
        }

        var exteriorSigned = SignedRingArea(exteriorRing, unwrapLongitudes);
        var candidateSigned = SignedRingArea(candidateRing, unwrapLongitudes);
        if (Math.Abs(exteriorSigned) > 1e-12 &&
            Math.Abs(candidateSigned) > 1e-12 &&
            Math.Sign(exteriorSigned) != Math.Sign(candidateSigned))
        {
            return true;
        }

        var testPoint = SignedAreaCentroid(candidateRing, unwrapLongitudes);
        return IsPointInRing(testPoint.X, testPoint.Y, exteriorRing);
    }

    private static double SignedRingArea(IReadOnlyList<(double X, double Y)> ring, bool unwrapLongitudes)
    {
        if (ring.Count < 3)
        {
            return 0d;
        }

        var xs = new double[ring.Count];
        var ys = new double[ring.Count];
        for (var i = 0; i < ring.Count; i++)
        {
            xs[i] = ring[i].X;
            ys[i] = ring[i].Y;
        }

        if (unwrapLongitudes)
        {
            UnwrapLongitudesInPlace(xs);
        }

        var area = 0d;
        for (var i = 0; i < ring.Count; i++)
        {
            var j = (i + 1) % ring.Count;
            area += (xs[i] * ys[j]) - (xs[j] * ys[i]);
        }

        return area / 2d;
    }

    private static bool IsPointInRing(double x, double y, IReadOnlyList<(double X, double Y)> ring)
    {
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var xi = ring[i].X;
            var yi = ring[i].Y;
            var xj = ring[j].X;
            var yj = ring[j].Y;

            var intersects = ((yi > y) != (yj > y)) &&
                (x < ((xj - xi) * (y - yi) / (yj - yi)) + xi);
            if (intersects)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    internal static double ComputeGroundPixelAreaSquareMeters(
        double nativePixelArea,
        double centerY,
        int? srid,
        bool isGeographic,
        double linearMetersPerUnit)
    {
        if (srid is int wkid)
        {
            if (isGeographic)
            {
                var centerLatRad = DegreesToRadians(Math.Clamp(centerY, -89.999999d, 89.999999d));
                var metersPerDegree = MeanEarthRadiusMeters * Math.PI / 180d;
                return nativePixelArea * metersPerDegree * metersPerDegree * Math.Cos(centerLatRad);
            }

            if (SpatialReferenceExtensions.NormalizeWebMercatorSrid(wkid) == 3857)
            {
                var (_, centerLat) = WebMercatorMath.WebMercatorToLonLat(0d, centerY);
                var cosLat = Math.Cos(DegreesToRadians(centerLat));
                return nativePixelArea * cosLat * cosLat;
            }
        }

        var factor = linearMetersPerUnit > 0d && double.IsFinite(linearMetersPerUnit)
            ? linearMetersPerUnit
            : 1d;
        return nativePixelArea * factor * factor;
    }

    internal static double PlanarRingAreaSquareMeters(IReadOnlyList<(double X, double Y)> ring)
    {
        if (ring.Count < 3)
        {
            return 0d;
        }

        var area = 0d;
        for (var i = 0; i < ring.Count; i++)
        {
            var j = (i + 1) % ring.Count;
            area += ring[i].X * ring[j].Y;
            area -= ring[j].X * ring[i].Y;
        }

        return Math.Abs(area) / 2d;
    }

    internal static (double X, double Y) SignedAreaCentroid(
        IReadOnlyList<(double X, double Y)> ring,
        bool unwrapLongitudes)
    {
        var count = ring.Count > 1 && ring[0].X.Equals(ring[^1].X) && ring[0].Y.Equals(ring[^1].Y)
            ? ring.Count - 1
            : ring.Count;
        if (count < 3)
        {
            return VertexMean(ring, count);
        }

        var xs = new double[count];
        var ys = new double[count];
        for (var i = 0; i < count; i++)
        {
            xs[i] = ring[i].X;
            ys[i] = ring[i].Y;
        }

        if (unwrapLongitudes)
        {
            UnwrapLongitudesInPlace(xs);
        }

        var signedArea = 0d;
        var cx = 0d;
        var cy = 0d;
        for (var i = 0; i < count; i++)
        {
            var j = (i + 1) % count;
            var cross = (xs[i] * ys[j]) - (xs[j] * ys[i]);
            signedArea += cross;
            cx += (xs[i] + xs[j]) * cross;
            cy += (ys[i] + ys[j]) * cross;
        }

        signedArea /= 2d;
        if (Math.Abs(signedArea) < 1e-12)
        {
            return VertexMean(ring, count);
        }

        var centroidX = cx / (6d * signedArea);
        var centroidY = cy / (6d * signedArea);
        if (unwrapLongitudes)
        {
            centroidX = WrapLongitude(centroidX);
        }

        return (centroidX, centroidY);
    }

    private static (double X, double Y) VertexMean(IReadOnlyList<(double X, double Y)> ring, int count)
    {
        if (count <= 0)
        {
            count = ring.Count;
        }

        var x = 0d;
        var y = 0d;
        for (var i = 0; i < count; i++)
        {
            x += ring[i].X;
            y += ring[i].Y;
        }

        return (x / count, y / count);
    }

    private static void UnwrapLongitudesInPlace(double[] lons)
    {
        for (var i = 1; i < lons.Length; i++)
        {
            var delta = NormalizeLongitudeDelta(lons[i] - lons[i - 1]);
            lons[i] = lons[i - 1] + delta;
        }
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

    private static double NormalizeBearing(double degrees)
        => degrees < 0d ? degrees + 360d : degrees;

    private static double DegreesToRadians(double degrees)
        => degrees * Math.PI / 180d;

    private static double RadiansToDegrees(double radians)
        => radians * 180d / Math.PI;
}

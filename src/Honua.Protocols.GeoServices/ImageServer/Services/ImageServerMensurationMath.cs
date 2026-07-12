// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

/// <summary>
/// Pure ground-mensuration math for Basic ImageServer measure operations (#2734).
/// <para>
/// Basic mensuration must return <em>ground</em> quantities (meters, square meters, true
/// bearing), not raw map-unit deltas. Web Mercator (EPSG:3857) overstates ground distance
/// by <c>1/cos(latitude)</c>, and geographic map units are degrees, so planar
/// <c>sqrt(dx²+dy²)</c> on those coordinates is wrong by a factor that grows to ~10^5 for
/// degree inputs. These helpers normalize coordinates to lon/lat and measure geodesically,
/// falling back to honest planar meters only for projected coordinate systems whose
/// coordinates are already in meters.
/// </para>
/// </summary>
internal static class ImageServerMensurationMath
{
    /// <summary>
    /// IUGG mean Earth radius R1 = (2a + b) / 3 ≈ 6371008.8 m. The spherical
    /// haversine/bearing approximation uses the mean radius (rather than the Web Mercator
    /// sphere's semi-major axis 6378137 m) because it minimizes global RMS error across all
    /// latitudes for a single-radius sphere; the ~0.11% difference is far below the accuracy
    /// clients expect from Basic mensuration.
    /// </summary>
    internal const double MeanEarthRadiusMeters = 6371008.8;

    /// <summary>
    /// Coordinate space a normalized measurement is expressed in.
    /// </summary>
    internal enum MeasureSpace
    {
        /// <summary>Coordinates are geographic lon/lat degrees; use spherical geodesic math.</summary>
        Geodesic,

        /// <summary>Coordinates are projected easting/northing meters; use planar Euclidean math.</summary>
        PlanarMeters,
    }

    /// <summary>
    /// Determines whether the SRID denotes a geographic (lon/lat degree) coordinate system.
    /// </summary>
    /// <remarks>
    /// Uses the shared <see cref="SpatialConstants.GeographicSrids"/> list plus an EPSG
    /// geographic-2D range heuristic (4000–4999). Unifying this ad hoc classification with
    /// the shared SRID classifier is tracked by #2732; until then this local heuristic keeps
    /// the measure path self-contained.
    /// </remarks>
    internal static bool IsGeographicSrid(int srid)
        => Array.IndexOf(SpatialConstants.GeographicSrids, srid) >= 0
           || srid is >= 4000 and <= 4999;

    /// <summary>
    /// Attempts an in-process conversion of a projected/geographic coordinate to lon/lat
    /// degrees without any external transform service. Handles Web Mercator (and its aliases)
    /// via the exact inverse Mercator projection and treats geographic SRIDs as already lon/lat.
    /// Returns <see langword="false"/> for other projected SRIDs, where an authoritative
    /// transform service is required.
    /// </summary>
    internal static bool TryConvertToLonLat(double x, double y, int srid, out double lon, out double lat)
    {
        if (SpatialReferenceExtensions.NormalizeWebMercatorSrid(srid) == 3857)
        {
            (lon, lat) = WebMercatorMath.WebMercatorToLonLat(x, y);
            return true;
        }

        if (IsGeographicSrid(srid))
        {
            lon = x;
            lat = y;
            return true;
        }

        lon = 0d;
        lat = 0d;
        return false;
    }

    /// <summary>
    /// Great-circle (haversine) distance in meters between two lon/lat points.
    /// </summary>
    internal static double GeodesicDistanceMeters(double lon1, double lat1, double lon2, double lat2)
    {
        var phi1 = DegreesToRadians(lat1);
        var phi2 = DegreesToRadians(lat2);
        var dPhi = DegreesToRadians(lat2 - lat1);
        var dLambda = DegreesToRadians(lon2 - lon1);
        var a = (Math.Sin(dPhi / 2d) * Math.Sin(dPhi / 2d)) +
                (Math.Cos(phi1) * Math.Cos(phi2) * Math.Sin(dLambda / 2d) * Math.Sin(dLambda / 2d));
        return 2d * MeanEarthRadiusMeters * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1d - a));
    }

    /// <summary>
    /// Planar Euclidean distance between two projected-meter points.
    /// </summary>
    internal static double PlanarDistanceMeters(double x1, double y1, double x2, double y2)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>
    /// True initial bearing (forward azimuth) in degrees clockwise from north, computed on
    /// the sphere from lon/lat. Consistent with the geodesic distance model.
    /// </summary>
    internal static double InitialBearingDegrees(double lon1, double lat1, double lon2, double lat2)
    {
        var phi1 = DegreesToRadians(lat1);
        var phi2 = DegreesToRadians(lat2);
        var dLambda = DegreesToRadians(lon2 - lon1);
        var yComponent = Math.Sin(dLambda) * Math.Cos(phi2);
        var xComponent = (Math.Cos(phi1) * Math.Sin(phi2)) -
                         (Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLambda));
        var degrees = RadiansToDegrees(Math.Atan2(yComponent, xComponent));
        return NormalizeBearing(degrees);
    }

    /// <summary>
    /// Grid bearing in degrees clockwise from the projected +Y axis for projected-meter
    /// coordinates (used only where no in-process lon/lat conversion is available).
    /// </summary>
    internal static double PlanarBearingDegrees(double x1, double y1, double x2, double y2)
    {
        var degrees = RadiansToDegrees(Math.Atan2(x2 - x1, y2 - y1));
        return NormalizeBearing(degrees);
    }

    /// <summary>
    /// Ground area in square meters of a geographic (lon/lat) ring using an equirectangular
    /// projection about the ring's mean latitude. Longitudes are unwrapped first so rings that
    /// cross the antimeridian project to a continuous polygon rather than wrapping through 360°.
    /// </summary>
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
        var lat0 = 0d;
        for (var i = 0; i < lats.Length; i++)
        {
            lat0 += lats[i];
        }

        lat0 /= lats.Length;
        var cosLat0 = Math.Cos(DegreesToRadians(lat0));

        var projected = new (double X, double Y)[ring.Count];
        for (var i = 0; i < ring.Count; i++)
        {
            projected[i] = (
                MeanEarthRadiusMeters * DegreesToRadians(lons[i]) * cosLat0,
                MeanEarthRadiusMeters * DegreesToRadians(lats[i]));
        }

        return PlanarRingAreaSquareMeters(projected);
    }

    /// <summary>
    /// Planar (shoelace) area in square meters of a projected-meter ring. The ring may be open
    /// or closed; the closing segment is handled by wrap-around indexing.
    /// </summary>
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

    /// <summary>
    /// Signed-area (shoelace) centroid of a ring expressed in its own coordinate space. When
    /// <paramref name="unwrapLongitudes"/> is set the X ordinates are treated as longitudes and
    /// unwrapped around the antimeridian before the computation, then the resulting X is wrapped
    /// back into [-180, 180). Falls back to the vertex mean for degenerate (near-zero-area) rings.
    /// </summary>
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

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
    /// IUGG mean Earth radius R1 = (2a + b) / 3 ≈ 6371008.8 m. Distance and bearing
    /// use Vincenty on the WGS 84 ellipsoid. This radius remains only for the
    /// near-antipodal haversine fallback inside <see cref="Wgs84Vincenty"/>.
    /// </summary>
    internal const double MeanEarthRadiusMeters = 6371008.8;

    /// <summary>
    /// Coordinate space a normalized measurement is expressed in.
    /// </summary>
    internal enum MeasureSpace
    {
        /// <summary>Coordinates are geographic lon/lat degrees; measure on the WGS 84 ellipsoid.</summary>
        Geodesic,

        /// <summary>Coordinates are projected easting/northing meters; use planar Euclidean math.</summary>
        PlanarMeters,
    }

    /// <summary>
    /// Determines whether the SRID denotes a geographic (lon/lat degree) coordinate system.
    /// </summary>
    /// <remarks>
    /// Delegates to the canonical <see cref="GeographicSridClassifier"/> (#2732), using the
    /// broad-list-plus-range variant so the offline mensuration path keeps its pre-#2732 behaviour
    /// of treating any unlisted EPSG 4000–4999 geographic-block code (e.g. EPSG:4301/4314/4322) as
    /// lat/lon degrees. The canonical variant excludes the well-known geocentric codes
    /// (e.g. EPSG:4978) that the old raw range heuristic mis-classified; measuring those planar in
    /// metres is correct, whereas treating a degree CRS as projected would compute nonsense.
    /// </remarks>
    internal static bool IsGeographicSrid(int srid)
        => GeographicSridClassifier.IsGeographicOrUnlistedGeographicRangeSrid(srid);

    /// <summary>
    /// Attempts an in-process conversion of a projected/geographic coordinate to lon/lat
    /// degrees without any external transform service. Handles Web Mercator (and its aliases)
    /// via the exact inverse Mercator projection and treats geographic SRIDs as already lon/lat.
    /// Returns <see langword="false"/> for other projected SRIDs, where an authoritative
    /// transform service is required.
    /// </summary>
    internal static bool TryConvertToLonLat(double x, double y, int srid, out double lon, out double lat)
        => TryConvertToLonLat(x, y, srid, IsGeographicSrid(srid), out lon, out lat);

    /// <summary>
    /// Overload that accepts a precomputed geographic classification (#2794). Handlers that can
    /// reach dependency injection resolve <paramref name="sridIsGeographic"/> once per request
    /// through the registry-backed <c>IGeographicSridClassifier</c> (which classifies arbitrary
    /// EPSG codes from live <c>spatial_ref_sys</c> WKT) and pass it here, keeping this math pure and
    /// synchronous while superseding the static range heuristic in <see cref="IsGeographicSrid"/>.
    /// </summary>
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

    /// <summary>
    /// WGS 84 ellipsoidal distance in meters between two lon/lat points.
    /// </summary>
    internal static double GeodesicDistanceMeters(double lon1, double lat1, double lon2, double lat2)
        => Wgs84Vincenty.DistanceMeters(lon1, lat1, lon2, lat2);

    /// <summary>
    /// Shadow-based object height in meters: <c>height = shadowLength · tan(sunElevation)</c>.
    /// <para>
    /// Classic photogrammetric shadow mensuration. An object of height <c>h</c> standing on flat
    /// ground casts a shadow of horizontal length <c>L</c> under a sun elevation angle <c>θ</c>
    /// (degrees above the horizon): <c>tan(θ) = h / L</c>, hence <c>h = L · tan(θ)</c>. The caller
    /// supplies the measured ground shadow length (from the existing planar/geodesic distance path)
    /// and the sun elevation read from the raster's exterior-orientation metadata
    /// (<see cref="ImageServerSensorModel.TryReadSunGeometry"/>). Backs the
    /// <c>esriMensurationHeightFromBaseAndTopShadow</c> / <c>*HeightFromTopAndTopShadow</c>
    /// operations (ADR-0065).
    /// </para>
    /// </summary>
    /// <param name="shadowLengthMeters">Measured ground shadow length in meters (must be finite, ≥ 0).</param>
    /// <param name="sunElevationDegrees">
    /// Sun elevation angle above the horizon in degrees; the caller guarantees it is in the open
    /// interval (0, 90) so the tangent is finite and positive.
    /// </param>
    /// <returns>The object height in meters.</returns>
    internal static double ShadowHeightMeters(double shadowLengthMeters, double sunElevationDegrees)
        => shadowLengthMeters * Math.Tan(DegreesToRadians(sunElevationDegrees));

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
    /// Initial bearing (forward azimuth) in degrees clockwise from north on the WGS 84 ellipsoid.
    /// </summary>
    internal static double InitialBearingDegrees(double lon1, double lat1, double lon2, double lat2)
        => Wgs84Vincenty.InitialBearingDegrees(lon1, lat1, lon2, lat2);

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
    /// Ground area in square meters of a geographic ring. Edges are densified along the
    /// WGS 84 geodesic at about 10 km, then measured in an azimuthal equidistant plane
    /// centered on the vertex mean. Longitudes are unwrapped first so an antimeridian
    /// ring stays one polygon.
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
                densified.Add((distance * Math.Sin(azimuth), distance * Math.Cos(azimuth)));
            }
        }

        return PlanarRingAreaSquareMeters(densified);
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

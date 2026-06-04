// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;

namespace Honua.Routing.Features.Routing.Providers;

/// <summary>
/// Deterministic, database-free routing provider used by unit tests and when
/// <c>Routing:Provider=mock</c>. Routes are straight lines between the first and
/// last stop; service areas are coarse buffered circles per break. Geometry is
/// emitted as valid GeoJSON so callers can exercise the full pipeline without a
/// pgRouting topology.
/// </summary>
internal sealed class MockRoutingProvider : IRoutingProvider
{
    /// <summary>
    /// Mock provider name constant.
    /// </summary>
    public const string ProviderName = "mock";

    // Approximate meters per degree of latitude; used for coarse length and
    // buffer-radius estimates. Good enough for deterministic test fixtures.
    private const double MetersPerDegree = 111_320.0;

    // Assumed average speed (meters per minute) to derive a travel time from the
    // straight-line length: ~50 km/h.
    private const double MetersPerMinute = 50_000.0 / 60.0;

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public Task<RouteSolveResult> SolveRouteAsync(
        RouteSolveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Stops.Count < 2)
        {
            return Task.FromResult(new RouteSolveResult(string.Empty, 0, 0, []));
        }

        var start = request.Stops[0];
        var end = request.Stops[^1];

        var lengthMeters = HaversineMeters(start, end);
        var timeMinutes = lengthMeters / MetersPerMinute;

        var geometry = LineStringGeoJson([start, end]);

        var directions = new List<RouteDirectionStep>
        {
            new("Depart", 0, 0, "depart"),
            new("Travel to destination", lengthMeters, timeMinutes, "straight"),
            new("Arrive at destination", 0, 0, "arrive"),
        };

        return Task.FromResult(new RouteSolveResult(geometry, lengthMeters, timeMinutes, directions));
    }

    /// <inheritdoc />
    public Task<ServiceAreaSolveResult> SolveServiceAreaAsync(
        ServiceAreaSolveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var polygons = new List<ServiceAreaPolygon>();

        // Ascending, de-duplicated breaks so concentric rings nest correctly.
        var orderedBreaks = request.Breaks
            .Where(b => b > 0)
            .Distinct()
            .OrderBy(b => b)
            .ToArray();

        for (var facilityId = 0; facilityId < request.Facilities.Count; facilityId++)
        {
            var facility = request.Facilities[facilityId];
            var fromBreak = 0.0;

            foreach (var toBreak in orderedBreaks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Treat the break as minutes and convert to a buffer radius in
                // degrees via the assumed average speed.
                var radiusMeters = toBreak * MetersPerMinute;
                var radiusDegrees = radiusMeters / MetersPerDegree;

                var geometry = CirclePolygonGeoJson(facility, radiusDegrees);
                polygons.Add(new ServiceAreaPolygon(facilityId, fromBreak, toBreak, geometry));

                fromBreak = toBreak;
            }
        }

        return Task.FromResult(new ServiceAreaSolveResult(polygons));
    }

    private static double HaversineMeters(RoutePoint a, RoutePoint b)
    {
        const double earthRadiusMeters = 6_371_000.0;
        var lat1 = DegreesToRadians(a.Lat);
        var lat2 = DegreesToRadians(b.Lat);
        var dLat = DegreesToRadians(b.Lat - a.Lat);
        var dLon = DegreesToRadians(b.Lon - a.Lon);

        var h = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2)) +
                (Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2));
        return earthRadiusMeters * 2 * Math.Asin(Math.Min(1.0, Math.Sqrt(h)));
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static string LineStringGeoJson(IReadOnlyList<RoutePoint> points)
    {
        var builder = new StringBuilder();
        builder.Append("{\"type\":\"LineString\",\"coordinates\":[");
        AppendCoordinateList(builder, points);
        builder.Append("]}");
        return builder.ToString();
    }

    private static string CirclePolygonGeoJson(RoutePoint center, double radiusDegrees)
    {
        const int segments = 36;
        var ring = new List<RoutePoint>(segments + 1);

        // Latitude scaling so the circle stays roughly round away from the equator.
        var lonScale = Math.Cos(DegreesToRadians(center.Lat));
        if (Math.Abs(lonScale) < 1e-6)
        {
            lonScale = 1e-6;
        }

        for (var i = 0; i <= segments; i++)
        {
            var angle = 2 * Math.PI * i / segments;
            var lon = center.Lon + (radiusDegrees * Math.Cos(angle) / lonScale);
            var lat = center.Lat + (radiusDegrees * Math.Sin(angle));
            ring.Add(new RoutePoint(lon, lat));
        }

        var builder = new StringBuilder();
        builder.Append("{\"type\":\"Polygon\",\"coordinates\":[[");
        AppendCoordinateList(builder, ring);
        builder.Append("]]}");
        return builder.ToString();
    }

    private static void AppendCoordinateList(StringBuilder builder, IReadOnlyList<RoutePoint> points)
    {
        for (var i = 0; i < points.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            var p = points[i];
            builder.Append('[')
                .Append(p.Lon.ToString("R", CultureInfo.InvariantCulture))
                .Append(',')
                .Append(p.Lat.ToString("R", CultureInfo.InvariantCulture))
                .Append(']');
        }
    }
}

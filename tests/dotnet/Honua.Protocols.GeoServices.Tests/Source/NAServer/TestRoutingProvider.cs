// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.NAServer;

/// <summary>
/// Deterministic in-process <see cref="IRoutingProvider"/> used by the NAServer
/// integration tests. Mirrors the production MockRoutingProvider behavior
/// (straight-line routes, buffered-circle service areas) without depending on the
/// internal mock type, so the GeoServices.Tests project can exercise the full
/// adapter pipeline without a pgRouting topology.
/// </summary>
internal sealed class TestRoutingProvider : IRoutingProvider
{
    private const double MetersPerDegree = 111_320.0;
    private const double MetersPerMinute = 50_000.0 / 60.0;

    public string Name => "test";

    public Task<RouteSolveResult> SolveRouteAsync(
        RouteSolveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Stops.Count < 2)
        {
            return Task.FromResult(new RouteSolveResult(string.Empty, 0, 0, []));
        }

        var start = request.Stops[0];
        var end = request.Stops[^1];
        var length = HaversineMeters(start, end);
        var time = length / MetersPerMinute;
        var geometry = LineStringGeoJson(start, end);

        var directions = new List<RouteDirectionStep>
        {
            new("Depart", 0, 0, "depart"),
            new("Travel to destination", length, time, "straight"),
            new("Arrive at destination", 0, 0, "arrive"),
        };

        return Task.FromResult(new RouteSolveResult(geometry, length, time, directions));
    }

    public Task<ServiceAreaSolveResult> SolveServiceAreaAsync(
        ServiceAreaSolveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var polygons = new List<ServiceAreaPolygon>();
        var orderedBreaks = request.Breaks.Where(b => b > 0).Distinct().OrderBy(b => b).ToArray();

        for (var facilityId = 0; facilityId < request.Facilities.Count; facilityId++)
        {
            var facility = request.Facilities[facilityId];
            var fromBreak = 0.0;
            foreach (var toBreak in orderedBreaks)
            {
                var radiusDegrees = toBreak * MetersPerMinute / MetersPerDegree;
                polygons.Add(new ServiceAreaPolygon(
                    facilityId, fromBreak, toBreak, CirclePolygonGeoJson(facility, radiusDegrees)));
                fromBreak = toBreak;
            }
        }

        return Task.FromResult(new ServiceAreaSolveResult(polygons));
    }

    private static double HaversineMeters(RoutePoint a, RoutePoint b)
    {
        const double earthRadiusMeters = 6_371_000.0;
        var lat1 = a.Lat * Math.PI / 180.0;
        var lat2 = b.Lat * Math.PI / 180.0;
        var dLat = (b.Lat - a.Lat) * Math.PI / 180.0;
        var dLon = (b.Lon - a.Lon) * Math.PI / 180.0;
        var h = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2)) +
                (Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2));
        return earthRadiusMeters * 2 * Math.Asin(Math.Min(1.0, Math.Sqrt(h)));
    }

    private static string LineStringGeoJson(RoutePoint a, RoutePoint b)
    {
        var builder = new StringBuilder();
        builder.Append("{\"type\":\"LineString\",\"coordinates\":[");
        AppendCoordinate(builder, a);
        builder.Append(',');
        AppendCoordinate(builder, b);
        builder.Append("]}");
        return builder.ToString();
    }

    private static string CirclePolygonGeoJson(RoutePoint center, double radiusDegrees)
    {
        const int segments = 36;
        var lonScale = Math.Cos(center.Lat * Math.PI / 180.0);
        if (Math.Abs(lonScale) < 1e-6)
        {
            lonScale = 1e-6;
        }

        var builder = new StringBuilder();
        builder.Append("{\"type\":\"Polygon\",\"coordinates\":[[");
        for (var i = 0; i <= segments; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            var angle = 2 * Math.PI * i / segments;
            var lon = center.Lon + (radiusDegrees * Math.Cos(angle) / lonScale);
            var lat = center.Lat + (radiusDegrees * Math.Sin(angle));
            AppendCoordinate(builder, new RoutePoint(lon, lat));
        }

        builder.Append("]]}");
        return builder.ToString();
    }

    private static void AppendCoordinate(StringBuilder builder, RoutePoint point)
        => builder.Append('[')
            .Append(point.Lon.ToString("R", CultureInfo.InvariantCulture))
            .Append(',')
            .Append(point.Lat.ToString("R", CultureInfo.InvariantCulture))
            .Append(']');
}

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
    public RoutingProviderCapabilities Capabilities { get; } = new(
        SupportsRoute: true,
        SupportsServiceArea: true,
        SupportsClosestFacility: true,
        SupportsOdCostMatrix: true,
        SupportsLocationAllocation: true)
    {
        // The mock buffers a symmetric circle, so travel direction does not change
        // the geometry; only FromFacility is meaningfully distinct.
        SupportedTravelDirections = [ServiceAreaTravelDirection.FromFacility],

        // The mock routes a straight line / symmetric buffer, so it cannot honour
        // barriers geometrically — advertise no barrier kinds (honest). It DOES
        // accept multiple named travel modes (it ignores the impedance difference,
        // but the request surface and validation are exercised), so the NAServer
        // travel-mode validation path can be driven without a pgRouting topology.
        SupportedTravelModes = ["driving", "walking", "trucking"],

        SupportedLocationAllocationProblemTypes =
        [
            LocationAllocationProblemType.MinimizeImpedance,
            LocationAllocationProblemType.MaximizeCoverage,
            LocationAllocationProblemType.MinimizeFacilities,
        ],
    };

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

    /// <inheritdoc />
    public Task<ClosestFacilitySolveResult> SolveClosestFacilityAsync(
        ClosestFacilitySolveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var targetCount = Math.Max(1, request.DefaultTargetFacilityCount);
        var routes = new List<ClosestFacilityRoute>();

        for (var incidentId = 0; incidentId < request.Incidents.Count; incidentId++)
        {
            var incident = request.Incidents[incidentId];
            var ranked = request.Facilities
                .Select((facility, facilityId) =>
                {
                    var meters = HaversineMeters(incident, facility);
                    return (FacilityId: facilityId, Facility: facility, Meters: meters, Minutes: meters / MetersPerMinute);
                })
                .Where(x => request.Cutoff is not { } cutoff || x.Minutes <= cutoff)
                .OrderBy(x => x.Meters)
                .Take(targetCount)
                .ToList();

            var rank = 1;
            foreach (var entry in ranked)
            {
                var geometry = LineStringGeoJson([incident, entry.Facility]);
                routes.Add(new ClosestFacilityRoute(
                    incidentId,
                    entry.FacilityId,
                    rank,
                    geometry,
                    entry.Meters,
                    entry.Minutes,
                    [new RouteDirectionStep($"Incident {incidentId} - Facility {entry.FacilityId}", entry.Meters, entry.Minutes, "straight")]));
                rank++;
            }
        }

        return Task.FromResult(new ClosestFacilitySolveResult(routes));
    }

    /// <inheritdoc />
    public Task<OdCostMatrixSolveResult> SolveOdCostMatrixAsync(
        OdCostMatrixSolveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var lines = new List<OdLine>();
        for (var originId = 0; originId < request.Origins.Count; originId++)
        {
            var origin = request.Origins[originId];
            var perOrigin = new List<(int DestinationId, double Meters, double Minutes)>();
            for (var destId = 0; destId < request.Destinations.Count; destId++)
            {
                var meters = HaversineMeters(origin, request.Destinations[destId]);
                var minutes = meters / MetersPerMinute;
                if (request.Cutoff is { } cutoff && minutes > cutoff)
                {
                    continue;
                }

                perOrigin.Add((destId, meters, minutes));
            }

            var ranked = perOrigin.OrderBy(x => x.Minutes).AsEnumerable();
            if (request.DestinationCount is { } k && k > 0)
            {
                ranked = ranked.Take(k);
            }

            var rank = 1;
            foreach (var entry in ranked)
            {
                var geometry = request.OutputType == OdLineOutputType.StraightLines
                    ? LineStringGeoJson([origin, request.Destinations[entry.DestinationId]])
                    : null;
                lines.Add(new OdLine(
                    originId,
                    entry.DestinationId,
                    rank,
                    entry.Minutes,
                    entry.Meters,
                    geometry));
                rank++;
            }
        }

        return Task.FromResult(new OdCostMatrixSolveResult(lines));
    }

    /// <inheritdoc />
    public Task<LocationAllocationSolveResult> SolveLocationAllocationAsync(
        LocationAllocationSolveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // Build a haversine impedance matrix (minutes) and reuse the shared solver.
        var matrix = new double[request.Facilities.Count][];
        for (var f = 0; f < request.Facilities.Count; f++)
        {
            matrix[f] = new double[request.DemandPoints.Count];
            for (var d = 0; d < request.DemandPoints.Count; d++)
            {
                matrix[f][d] = HaversineMeters(request.Facilities[f], request.DemandPoints[d].Location) / MetersPerMinute;
            }
        }

        return Task.FromResult(LocationAllocationSolver.Solve(request, matrix, cancellationToken));
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

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

    /// <summary>
    /// Creates a provider advertising full capabilities (route + service area, both
    /// travel directions, all barrier kinds, and driving/walking travel modes).
    /// Used by the happy-path NAServer integration tests. The mock geometry ignores
    /// barriers and the mode impedance, but advertising the capability lets the
    /// adapter's accept/validate/thread surface be exercised end-to-end.
    /// </summary>
    public TestRoutingProvider()
        : this(new RoutingProviderCapabilities(
            SupportsRoute: true,
            SupportsServiceArea: true,
            SupportsClosestFacility: true,
            SupportsOdCostMatrix: true,
            SupportsLocationAllocation: true)
        {
            SupportedBarrierKinds =
            [
                RouteBarrierKind.Point,
                RouteBarrierKind.Line,
                RouteBarrierKind.Polygon,
            ],
            SupportedTravelModes = ["driving", "walking"],
            SupportedLocationAllocationProblemTypes =
            [
                LocationAllocationProblemType.MinimizeImpedance,
                LocationAllocationProblemType.MaximizeCoverage,
            ],
        })
    {
    }

    /// <summary>
    /// Creates a provider with the supplied capabilities so the capability-gate tests
    /// can exercise restricted-provider behavior (e.g. service area disabled, or a
    /// travel direction not advertised).
    /// </summary>
    /// <param name="capabilities">Capabilities the provider should advertise.</param>
    public TestRoutingProvider(RoutingProviderCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        Capabilities = capabilities;
    }

    public string Name => "test";

    public RoutingProviderCapabilities Capabilities { get; }

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

    public Task<ClosestFacilitySolveResult> SolveClosestFacilityAsync(
        ClosestFacilitySolveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var targetCount = Math.Max(1, request.DefaultTargetFacilityCount);
        var routes = new List<ClosestFacilityRoute>();
        for (var incidentId = 0; incidentId < request.Incidents.Count; incidentId++)
        {
            var incident = request.Incidents[incidentId];
            var ranked = request.Facilities
                .Select((f, fid) => (FacilityId: fid, Facility: f, Meters: HaversineMeters(incident, f)))
                .Where(x => request.Cutoff is not { } cutoff || x.Meters / MetersPerMinute <= cutoff)
                .OrderBy(x => x.Meters)
                .Take(targetCount)
                .ToList();

            var rank = 1;
            foreach (var entry in ranked)
            {
                var minutes = entry.Meters / MetersPerMinute;
                routes.Add(new ClosestFacilityRoute(
                    incidentId,
                    entry.FacilityId,
                    rank,
                    LineStringGeoJson(incident, entry.Facility),
                    entry.Meters,
                    minutes,
                    [new RouteDirectionStep($"Incident {incidentId} - Facility {entry.FacilityId}", entry.Meters, minutes, "straight")]));
                rank++;
            }
        }

        return Task.FromResult(new ClosestFacilitySolveResult(routes));
    }

    public Task<OdCostMatrixSolveResult> SolveOdCostMatrixAsync(
        OdCostMatrixSolveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

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
                lines.Add(new OdLine(originId, entry.DestinationId, rank, entry.Minutes, entry.Meters));
                rank++;
            }
        }

        return Task.FromResult(new OdCostMatrixSolveResult(lines));
    }

    public Task<LocationAllocationSolveResult> SolveLocationAllocationAsync(
        LocationAllocationSolveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Greedy nearest-facility allocation: choose the requested number of
        // facilities minimizing demand-weighted haversine impedance.
        var chosen = new List<int>();
        var demandBest = new double[request.DemandPoints.Count];
        Array.Fill(demandBest, double.PositiveInfinity);
        var toFind = Math.Clamp(request.FacilitiesToFind, 1, Math.Max(1, request.Facilities.Count));

        for (var pick = 0; pick < toFind; pick++)
        {
            var bestFacility = -1;
            var bestGain = double.NegativeInfinity;
            for (var f = 0; f < request.Facilities.Count; f++)
            {
                if (chosen.Contains(f))
                {
                    continue;
                }

                var gain = 0.0;
                for (var d = 0; d < request.DemandPoints.Count; d++)
                {
                    var cost = HaversineMeters(request.Facilities[f], request.DemandPoints[d].Location) / MetersPerMinute;
                    if (request.ImpedanceCutoff is { } cut && cost > cut)
                    {
                        continue;
                    }

                    if (cost < demandBest[d])
                    {
                        gain += (double.IsInfinity(demandBest[d]) ? 1e6 : demandBest[d] - cost) * request.DemandPoints[d].Weight;
                    }
                }

                if (gain > bestGain)
                {
                    bestGain = gain;
                    bestFacility = f;
                }
            }

            if (bestFacility < 0)
            {
                break;
            }

            chosen.Add(bestFacility);
            for (var d = 0; d < request.DemandPoints.Count; d++)
            {
                var cost = HaversineMeters(request.Facilities[bestFacility], request.DemandPoints[d].Location) / MetersPerMinute;
                if (request.ImpedanceCutoff is { } cut && cost > cut)
                {
                    continue;
                }

                if (cost < demandBest[d])
                {
                    demandBest[d] = cost;
                }
            }
        }

        chosen.Sort();
        var allocations = new List<DemandAllocation>();
        double totalImpedance = 0;
        double totalCovered = 0;
        for (var d = 0; d < request.DemandPoints.Count; d++)
        {
            var bestF = -1;
            var bestCost = double.PositiveInfinity;
            foreach (var f in chosen)
            {
                var cost = HaversineMeters(request.Facilities[f], request.DemandPoints[d].Location) / MetersPerMinute;
                if (request.ImpedanceCutoff is { } cut && cost > cut)
                {
                    continue;
                }

                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestF = f;
                }
            }

            var weight = request.DemandPoints[d].Weight;
            if (bestF >= 0)
            {
                allocations.Add(new DemandAllocation(d, bestF, weight, bestCost));
                totalImpedance += bestCost * weight;
                totalCovered += weight;
            }
            else
            {
                allocations.Add(new DemandAllocation(d, -1, weight, double.PositiveInfinity));
            }
        }

        return Task.FromResult(new LocationAllocationSolveResult(chosen, allocations, totalImpedance, totalCovered));
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

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Routing.Features.Routing.Domain;

/// <summary>
/// A single geographic point expressed as longitude/latitude in the request's
/// spatial reference (default WGS84 / EPSG:4326). Protocol-neutral: this is NOT
/// an Esri DTO; protocol adapters (e.g. NAServer) map their wire formats onto it.
/// </summary>
/// <param name="Lon">Longitude (X) ordinate.</param>
/// <param name="Lat">Latitude (Y) ordinate.</param>
public readonly record struct RoutePoint(double Lon, double Lat);

/// <summary>
/// Direction of travel relative to a service-area facility.
/// </summary>
public enum ServiceAreaTravelDirection
{
    /// <summary>
    /// Cost is accumulated travelling away from the facility (outbound coverage).
    /// </summary>
    FromFacility = 0,

    /// <summary>
    /// Cost is accumulated travelling towards the facility (inbound coverage).
    /// </summary>
    ToFacility = 1,
}

/// <summary>
/// Protocol-neutral request to solve a point-to-point (multi-stop) route.
/// </summary>
/// <param name="Stops">
/// Ordered stops to route through, in visit order. At least two stops are
/// required to produce a route; the provider routes consecutive pairs and
/// concatenates the legs.
/// </param>
/// <param name="TravelProfile">
/// Optional travel profile / cost model selector (e.g. "driving", "walking").
/// MVP providers treat this as advisory and route on the topology's stored
/// <c>cost</c>/<c>reverse_cost</c> weights regardless of profile.
/// </param>
/// <param name="OutSrid">
/// Output spatial reference (SRID/WKID) for returned geometry. Defaults to 4326.
/// Input <see cref="RoutePoint"/> ordinates are interpreted in this SRID as well.
/// </param>
public sealed record RouteSolveRequest(
    IReadOnlyList<RoutePoint> Stops,
    string TravelProfile = "driving",
    int OutSrid = 4326)
{
    /// <summary>
    /// Point/line barriers to avoid. MVP-stubbed: accepted but ignored by the
    /// current providers. Reserved so the NAServer adapter can pass barriers
    /// through once barrier support lands.
    /// </summary>
    public IReadOnlyList<RoutePoint> Barriers { get; init; } = [];

    /// <summary>
    /// Esri-style travel-mode identifier. MVP-stubbed: accepted but ignored.
    /// </summary>
    public string? TravelMode { get; init; }
}

/// <summary>
/// A single turn-by-turn direction step in a solved route.
/// </summary>
/// <param name="Text">Human-readable maneuver text.</param>
/// <param name="Length">Step length in meters.</param>
/// <param name="Time">Step travel time in minutes.</param>
/// <param name="ManeuverType">
/// Maneuver classifier (e.g. "depart", "straight", "arrive"). MVP providers may
/// emit a coarse value.
/// </param>
public sealed record RouteDirectionStep(
    string Text,
    double Length,
    double Time,
    string ManeuverType);

/// <summary>
/// Result of solving a route.
/// </summary>
/// <param name="RouteGeometryGeoJson">
/// The merged route geometry as a GeoJSON <c>LineString</c> string in the request's
/// <see cref="RouteSolveRequest.OutSrid"/>. GeoJSON (rather than a NetTopologySuite
/// geometry) is used so the model stays serialization-neutral and the adapter can
/// embed it directly. Empty string when no route was found.
/// </param>
/// <param name="TotalLengthMeters">Total route length in meters.</param>
/// <param name="TotalTimeMinutes">Total travel time in minutes.</param>
/// <param name="Directions">
/// Ordered turn-by-turn steps. MVP providers may return a single summary step or
/// an empty list when directions are not computed.
/// </param>
public sealed record RouteSolveResult(
    string RouteGeometryGeoJson,
    double TotalLengthMeters,
    double TotalTimeMinutes,
    IReadOnlyList<RouteDirectionStep> Directions)
{
    /// <summary>
    /// Whether a route was successfully found between the stops.
    /// </summary>
    public bool Solved => !string.IsNullOrEmpty(RouteGeometryGeoJson);
}

/// <summary>
/// Protocol-neutral request to solve service areas (isochrones) around facilities.
/// </summary>
/// <param name="Facilities">Facility points to generate service areas around.</param>
/// <param name="Breaks">
/// Cost cutoffs defining concentric service-area rings, in ascending order. Units
/// match the topology cost weights interpreted as minutes for the MVP (e.g.
/// <c>[5, 10, 15]</c> for 5/10/15-minute drive-time areas).
/// </param>
/// <param name="TravelDirection">Direction of travel relative to each facility.</param>
/// <param name="OutSrid">Output spatial reference (SRID/WKID). Defaults to 4326.</param>
public sealed record ServiceAreaSolveRequest(
    IReadOnlyList<RoutePoint> Facilities,
    IReadOnlyList<double> Breaks,
    ServiceAreaTravelDirection TravelDirection = ServiceAreaTravelDirection.FromFacility,
    int OutSrid = 4326);

/// <summary>
/// A single service-area polygon ring for one facility and one break interval.
/// </summary>
/// <param name="FacilityId">Zero-based index of the facility this polygon belongs to.</param>
/// <param name="FromBreak">
/// Inner cost cutoff (minutes) of this ring; 0 for the innermost ring.
/// </param>
/// <param name="ToBreak">Outer cost cutoff (minutes) of this ring.</param>
/// <param name="GeometryGeoJson">
/// The service-area polygon as a GeoJSON <c>Polygon</c>/<c>MultiPolygon</c> string
/// in the request SRID. Empty string when no reachable area was produced.
/// </param>
public sealed record ServiceAreaPolygon(
    int FacilityId,
    double FromBreak,
    double ToBreak,
    string GeometryGeoJson);

/// <summary>
/// Result of solving service areas.
/// </summary>
/// <param name="Polygons">
/// Service-area polygons, one per (facility, break) combination, ordered by
/// facility then ascending break.
/// </param>
public sealed record ServiceAreaSolveResult(
    IReadOnlyList<ServiceAreaPolygon> Polygons);

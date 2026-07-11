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
/// Classifies a routing barrier by the Esri geometry family it was supplied as.
/// A barrier restricts traversal: edges that interact with the barrier geometry
/// are excluded from the routing graph for the solve.
/// </summary>
public enum RouteBarrierKind
{
    /// <summary>
    /// A point barrier (Esri <c>barriers</c>). Restricts the single graph edge
    /// nearest the point — a blocked intersection / incident location.
    /// </summary>
    Point = 0,

    /// <summary>
    /// A polyline barrier (Esri <c>polylineBarriers</c>). Restricts every edge
    /// the barrier line crosses — a road closure drawn along/across streets.
    /// </summary>
    Line = 1,

    /// <summary>
    /// A polygon barrier (Esri <c>polygonBarriers</c>). Restricts every edge that
    /// intersects the barrier area — a flood zone / event-closure region.
    /// </summary>
    Polygon = 2,
}

/// <summary>
/// A single routing barrier expressed as a GeoJSON geometry in the request's input
/// spatial reference. Protocol-neutral: the NAServer adapter maps Esri barrier
/// inputs onto this model and the provider threads it into the solve by excluding
/// the graph edges the barrier restricts. The geometry is transformed from
/// <see cref="RouteSolveRequest.InSrid"/> to the graph SRID by the provider before
/// the edge-exclusion predicate runs.
/// </summary>
/// <param name="Kind">The barrier geometry family (point/line/polygon).</param>
/// <param name="GeometryGeoJson">
/// The barrier geometry as a GeoJSON string (<c>Point</c>/<c>MultiPoint</c>,
/// <c>LineString</c>/<c>MultiLineString</c>, or <c>Polygon</c>/<c>MultiPolygon</c>)
/// in the request's input SRID.
/// </param>
public sealed record RouteBarrier(RouteBarrierKind Kind, string GeometryGeoJson);

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
/// Dataset-backed providers resolve it to validated forward/reverse impedance
/// columns. <c>driving</c> remains the backward-compatible default.
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
    private readonly int? _inSrid;

    /// <summary>
    /// Spatial reference (SRID/WKID) the input <see cref="RoutePoint"/> ordinates
    /// are expressed in. Defaults to <see cref="OutSrid"/> so callers that send a
    /// single <c>outSR</c> (the common case) have their stops interpreted in that
    /// same SRID. Set explicitly (e.g. from <c>inSR</c>) when input and output
    /// references differ. Used to transform stops to the graph SRID before snapping.
    /// </summary>
    public int InSrid
    {
        get => _inSrid ?? OutSrid;
        init => _inSrid = value;
    }

    /// <summary>
    /// Point/line/polygon barriers to honour. The provider excludes the graph
    /// edges each barrier restricts before solving. Empty when no barriers are
    /// supplied. Providers that do not advertise a barrier kind in
    /// <see cref="RoutingProviderCapabilities.SupportedBarrierKinds"/> have the
    /// request rejected by the adapter before solving, so a provider only ever
    /// receives barrier kinds it supports.
    /// </summary>
    public IReadOnlyList<RouteBarrier> Barriers { get; init; } = [];

    /// <summary>
    /// Esri-style travel-mode identifier (e.g. <c>driving</c>, <c>walking</c>).
    /// <c>null</c> selects the provider default. The adapter validates the value
    /// against <see cref="RoutingProviderCapabilities.SupportedTravelModes"/> and
    /// rejects unsupported modes, so a provider only ever receives a mode it
    /// advertises (or <c>null</c>).
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
/// Travel-time cutoffs in <em>minutes</em> defining concentric service-area rings,
/// in ascending order (e.g. <c>[5, 10, 15]</c> for 5/10/15-minute drive-time areas).
/// The provider converts each break into the topology's raw cost unit (declared by
/// <c>RoutingConfiguration.CostUnit</c>) before passing it to pgRouting as the
/// reachability cutoff.
/// </param>
/// <param name="TravelDirection">Direction of travel relative to each facility.</param>
/// <param name="OutSrid">Output spatial reference (SRID/WKID). Defaults to 4326.</param>
public sealed record ServiceAreaSolveRequest(
    IReadOnlyList<RoutePoint> Facilities,
    IReadOnlyList<double> Breaks,
    ServiceAreaTravelDirection TravelDirection = ServiceAreaTravelDirection.FromFacility,
    int OutSrid = 4326)
{
    private readonly int? _inSrid;

    /// <summary>
    /// Spatial reference (SRID/WKID) the input facility ordinates are expressed in.
    /// Defaults to <see cref="OutSrid"/> so a single <c>outSR</c> interprets
    /// facilities in that SRID. Used to transform facilities to the graph SRID
    /// before snapping.
    /// </summary>
    public int InSrid
    {
        get => _inSrid ?? OutSrid;
        init => _inSrid = value;
    }

    /// <summary>
    /// Point/line/polygon barriers to honour. The provider excludes the graph
    /// edges each barrier restricts before computing reachability. Empty when no
    /// barriers are supplied. Only barrier kinds the provider advertises ever
    /// reach the provider (the adapter rejects unsupported kinds first).
    /// </summary>
    public IReadOnlyList<RouteBarrier> Barriers { get; init; } = [];

    /// <summary>
    /// Esri-style travel-mode identifier. <c>null</c> selects the provider
    /// default. Validated against
    /// <see cref="RoutingProviderCapabilities.SupportedTravelModes"/> by the
    /// adapter before solving.
    /// </summary>
    public string? TravelMode { get; init; }
}

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

/// <summary>
/// Direction of travel relative to a closest-facility incident/facility pair.
/// Mirrors the Esri <c>esriNATravelDirectionToFacility</c> /
/// <c>esriNATravelDirectionFromFacility</c> options.
/// </summary>
public enum ClosestFacilityTravelDirection
{
    /// <summary>
    /// Cost is accumulated travelling from the incident to the facility (the
    /// common "find the nearest facility a traveller can reach" case).
    /// </summary>
    ToFacility = 0,

    /// <summary>
    /// Cost is accumulated travelling from the facility to the incident (e.g. an
    /// emergency vehicle dispatched from a facility to an incident).
    /// </summary>
    FromFacility = 1,
}

/// <summary>
/// Protocol-neutral request to solve closest-facility routes: for each incident,
/// rank the supplied facilities by network impedance and materialize the route to
/// the closest <see cref="DefaultTargetFacilityCount"/> of them.
/// </summary>
/// <param name="Incidents">Incident points to find the closest facilities for.</param>
/// <param name="Facilities">Candidate facility points to rank by impedance.</param>
/// <param name="DefaultTargetFacilityCount">
/// Number of closest facilities to return per incident (ranked ascending by
/// impedance). Defaults to 1.
/// </param>
/// <param name="Cutoff">
/// Optional impedance cutoff (minutes). Facilities beyond this cost are excluded.
/// <c>null</c> applies no cutoff.
/// </param>
/// <param name="Direction">Direction of travel relative to the facility.</param>
/// <param name="OutSrid">Output spatial reference (SRID/WKID). Defaults to 4326.</param>
public sealed record ClosestFacilitySolveRequest(
    IReadOnlyList<RoutePoint> Incidents,
    IReadOnlyList<RoutePoint> Facilities,
    int DefaultTargetFacilityCount = 1,
    double? Cutoff = null,
    ClosestFacilityTravelDirection Direction = ClosestFacilityTravelDirection.ToFacility,
    int OutSrid = 4326)
{
    private readonly int? _inSrid;

    /// <summary>
    /// Spatial reference (SRID/WKID) the input incident/facility ordinates are
    /// expressed in. Defaults to <see cref="OutSrid"/>. Used to transform inputs to
    /// the graph SRID before snapping.
    /// </summary>
    public int InSrid
    {
        get => _inSrid ?? OutSrid;
        init => _inSrid = value;
    }

    /// <summary>
    /// Point/line/polygon barriers to honour. The provider excludes the graph edges
    /// each barrier restricts before solving. Empty when no barriers are supplied.
    /// Only barrier kinds the provider advertises ever reach the provider (the
    /// adapter rejects unsupported kinds first).
    /// </summary>
    public IReadOnlyList<RouteBarrier> Barriers { get; init; } = [];

    /// <summary>
    /// Esri-style travel-mode identifier. <c>null</c> selects the provider default.
    /// Validated against
    /// <see cref="RoutingProviderCapabilities.SupportedTravelModes"/> by the adapter.
    /// </summary>
    public string? TravelMode { get; init; }
}

/// <summary>
/// One solved closest-facility route from an incident to a ranked facility.
/// </summary>
/// <param name="IncidentId">Zero-based index of the incident.</param>
/// <param name="FacilityId">Zero-based index of the chosen facility.</param>
/// <param name="Rank">1-based rank of this facility for the incident (1 = closest).</param>
/// <param name="RouteGeometryGeoJson">
/// Merged route geometry as a GeoJSON <c>LineString</c> string in the request's
/// <see cref="ClosestFacilitySolveRequest.OutSrid"/>. Empty when only ranking
/// (no geometry) was produced.
/// </param>
/// <param name="TotalLengthMeters">Route length in meters.</param>
/// <param name="TotalTimeMinutes">Route travel time (impedance) in minutes.</param>
/// <param name="Directions">Ordered turn-by-turn steps; may be empty.</param>
public sealed record ClosestFacilityRoute(
    int IncidentId,
    int FacilityId,
    int Rank,
    string RouteGeometryGeoJson,
    double TotalLengthMeters,
    double TotalTimeMinutes,
    IReadOnlyList<RouteDirectionStep> Directions);

/// <summary>
/// Result of solving closest-facility routes.
/// </summary>
/// <param name="Routes">
/// Ranked closest-facility routes, ordered by incident then ascending rank.
/// </param>
public sealed record ClosestFacilitySolveResult(
    IReadOnlyList<ClosestFacilityRoute> Routes);

/// <summary>
/// Protocol-neutral request to solve an origins×destinations cost matrix. Produces
/// an impedance value per origin/destination pair (attribute-only; no route
/// geometry by default).
/// </summary>
/// <param name="Origins">Origin points (matrix rows).</param>
/// <param name="Destinations">Destination points (matrix columns).</param>
/// <param name="OutSrid">Output spatial reference (SRID/WKID). Defaults to 4326.</param>
public sealed record OdCostMatrixSolveRequest(
    IReadOnlyList<RoutePoint> Origins,
    IReadOnlyList<RoutePoint> Destinations,
    int OutSrid = 4326)
{
    private readonly int? _inSrid;

    /// <summary>
    /// Spatial reference (SRID/WKID) the input ordinates are expressed in. Defaults
    /// to <see cref="OutSrid"/>. Used to transform inputs to the graph SRID before
    /// snapping.
    /// </summary>
    public int InSrid
    {
        get => _inSrid ?? OutSrid;
        init => _inSrid = value;
    }

    /// <summary>
    /// Optional impedance cutoff (minutes). Origin/destination pairs beyond this
    /// cost are excluded. <c>null</c> applies no cutoff.
    /// </summary>
    public double? Cutoff { get; init; }

    /// <summary>
    /// Optional k-nearest limit: keep only the closest <c>DestinationCount</c>
    /// destinations per origin (ranked ascending by cost). <c>null</c> keeps all.
    /// </summary>
    public int? DestinationCount { get; init; }

    /// <summary>
    /// Point/line/polygon barriers to honour. Empty when none are supplied. Only
    /// barrier kinds the provider advertises reach the provider.
    /// </summary>
    public IReadOnlyList<RouteBarrier> Barriers { get; init; } = [];

    /// <summary>
    /// Esri-style travel-mode identifier. <c>null</c> selects the provider default.
    /// Validated against the provider's advertised modes by the adapter.
    /// </summary>
    public string? TravelMode { get; init; }

    /// <summary>
    /// Requested line materialization mode. The default preserves the cost-only
    /// fast path; straight lines connect the original origin/destination points
    /// and are emitted in <see cref="OutSrid"/>.
    /// </summary>
    public OdLineOutputType OutputType { get; init; } = OdLineOutputType.NoLines;
}

/// <summary>
/// Geometry materialization modes supported by the canonical OD matrix pipeline.
/// Network true-shape lines are intentionally absent until providers expose a
/// bounded path-geometry contract.
/// </summary>
public enum OdLineOutputType
{
    /// <summary>Return ranked costs without line geometry.</summary>
    NoLines,

    /// <summary>Return a two-vertex line from each origin to its destination.</summary>
    StraightLines,
}

/// <summary>
/// One origin→destination cell of a cost matrix.
/// </summary>
/// <param name="OriginId">Zero-based index of the origin.</param>
/// <param name="DestinationId">Zero-based index of the destination.</param>
/// <param name="DestinationRank">1-based rank of this destination for the origin (1 = closest).</param>
/// <param name="TotalCostMinutes">Aggregate impedance (minutes) for the pair.</param>
/// <param name="TotalLengthMeters">
/// Aggregate length (meters) for the pair. 0 when length was not materialized
/// (cost-only matrices estimate length lazily; the MVP returns 0).
/// </param>
/// <param name="GeometryGeoJson">
/// Optional GeoJSON LineString in the request's output SRID. Present only when
/// <see cref="OdCostMatrixSolveRequest.OutputType"/> requests materialized lines.
/// </param>
public sealed record OdLine(
    int OriginId,
    int DestinationId,
    int DestinationRank,
    double TotalCostMinutes,
    double TotalLengthMeters,
    string? GeometryGeoJson = null);

/// <summary>
/// Result of solving an OD cost matrix.
/// </summary>
/// <param name="Lines">
/// Origin→destination cells, ordered by origin then ascending rank.
/// </param>
public sealed record OdCostMatrixSolveResult(
    IReadOnlyList<OdLine> Lines);

/// <summary>
/// A demand point for a location-allocation solve: a location whose demand
/// (weight) is allocated to the closest chosen facility within the impedance
/// cutoff.
/// </summary>
/// <param name="Location">The demand-point location.</param>
/// <param name="Weight">
/// Demand weight (e.g. population). Defaults to 1. Used to weight the impedance
/// objective and coverage totals.
/// </param>
public sealed record DemandPoint(RoutePoint Location, double Weight = 1.0);

/// <summary>
/// The location-allocation problem type. The MVP implements the two most-requested
/// types over a precomputed cost matrix; other Esri types are gated with a 400 by
/// the adapter.
/// </summary>
public enum LocationAllocationProblemType
{
    /// <summary>
    /// Choose facilities to minimize the total demand-weighted impedance from each
    /// demand point to its closest chosen facility (the p-median problem).
    /// </summary>
    MinimizeImpedance = 0,

    /// <summary>
    /// Choose facilities to maximize the total demand weight covered within the
    /// impedance cutoff (the maximal-coverage problem).
    /// </summary>
    MaximizeCoverage = 1,
}

/// <summary>
/// Protocol-neutral request to solve a location-allocation problem: choose
/// <see cref="FacilitiesToFind"/> facilities from the candidate
/// <see cref="Facilities"/> to optimize the <see cref="ProblemType"/> objective
/// over the supplied <see cref="DemandPoints"/>.
/// </summary>
/// <param name="Facilities">Candidate facility locations to choose from.</param>
/// <param name="DemandPoints">Weighted demand points to allocate.</param>
/// <param name="ProblemType">The optimization objective.</param>
/// <param name="FacilitiesToFind">Number of facilities to choose. Defaults to 1.</param>
/// <param name="ImpedanceCutoff">
/// Maximum impedance (minutes) a demand point may be from a facility to be served.
/// Required for <see cref="LocationAllocationProblemType.MaximizeCoverage"/>;
/// optional for minimize-impedance (demand beyond the cutoff is treated as
/// unallocated). <c>null</c> applies no cutoff.
/// </param>
/// <param name="OutSrid">Output spatial reference (SRID/WKID). Defaults to 4326.</param>
public sealed record LocationAllocationSolveRequest(
    IReadOnlyList<RoutePoint> Facilities,
    IReadOnlyList<DemandPoint> DemandPoints,
    LocationAllocationProblemType ProblemType = LocationAllocationProblemType.MinimizeImpedance,
    int FacilitiesToFind = 1,
    double? ImpedanceCutoff = null,
    int OutSrid = 4326)
{
    private readonly int? _inSrid;

    /// <summary>
    /// Spatial reference (SRID/WKID) the input ordinates are expressed in. Defaults
    /// to <see cref="OutSrid"/>. Used to transform inputs to the graph SRID before
    /// snapping.
    /// </summary>
    public int InSrid
    {
        get => _inSrid ?? OutSrid;
        init => _inSrid = value;
    }

    /// <summary>
    /// Point/line/polygon barriers to honour. Empty when none are supplied. Only
    /// barrier kinds the provider advertises reach the provider.
    /// </summary>
    public IReadOnlyList<RouteBarrier> Barriers { get; init; } = [];

    /// <summary>
    /// Esri-style travel-mode identifier. <c>null</c> selects the provider default.
    /// Validated against the provider's advertised modes by the adapter.
    /// </summary>
    public string? TravelMode { get; init; }
}

/// <summary>
/// One demand point's allocation to a chosen facility in a location-allocation solve.
/// </summary>
/// <param name="DemandPointId">Zero-based index of the demand point.</param>
/// <param name="AllocatedFacilityId">
/// Zero-based index (into the request's candidate facilities) of the facility the
/// demand point is allocated to, or <c>-1</c> when it is unallocated (no chosen
/// facility within the cutoff).
/// </param>
/// <param name="Weight">The demand point's weight.</param>
/// <param name="ImpedanceMinutes">
/// Impedance (minutes) from the demand point to its allocated facility, or
/// <see cref="double.PositiveInfinity"/> when unallocated.
/// </param>
public sealed record DemandAllocation(
    int DemandPointId,
    int AllocatedFacilityId,
    double Weight,
    double ImpedanceMinutes);

/// <summary>
/// Result of a location-allocation solve.
/// </summary>
/// <param name="ChosenFacilityIds">
/// Zero-based indices (into the request's candidate facilities) of the chosen
/// facilities, ordered by index.
/// </param>
/// <param name="Allocations">Per-demand-point allocations to chosen facilities.</param>
/// <param name="TotalWeightedImpedance">
/// Total demand-weighted impedance of all allocated demand points (the
/// minimize-impedance objective value). 0 when no demand was allocated.
/// </param>
/// <param name="TotalWeightCovered">
/// Total demand weight allocated within the cutoff (the maximize-coverage objective
/// value).
/// </param>
public sealed record LocationAllocationSolveResult(
    IReadOnlyList<int> ChosenFacilityIds,
    IReadOnlyList<DemandAllocation> Allocations,
    double TotalWeightedImpedance,
    double TotalWeightCovered);

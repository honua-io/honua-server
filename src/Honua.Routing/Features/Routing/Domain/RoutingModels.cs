// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Routing.Features.Routing.Domain;

public readonly record struct RoutePoint(double Lon, double Lat);

public enum RouteBarrierKind
{
    Point = 0,

    Line = 1,

    Polygon = 2,
}

public sealed record RouteBarrier(RouteBarrierKind Kind, string GeometryGeoJson);

public enum ServiceAreaTravelDirection
{
    FromFacility = 0,

    ToFacility = 1,
}

public sealed record RouteSolveRequest(
    IReadOnlyList<RoutePoint> Stops,
    string TravelProfile = "driving",
    int OutSrid = 4326)
{
    private readonly int? _inSrid;

    public int InSrid
    {
        get => _inSrid ?? OutSrid;
        init => _inSrid = value;
    }

    public IReadOnlyList<RouteBarrier> Barriers { get; init; } = [];

    public string? TravelMode { get; init; }
}

public sealed record RouteDirectionStep(
    string Text,
    double Length,
    double Time,
    string ManeuverType);

public sealed record RouteSolveResult(
    string RouteGeometryGeoJson,
    double TotalLengthMeters,
    double TotalTimeMinutes,
    IReadOnlyList<RouteDirectionStep> Directions)
{
    public bool Solved => !string.IsNullOrEmpty(RouteGeometryGeoJson);
}

public sealed record ServiceAreaSolveRequest(
    IReadOnlyList<RoutePoint> Facilities,
    IReadOnlyList<double> Breaks,
    ServiceAreaTravelDirection TravelDirection = ServiceAreaTravelDirection.FromFacility,
    int OutSrid = 4326)
{
    private readonly int? _inSrid;

    public int InSrid
    {
        get => _inSrid ?? OutSrid;
        init => _inSrid = value;
    }

    public IReadOnlyList<RouteBarrier> Barriers { get; init; } = [];

    public string? TravelMode { get; init; }
}

public sealed record ServiceAreaPolygon(
    int FacilityId,
    double FromBreak,
    double ToBreak,
    string GeometryGeoJson);

public sealed record ServiceAreaSolveResult(
    IReadOnlyList<ServiceAreaPolygon> Polygons);

public enum ClosestFacilityTravelDirection
{
    ToFacility = 0,

    FromFacility = 1,
}

public sealed record ClosestFacilitySolveRequest(
    IReadOnlyList<RoutePoint> Incidents,
    IReadOnlyList<RoutePoint> Facilities,
    int DefaultTargetFacilityCount = 1,
    double? Cutoff = null,
    ClosestFacilityTravelDirection Direction = ClosestFacilityTravelDirection.ToFacility,
    int OutSrid = 4326)
{
    private readonly int? _inSrid;

    public int InSrid
    {
        get => _inSrid ?? OutSrid;
        init => _inSrid = value;
    }

    public IReadOnlyList<RouteBarrier> Barriers { get; init; } = [];

    public string? TravelMode { get; init; }
}

public sealed record ClosestFacilityRoute(
    int IncidentId,
    int FacilityId,
    int Rank,
    string RouteGeometryGeoJson,
    double TotalLengthMeters,
    double TotalTimeMinutes,
    IReadOnlyList<RouteDirectionStep> Directions);

public sealed record ClosestFacilitySolveResult(
    IReadOnlyList<ClosestFacilityRoute> Routes);

public sealed record OdCostMatrixSolveRequest(
    IReadOnlyList<RoutePoint> Origins,
    IReadOnlyList<RoutePoint> Destinations,
    int OutSrid = 4326)
{
    private readonly int? _inSrid;

    public int InSrid
    {
        get => _inSrid ?? OutSrid;
        init => _inSrid = value;
    }

    public double? Cutoff { get; init; }

    public int? DestinationCount { get; init; }

    public IReadOnlyList<RouteBarrier> Barriers { get; init; } = [];

    public string? TravelMode { get; init; }

    public OdLineOutputType OutputType { get; init; } = OdLineOutputType.NoLines;
}

public enum OdLineOutputType
{
    NoLines,

    StraightLines,
}

public sealed record OdLine(
    int OriginId,
    int DestinationId,
    int DestinationRank,
    double TotalCostMinutes,
    double TotalLengthMeters,
    string? GeometryGeoJson = null);

public sealed record OdCostMatrixSolveResult(
    IReadOnlyList<OdLine> Lines);

public sealed record DemandPoint(RoutePoint Location, double Weight = 1.0);

public enum LocationAllocationProblemType
{
    MinimizeImpedance = 0,

    MaximizeCoverage = 1,

    MinimizeFacilities = 2,
}

public sealed record LocationAllocationSolveRequest(
    IReadOnlyList<RoutePoint> Facilities,
    IReadOnlyList<DemandPoint> DemandPoints,
    LocationAllocationProblemType ProblemType = LocationAllocationProblemType.MinimizeImpedance,
    int FacilitiesToFind = 1,
    double? ImpedanceCutoff = null,
    int OutSrid = 4326)
{
    private readonly int? _inSrid;

    public int InSrid
    {
        get => _inSrid ?? OutSrid;
        init => _inSrid = value;
    }

    public IReadOnlyList<RouteBarrier> Barriers { get; init; } = [];

    public string? TravelMode { get; init; }
}

public sealed record DemandAllocation(
    int DemandPointId,
    int AllocatedFacilityId,
    double Weight,
    double ImpedanceMinutes);

public sealed record LocationAllocationSolveResult(
    IReadOnlyList<int> ChosenFacilityIds,
    IReadOnlyList<DemandAllocation> Allocations,
    double TotalWeightedImpedance,
    double TotalWeightCovered);

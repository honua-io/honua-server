// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Routing.Features.Routing.Domain;

namespace Honua.Geoprocessing.Execution;

// Use ordinary setters at this serialization boundary. In .NET 10 source-generated
// deserialization overwrites absent init-only properties with their CLR defaults
// (dotnet/runtime#84484), losing both the fallback input CRS and empty barriers.
// Map into the immutable canonical models only after deserialization.
internal abstract class RoutingJobRequest
{
    private int? _inSrid;

    public int OutSrid { get; set; } = 4326;

    public int InSrid
    {
        get => _inSrid ?? OutSrid;
        set => _inSrid = value;
    }

    public IReadOnlyList<RouteBarrier> Barriers { get; set; } = [];

    public string? TravelMode { get; set; }
}

internal sealed class RouteJobRequest : RoutingJobRequest
{
    public IReadOnlyList<RoutePoint> Stops { get; set; } = [];

    public string TravelProfile { get; set; } = "driving";

    public RouteSolveRequest ToCanonicalRequest() => new(Stops, TravelProfile, OutSrid)
    {
        InSrid = InSrid,
        Barriers = Barriers,
        TravelMode = TravelMode,
    };
}

internal sealed class ServiceAreaJobRequest : RoutingJobRequest
{
    public IReadOnlyList<RoutePoint> Facilities { get; set; } = [];

    public IReadOnlyList<double> Breaks { get; set; } = [];

    public ServiceAreaTravelDirection TravelDirection { get; set; } = ServiceAreaTravelDirection.FromFacility;

    public ServiceAreaSolveRequest ToCanonicalRequest() => new(Facilities, Breaks, TravelDirection, OutSrid)
    {
        InSrid = InSrid,
        Barriers = Barriers,
        TravelMode = TravelMode,
    };
}

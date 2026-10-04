// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Routing.Features.Routing.Domain;

namespace Honua.Geoprocessing.Execution;

// Keep defaults for omitted JSON properties at this serialization boundary.
// Map into the immutable canonical models only after deserialization, so their
// init-only input CRS and barrier defaults are not replaced with CLR defaults.
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

    protected static IReadOnlyList<RoutePoint> ToCanonicalPoints(IReadOnlyList<RoutingJobPoint> points)
        => points?.Select(point => point?.ToCanonicalPoint()
            ?? throw new ArgumentException("A routing point is required.")).ToArray()!;
}

internal sealed class RoutingJobPoint
{
    [JsonRequired]
    public double Lon { get; set; }

    [JsonRequired]
    public double Lat { get; set; }

    public RoutePoint ToCanonicalPoint() => new(Lon, Lat);
}

internal sealed class RouteJobRequest : RoutingJobRequest
{
    public IReadOnlyList<RoutingJobPoint> Stops { get; set; } = [];

    public string TravelProfile { get; set; } = "driving";

    public RouteSolveRequest ToCanonicalRequest() => new(ToCanonicalPoints(Stops), TravelProfile, OutSrid)
    {
        InSrid = InSrid,
        Barriers = Barriers,
        TravelMode = TravelMode,
    };
}

internal sealed class ServiceAreaJobRequest : RoutingJobRequest
{
    public IReadOnlyList<RoutingJobPoint> Facilities { get; set; } = [];

    public IReadOnlyList<double> Breaks { get; set; } = [];

    public ServiceAreaTravelDirection TravelDirection { get; set; } = ServiceAreaTravelDirection.FromFacility;

    public ServiceAreaSolveRequest ToCanonicalRequest() => new(ToCanonicalPoints(Facilities), Breaks, TravelDirection, OutSrid)
    {
        InSrid = InSrid,
        Barriers = Barriers,
        TravelMode = TravelMode,
    };
}

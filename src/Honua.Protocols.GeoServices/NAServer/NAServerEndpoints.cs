// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Protocols.GeoServices.NAServer.Models;

namespace Honua.Server.Features.Protocols.GeoServices.NAServer;

/// <summary>
/// Maps the minimal GeoServices NAServer compatibility endpoints used by mobile routing clients.
/// </summary>
internal static class NAServerEndpoints
{
    private const string RouteBase = "/rest/services/{serviceId}/NAServer";
    private const string JsonContentType = "application/json";

    private static readonly NAServerRouteSolveResponse RouteSolveResponse = new()
    {
        Routes = new NAServerRouteFeatureSet
        {
            Features =
            [
                new NAServerRouteFeature
                {
                    Attributes = new NAServerRouteAttributes
                    {
                        Name = "Route 1",
                        TotalLength = 10.5,
                        TotalTime = 25
                    }
                }
            ]
        },
        Directions =
        [
            new NAServerDirection
            {
                Features =
                [
                    new NAServerDirectionFeature
                    {
                        Attributes = new NAServerDirectionAttributes
                        {
                            Text = "Head east",
                            Length = 0.1,
                            Time = 1.2,
                            ManeuverType = "esriDMTDepart"
                        }
                    }
                ]
            }
        ]
    };

    private static readonly NAServerClosestFacilityResponse ClosestFacilityResponse = new()
    {
        Routes = new NAServerRouteFeatureSet
        {
            Features =
            [
                new NAServerRouteFeature
                {
                    Attributes = new NAServerRouteAttributes
                    {
                        Name = "Incident - Facility A",
                        TotalLength = 2.5,
                        TotalTime = 8
                    }
                }
            ]
        },
        Directions =
        [
            new NAServerDirection
            {
                Summary = new NAServerDirectionSummary
                {
                    RouteName = "Incident - Facility A",
                    TotalLength = 2.5,
                    TotalTime = 8
                }
            }
        ]
    };

    /// <summary>
    /// Maps NAServer endpoints.
    /// </summary>
    public static IEndpointRouteBuilder MapNAServerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // PUBLIC by design (#1144): minimal NAServer compatibility stubs that
        // return deterministic envelopes for mobile routing client probes;
        // no caller state, no data mutation. Marked AllowAnonymous so the
        // audit guard records the intentional decision.
        endpoints.MapPost($"{RouteBase}/Route/solve", static () => HandleRouteSolve())
            .WithDisplayName("NAServer Route Solve")
            .WithName("NAServerRouteSolve")
            .WithSummary("Solve a minimal NAServer route")
            .WithDescription("Returns a deterministic route and direction envelope for first-party mobile routing integration.")
            .WithTags("NAServer")
            .Produces<NAServerRouteSolveResponse>(StatusCodes.Status200OK, JsonContentType)
            .AllowAnonymous();

        endpoints.MapPost($"{RouteBase}/ServiceArea/solveServiceArea", static () => HandleServiceArea())
            .WithDisplayName("NAServer Service Area Solve")
            .WithName("NAServerServiceAreaSolve")
            .WithSummary("Solve a minimal NAServer service area")
            .WithDescription("Returns an empty deterministic service-area envelope for first-party mobile routing integration.")
            .WithTags("NAServer")
            .Produces<NAServerServiceAreaResponse>(StatusCodes.Status200OK, JsonContentType)
            .AllowAnonymous();

        endpoints.MapPost($"{RouteBase}/ClosestFacility/solveClosestFacility", static () => HandleClosestFacility())
            .WithDisplayName("NAServer Closest Facility Solve")
            .WithName("NAServerClosestFacilitySolve")
            .WithSummary("Solve a minimal NAServer closest facility")
            .WithDescription("Returns a deterministic closest-facility direction envelope for first-party mobile routing integration.")
            .WithTags("NAServer")
            .Produces<NAServerClosestFacilityResponse>(StatusCodes.Status200OK, JsonContentType)
            .AllowAnonymous();

        return endpoints;
    }

    private static IResult HandleRouteSolve()
        => Results.Json(
            RouteSolveResponse,
            NAServerJsonContext.Default.NAServerRouteSolveResponse,
            contentType: JsonContentType);

    private static IResult HandleServiceArea()
        => Results.Json(
            new NAServerServiceAreaResponse(),
            NAServerJsonContext.Default.NAServerServiceAreaResponse,
            contentType: JsonContentType);

    private static IResult HandleClosestFacility()
        => Results.Json(
            ClosestFacilityResponse,
            NAServerJsonContext.Default.NAServerClosestFacilityResponse,
            contentType: JsonContentType);
}

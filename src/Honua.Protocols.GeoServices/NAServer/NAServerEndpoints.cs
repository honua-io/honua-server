// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text.Json;
using Honua.Infrastructure.Models;
using Honua.Protocols.GeoServices.GPServer;
using Honua.Protocols.GeoServices.NAServer.Models;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.GeoServices.NAServer;

/// <summary>
/// Maps the GeoServices NAServer REST endpoints as a thin protocol adapter over the
/// shared <see cref="IRoutingProvider"/> pipeline. Route / ServiceArea solves parse
/// Esri parameters, delegate to the canonical routing provider, and format the Esri
/// JSON response. The ClosestFacility stub remains a deterministic probe envelope
/// pending a dedicated closest-facility canonical contract.
/// </summary>
internal static class NAServerEndpoints
{
    private const string RouteBase = "/rest/services/{serviceId}/NAServer";
    private const string JsonContentType = "application/json";

    // Indented serializer options for f=pjson. Copies the source-generated context's
    // resolver so the AOT-safe metadata is reused, layering WriteIndented on top.
    private static readonly JsonSerializerOptions PrettyJsonOptions = new(NAServerJsonContext.Default.Options)
    {
        WriteIndented = true,
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
                        TotalTravelTime = 8,
                    },
                },
            ],
        },
        Directions =
        [
            new NAServerDirection
            {
                Summary = new NAServerDirectionSummary
                {
                    RouteName = "Incident - Facility A",
                    TotalLength = 2.5,
                    TotalTime = 8,
                },
            },
        ],
    };

    /// <summary>
    /// Maps NAServer endpoints.
    /// </summary>
    public static IEndpointRouteBuilder MapNAServerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // ANONYMOUS by design (#1144, #1266): NAServer Route/ServiceArea solves are
        // stateless geospatial computations over a fixed routing capability — not a
        // metadata-published, per-tenant feature service — so there is no published
        // service to RBAC-gate. This mirrors the GeometryService buffer/simplify/project
        // compute endpoints, which are AllowAnonymous for the same reason. Marked
        // AllowAnonymous so the audit guard records the intentional decision.
        endpoints.MapPost($"{RouteBase}/Route/solve",
                static (HttpContext context, IRoutingProvider routing, IOptions<RoutingConfiguration> options, CancellationToken ct)
                    => HandleRouteSolve(context, routing, options.Value, ct))
            .WithDisplayName("NAServer Route Solve")
            .WithName("NAServerRouteSolve")
            .WithSummary("Solve a NAServer route")
            .WithDescription("Solves a multi-stop route through the shared routing pipeline and returns an Esri route feature set.")
            .WithTags("NAServer")
            .Produces<NAServerRouteSolveResponse>(StatusCodes.Status200OK, JsonContentType)
            .AllowAnonymous();

        endpoints.MapPost($"{RouteBase}/ServiceArea/solveServiceArea",
                static (HttpContext context, IRoutingProvider routing, IOptions<RoutingConfiguration> options, CancellationToken ct)
                    => HandleServiceArea(context, routing, options.Value, ct))
            .WithDisplayName("NAServer Service Area Solve")
            .WithName("NAServerServiceAreaSolve")
            .WithSummary("Solve a NAServer service area")
            .WithDescription("Solves service-area (isochrone) polygons through the shared routing pipeline and returns an Esri saPolygons feature set.")
            .WithTags("NAServer")
            .Produces<NAServerServiceAreaResponse>(StatusCodes.Status200OK, JsonContentType)
            .AllowAnonymous();

        // PUBLIC by design (#1144): minimal ClosestFacility compatibility stub that
        // returns a deterministic envelope for mobile routing client probes. Not yet
        // adapted over a canonical closest-facility contract (out of scope for #1266).
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

    private static async Task<IResult> HandleRouteSolve(
        HttpContext context,
        IRoutingProvider routing,
        RoutingConfiguration configuration,
        CancellationToken ct)
    {
        EnrichActivity("RouteSolve");

        var parameters = await GPServerParameterTranslation.ReadRequestParametersAsync(context, ct);
        var formatError = ValidateJsonFormat(context, parameters);
        if (formatError is not null)
        {
            return formatError;
        }

        // Capability gate: read from the SAME provider instance we solve with so the
        // guard reflects the engine that would run. If route solves are not advertised,
        // emit the standard Esri 400 error rather than attempting the solve.
        if (!routing.Capabilities.SupportsRoute)
        {
            return SetSpanErrorAndReturn(
                StandardErrorHelpers.CreateBadRequest(
                    context,
                    "Route solves are not supported by the configured routing provider."),
                "NAServer route solves unsupported by provider");
        }

        try
        {
            var caps = NAServerInputCaps.FromConfiguration(configuration);
            var request = NAServerParameterTranslation.BuildRouteSolveRequest(parameters, caps);
            var includeRoutes = ReadBool(parameters, "returnRoutes", defaultValue: true);
            var includeDirections = ReadBool(parameters, "returnDirections", defaultValue: false);

            var result = await routing.SolveRouteAsync(request, ct);
            var response = NAServerResultMapping.MapRoute(
                result, request.OutSrid, includeRoutes, includeDirections);

            return WriteResponse(
                context,
                parameters,
                response,
                NAServerJsonContext.Default.NAServerRouteSolveResponse);
        }
        catch (NAServerParameterTranslation.NAServerParameterException)
        {
            return SetSpanErrorAndReturn(
                StandardErrorHelpers.CreateBadRequest(context, "Invalid NAServer route parameters."),
                "Invalid NAServer route parameters");
        }
    }

    private static async Task<IResult> HandleServiceArea(
        HttpContext context,
        IRoutingProvider routing,
        RoutingConfiguration configuration,
        CancellationToken ct)
    {
        EnrichActivity("ServiceAreaSolve");

        var parameters = await GPServerParameterTranslation.ReadRequestParametersAsync(context, ct);
        var formatError = ValidateJsonFormat(context, parameters);
        if (formatError is not null)
        {
            return formatError;
        }

        // Capability gate: read from the SAME provider instance we solve with. If
        // service-area solves are not advertised, emit the standard Esri 400 error.
        if (!routing.Capabilities.SupportsServiceArea)
        {
            return SetSpanErrorAndReturn(
                StandardErrorHelpers.CreateBadRequest(
                    context,
                    "Service-area solves are not supported by the configured routing provider."),
                "NAServer service-area solves unsupported by provider");
        }

        try
        {
            var caps = NAServerInputCaps.FromConfiguration(configuration);
            var request = NAServerParameterTranslation.BuildServiceAreaSolveRequest(parameters, caps);

            // Validate the requested travel direction against the provider's advertised
            // directions. The direction was parsed by BuildServiceAreaSolveRequest above,
            // so this gates the same value the provider would solve with.
            if (!routing.Capabilities.SupportedTravelDirections.Contains(request.TravelDirection))
            {
                return SetSpanErrorAndReturn(
                    StandardErrorHelpers.CreateBadRequest(
                        context,
                        $"travelDirection '{request.TravelDirection}' is not supported by the configured routing provider."),
                    "NAServer travelDirection unsupported by provider");
            }

            var result = await routing.SolveServiceAreaAsync(request, ct);
            var response = NAServerResultMapping.MapServiceArea(result, request.OutSrid);

            return WriteResponse(
                context,
                parameters,
                response,
                NAServerJsonContext.Default.NAServerServiceAreaResponse);
        }
        catch (NAServerParameterTranslation.NAServerParameterException)
        {
            return SetSpanErrorAndReturn(
                StandardErrorHelpers.CreateBadRequest(context, "Invalid NAServer service-area parameters."),
                "Invalid NAServer service-area parameters");
        }
    }

    /// <summary>
    /// Serializes the response, emitting indented JSON for <c>f=pjson</c> and compact
    /// JSON otherwise. The pjson path reuses the source-generated resolver so AOT
    /// metadata is preserved.
    /// </summary>
    private static IResult WriteResponse<T>(
        HttpContext context,
        IReadOnlyDictionary<string, string> parameters,
        T response,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        if (IsPrettyJson(context, parameters))
        {
            return Results.Json(response, PrettyJsonOptions, contentType: JsonContentType);
        }

        return Results.Json(response, typeInfo, contentType: JsonContentType);
    }

    private static bool IsPrettyJson(HttpContext context, IReadOnlyDictionary<string, string> parameters)
    {
        string? format = null;
        if (parameters.TryGetValue("f", out var parameterFormat))
        {
            format = parameterFormat;
        }
        else if (context.Request.Query.TryGetValue("f", out var queryFormat))
        {
            format = queryFormat.ToString();
        }

        return format is not null && format.Trim().Equals("pjson", StringComparison.OrdinalIgnoreCase);
    }

    private static IResult HandleClosestFacility()
        => Results.Json(
            ClosestFacilityResponse,
            NAServerJsonContext.Default.NAServerClosestFacilityResponse,
            contentType: JsonContentType);

    private static bool ReadBool(IReadOnlyDictionary<string, string> parameters, string key, bool defaultValue)
    {
        if (!parameters.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" => true,
            "false" or "0" or "no" => false,
            _ => defaultValue,
        };
    }

    private static IResult? ValidateJsonFormat(HttpContext context, IReadOnlyDictionary<string, string> parameters)
    {
        string? format = null;
        if (parameters.TryGetValue("f", out var parameterFormat))
        {
            format = parameterFormat;
        }
        else if (context.Request.Query.TryGetValue("f", out var queryFormat))
        {
            format = queryFormat.ToString();
        }

        if (string.IsNullOrWhiteSpace(format) ||
            format.Equals("json", StringComparison.OrdinalIgnoreCase) ||
            format.Equals("pjson", StringComparison.OrdinalIgnoreCase) ||
            format.Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return SetSpanErrorAndReturn(
            StandardErrorHelpers.CreateBadRequest(
                context,
                "Unsupported output format",
                [$"Format '{format}' is not supported. Use f=json."]),
            "Unsupported NAServer output format");
    }

    private static IResult SetSpanErrorAndReturn(IResult result, string? message = null)
    {
        var activity = Activity.Current;
        if (activity is not null)
        {
            activity.SetStatus(ActivityStatusCode.Error, message);
        }

        return result;
    }

    private static void EnrichActivity(string operation)
    {
        var activity = Activity.Current;
        if (activity is null)
        {
            return;
        }

        activity.SetTag("honua.protocol", "NAServer");
        activity.SetTag("honua.operation", operation);
    }
}

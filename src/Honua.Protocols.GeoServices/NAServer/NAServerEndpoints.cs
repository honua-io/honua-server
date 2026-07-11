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
/// shared <see cref="IRoutingProvider"/> pipeline. Route / ServiceArea /
/// ClosestFacility / ODCostMatrix / LocationAllocation solves parse Esri parameters,
/// delegate to the canonical routing provider, and format the Esri JSON response.
/// Unsupported provider capabilities return GeoServices 400 envelopes rather than
/// fabricated solves.
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
        endpoints.MapGet($"{RouteBase}/Route/solve",
                static (HttpContext context, IRoutingProvider routing, IOptions<RoutingConfiguration> options, CancellationToken ct)
                    => HandleRouteSolve(context, routing, options.Value, ct))
            .WithDisplayName("NAServer Route Solve (GET)")
            .WithName("NAServerRouteSolveGet")
            .WithSummary("Solve a NAServer route from query parameters")
            .WithDescription("Solves a multi-stop route from query-string parameters through the shared routing pipeline and returns an Esri route feature set.")
            .WithTags("NAServer")
            .Produces<NAServerRouteSolveResponse>(StatusCodes.Status200OK, JsonContentType)
            .AllowAnonymous();

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

        // ANONYMOUS by design (same rationale as Route/ServiceArea): a stateless
        // closest-facility computation over the shared routing provider.
        endpoints.MapPost($"{RouteBase}/ClosestFacility/solveClosestFacility",
                static (HttpContext context, IRoutingProvider routing, IOptions<RoutingConfiguration> options, CancellationToken ct)
                    => HandleClosestFacility(context, routing, options.Value, ct))
            .WithDisplayName("NAServer Closest Facility Solve")
            .WithName("NAServerClosestFacilitySolve")
            .WithSummary("Solve a NAServer closest facility")
            .WithDescription("Ranks facilities by network impedance per incident over the shared routing pipeline and returns ranked Esri routes.")
            .WithTags("NAServer")
            .Produces<NAServerClosestFacilityResponse>(StatusCodes.Status200OK, JsonContentType)
            .AllowAnonymous();

        endpoints.MapPost($"{RouteBase}/ODCostMatrix/solveODCostMatrix",
                static (HttpContext context, IRoutingProvider routing, IOptions<RoutingConfiguration> options, CancellationToken ct)
                    => HandleOdCostMatrix(context, routing, options.Value, ct))
            .WithDisplayName("NAServer OD Cost Matrix Solve")
            .WithName("NAServerOdCostMatrixSolve")
            .WithSummary("Solve a NAServer OD cost matrix")
            .WithDescription("Computes an origins×destinations impedance matrix over the shared routing pipeline. Supports cost-only and straight-line odLines; true-shape network lines return a precise 400.")
            .WithTags("NAServer")
            .Produces<NAServerOdCostMatrixResponse>(StatusCodes.Status200OK, JsonContentType)
            .AllowAnonymous();

        endpoints.MapPost($"{RouteBase}/LocationAllocation/solveLocationAllocation",
                static (HttpContext context, IRoutingProvider routing, IOptions<RoutingConfiguration> options, CancellationToken ct)
                    => HandleLocationAllocation(context, routing, options.Value, ct))
            .WithDisplayName("NAServer Location Allocation Solve")
            .WithName("NAServerLocationAllocationSolve")
            .WithSummary("Solve a NAServer location-allocation problem")
            .WithDescription("Chooses facilities to minimize impedance, maximize coverage, or greedily minimize facilities within a cutoff over the shared routing pipeline. Objectives requiring capacity, competitor, or impedance-transformation inputs return a precise 400.")
            .WithTags("NAServer")
            .Produces<NAServerLocationAllocationResponse>(StatusCodes.Status200OK, JsonContentType)
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
        var capabilities = await routing.GetCapabilitiesAsync(ct).ConfigureAwait(false);
        if (!capabilities.SupportsRoute)
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

            var capabilityError = ValidateProviderCapabilities(
                context, capabilities, request.Barriers, request.TravelMode);
            if (capabilityError is not null)
            {
                return capabilityError;
            }

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
        catch (Exception ex) when (IsRoutingInvalidSpatialReferenceException(ex))
        {
            return CreateInvalidSpatialReferenceResult(context, "route");
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
        var capabilities = await routing.GetCapabilitiesAsync(ct).ConfigureAwait(false);
        if (!capabilities.SupportsServiceArea)
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
            if (!capabilities.SupportedTravelDirections.Contains(request.TravelDirection))
            {
                return SetSpanErrorAndReturn(
                    StandardErrorHelpers.CreateBadRequest(
                        context,
                        $"travelDirection '{request.TravelDirection}' is not supported by the configured routing provider."),
                    "NAServer travelDirection unsupported by provider");
            }

            var capabilityError = ValidateProviderCapabilities(
                context, capabilities, request.Barriers, request.TravelMode);
            if (capabilityError is not null)
            {
                return capabilityError;
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
        catch (Exception ex) when (IsRoutingInvalidSpatialReferenceException(ex))
        {
            return CreateInvalidSpatialReferenceResult(context, "service-area");
        }
    }

    /// <summary>
    /// Gates barrier and travel-mode inputs against the provider's advertised
    /// capabilities. Returns a GeoServices 400 envelope when the request asks for a
    /// barrier kind the provider does not honour, or a travel mode it does not
    /// route — being honest about what actually works rather than silently
    /// ignoring the input and returning an unrestricted/default solve. Returns
    /// <c>null</c> when the request is within the provider's capabilities.
    /// </summary>
    private static IResult? ValidateProviderCapabilities(
        HttpContext context,
        RoutingProviderCapabilities capabilities,
        IReadOnlyList<RouteBarrier> barriers,
        string? travelMode)
    {
        // Barriers: every requested barrier kind must be advertised. If any kind is
        // unsupported (or barriers are supplied to a provider that supports none),
        // reject rather than dropping the barrier and returning an unsafe solve.
        if (barriers.Count > 0)
        {
            foreach (var kind in barriers.Select(b => b.Kind).Distinct())
            {
                if (!capabilities.SupportedBarrierKinds.Contains(kind))
                {
                    return SetSpanErrorAndReturn(
                        StandardErrorHelpers.CreateBadRequest(
                            context,
                            $"{kind} barriers are not supported by the configured routing provider."),
                        "NAServer barrier kind unsupported by provider");
                }
            }
        }

        // Travel mode: an absent mode always uses the provider default. A supplied
        // mode must be one the provider advertises (case-insensitive).
        if (!string.IsNullOrWhiteSpace(travelMode))
        {
            var supported = capabilities.SupportedTravelModes
                .Any(m => string.Equals(m, travelMode, StringComparison.OrdinalIgnoreCase));
            if (!supported)
            {
                return SetSpanErrorAndReturn(
                    StandardErrorHelpers.CreateBadRequest(
                        context,
                        $"travelMode '{travelMode}' is not supported by the configured routing provider."),
                    "NAServer travelMode unsupported by provider");
            }
        }

        return null;
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

    private static async Task<IResult> HandleClosestFacility(
        HttpContext context,
        IRoutingProvider routing,
        RoutingConfiguration configuration,
        CancellationToken ct)
    {
        EnrichActivity("ClosestFacilitySolve");

        var parameters = await GPServerParameterTranslation.ReadRequestParametersAsync(context, ct);
        var formatError = ValidateJsonFormat(context, parameters);
        if (formatError is not null)
        {
            return formatError;
        }

        var capabilities = await routing.GetCapabilitiesAsync(ct).ConfigureAwait(false);
        if (!capabilities.SupportsClosestFacility)
        {
            return SetSpanErrorAndReturn(
                StandardErrorHelpers.CreateBadRequest(
                    context,
                    "Closest-facility solves are not supported by the configured routing provider."),
                "NAServer closest-facility solves unsupported by provider");
        }

        try
        {
            var caps = NAServerInputCaps.FromConfiguration(configuration);
            var request = NAServerParameterTranslation.BuildClosestFacilitySolveRequest(parameters, caps);
            var includeDirections = ReadBool(parameters, "returnDirections", defaultValue: false);

            var capabilityError = ValidateProviderCapabilities(
                context, capabilities, request.Barriers, request.TravelMode);
            if (capabilityError is not null)
            {
                return capabilityError;
            }

            var result = await routing.SolveClosestFacilityAsync(request, ct);
            var response = NAServerResultMapping.MapClosestFacility(result, request.OutSrid, includeDirections);

            return WriteResponse(
                context,
                parameters,
                response,
                NAServerJsonContext.Default.NAServerClosestFacilityResponse);
        }
        catch (NAServerParameterTranslation.NAServerParameterException ex)
        {
            return SetSpanErrorAndReturn(
                StandardErrorHelpers.CreateBadRequest(context, "Invalid NAServer closest-facility parameters.", [ex.Message]),
                "Invalid NAServer closest-facility parameters");
        }
        catch (Exception ex) when (IsRoutingInvalidSpatialReferenceException(ex))
        {
            return CreateInvalidSpatialReferenceResult(context, "closest-facility");
        }
    }

    private static async Task<IResult> HandleOdCostMatrix(
        HttpContext context,
        IRoutingProvider routing,
        RoutingConfiguration configuration,
        CancellationToken ct)
    {
        EnrichActivity("OdCostMatrixSolve");

        var parameters = await GPServerParameterTranslation.ReadRequestParametersAsync(context, ct);
        var formatError = ValidateJsonFormat(context, parameters);
        if (formatError is not null)
        {
            return formatError;
        }

        var capabilities = await routing.GetCapabilitiesAsync(ct).ConfigureAwait(false);
        if (!capabilities.SupportsOdCostMatrix)
        {
            return SetSpanErrorAndReturn(
                StandardErrorHelpers.CreateBadRequest(
                    context,
                    "OD cost matrix solves are not supported by the configured routing provider."),
                "NAServer OD cost matrix solves unsupported by provider");
        }

        try
        {
            var caps = NAServerInputCaps.FromConfiguration(configuration);
            var request = NAServerParameterTranslation.BuildOdCostMatrixSolveRequest(parameters, caps);
            Activity.Current?.SetTag("honua.routing.od_output_type", request.OutputType.ToString());

            if (request.OutputType == OdLineOutputType.StraightLines &&
                !capabilities.SupportsOdStraightLines)
            {
                return SetSpanErrorAndReturn(
                    StandardErrorHelpers.CreateBadRequest(
                        context,
                        "Straight-line OD geometry is not supported by the configured routing provider. " +
                        "Use outputType=esriNAODOutputNoLines."),
                    "NAServer OD straight lines unsupported by provider");
            }

            var capabilityError = ValidateProviderCapabilities(
                context, capabilities, request.Barriers, request.TravelMode);
            if (capabilityError is not null)
            {
                return capabilityError;
            }

            var result = await routing.SolveOdCostMatrixAsync(request, ct);
            var response = NAServerResultMapping.MapOdCostMatrix(result, request.OutputType, request.OutSrid);

            return WriteResponse(
                context,
                parameters,
                response,
                NAServerJsonContext.Default.NAServerOdCostMatrixResponse);
        }
        catch (NAServerParameterTranslation.NAServerParameterException ex)
        {
            return SetSpanErrorAndReturn(
                StandardErrorHelpers.CreateBadRequest(context, "Invalid NAServer OD cost matrix parameters.", [ex.Message]),
                "Invalid NAServer OD cost matrix parameters");
        }
        catch (Exception ex) when (IsRoutingInvalidSpatialReferenceException(ex))
        {
            return CreateInvalidSpatialReferenceResult(context, "OD cost matrix");
        }
    }

    private static async Task<IResult> HandleLocationAllocation(
        HttpContext context,
        IRoutingProvider routing,
        RoutingConfiguration configuration,
        CancellationToken ct)
    {
        EnrichActivity("LocationAllocationSolve");

        var parameters = await GPServerParameterTranslation.ReadRequestParametersAsync(context, ct);
        var formatError = ValidateJsonFormat(context, parameters);
        if (formatError is not null)
        {
            return formatError;
        }

        var capabilities = await routing.GetCapabilitiesAsync(ct).ConfigureAwait(false);
        if (!capabilities.SupportsLocationAllocation)
        {
            return SetSpanErrorAndReturn(
                StandardErrorHelpers.CreateBadRequest(
                    context,
                    "Location-allocation solves are not supported by the configured routing provider."),
                "NAServer location-allocation solves unsupported by provider");
        }

        try
        {
            var caps = NAServerInputCaps.FromConfiguration(configuration);
            var request = NAServerParameterTranslation.BuildLocationAllocationSolveRequest(parameters, caps);

            // Gate the requested problem type against the provider's advertised set.
            if (!capabilities.SupportedLocationAllocationProblemTypes.Contains(request.ProblemType))
            {
                return SetSpanErrorAndReturn(
                    StandardErrorHelpers.CreateBadRequest(
                        context,
                        $"location-allocation problem type '{request.ProblemType}' is not supported by the configured routing provider."),
                    "NAServer location-allocation problem type unsupported by provider");
            }

            var capabilityError = ValidateProviderCapabilities(
                context, capabilities, request.Barriers, request.TravelMode);
            if (capabilityError is not null)
            {
                return capabilityError;
            }

            var result = await routing.SolveLocationAllocationAsync(request, ct);
            var response = NAServerResultMapping.MapLocationAllocation(result);

            return WriteResponse(
                context,
                parameters,
                response,
                NAServerJsonContext.Default.NAServerLocationAllocationResponse);
        }
        catch (NAServerParameterTranslation.NAServerParameterException ex)
        {
            return SetSpanErrorAndReturn(
                StandardErrorHelpers.CreateBadRequest(context, "Invalid NAServer location-allocation parameters.", [ex.Message]),
                "Invalid NAServer location-allocation parameters");
        }
        catch (Exception ex) when (IsRoutingInvalidSpatialReferenceException(ex))
        {
            return CreateInvalidSpatialReferenceResult(context, "location-allocation");
        }
    }

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

    private static IResult CreateInvalidSpatialReferenceResult(HttpContext context, string operation)
        => SetSpanErrorAndReturn(
            StandardErrorHelpers.CreateBadRequest(
                context,
                $"Invalid NAServer {operation} spatial reference.",
                ["The requested input or output spatial reference is not supported by the configured routing provider."]),
            $"Invalid NAServer {operation} spatial reference");

    private static bool IsRoutingInvalidSpatialReferenceException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            if (ContainsSpatialReferenceSignal(message) && ContainsInvalidSpatialReferenceSignal(message))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsSpatialReferenceSignal(string message)
        => message.Contains("SRID", StringComparison.OrdinalIgnoreCase)
           || message.Contains("spatial reference", StringComparison.OrdinalIgnoreCase)
           || message.Contains("spatial_ref_sys", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsInvalidSpatialReferenceSignal(string message)
        => message.Contains("invalid", StringComparison.OrdinalIgnoreCase)
           || message.Contains("unknown", StringComparison.OrdinalIgnoreCase)
           || message.Contains("not found", StringComparison.OrdinalIgnoreCase)
           || message.Contains("does not exist", StringComparison.OrdinalIgnoreCase);

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

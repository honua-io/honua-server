// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Protocols.GeoServices.GPServer;
using Honua.Protocols.GeoServices.NAServer;
using Honua.Protocols.GeoServices.NAServer.Models;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.GeoServices;

/// <summary>
/// Shared synchronous FindRoutes adapter for the GPServer and NAServer URLs.
/// Supports ordered stops and minutes only; broader ready-to-use routing remains #5192.
/// </summary>
internal static class FindRoutesWebTool
{
    private static readonly HashSet<string> AllowedParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "f", "token", "Stops", "Travel_Mode", "travelMode", "Measurement_Units", "measurementUnits",
        "Reorder_Stops_to_Find_Optimal_Routes", "Reorder_Stops_to_Find_Optimal_Route", "findBestSequence",
        "impedanceAttributeName", "inSR", "outSR", "Point_Barriers", "Line_Barriers", "Polygon_Barriers",
        "barriers", "polylineBarriers", "polygonBarriers",
    };

    internal static async Task<IResult> ExecuteAsync(HttpContext context, CancellationToken ct)
    {
        ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var parameters = await GPServerParameterTranslation.ReadRequestParametersAsync(context, ct).ConfigureAwait(false);
        parameters.TryGetValue("f", out var format);
        if (!string.IsNullOrWhiteSpace(format) &&
            !format.Equals("json", StringComparison.OrdinalIgnoreCase) &&
            !format.Equals("pjson", StringComparison.OrdinalIgnoreCase) &&
            !format.Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Unsupported output format. Use f=json.");
        }

        var routing = context.RequestServices.GetRequiredService<IRoutingProvider>();
        var configuration = context.RequestServices.GetRequiredService<IOptions<RoutingConfiguration>>().Value;
        var capabilities = await routing.GetCapabilitiesAsync(ct).ConfigureAwait(false);
        if (!capabilities.SupportsRoute)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context, "Route solves are not supported by the configured routing provider.");
        }

        try
        {
            var translated = TranslateParameters(parameters);
            var request = NAServerParameterTranslation.BuildRouteSolveRequest(
                translated, NAServerInputCaps.FromConfiguration(configuration));
            var capabilityError = RoutingRequestValidation.ValidateCapabilities(
                capabilities, request.Barriers, request.TravelMode);
            if (capabilityError is not null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, capabilityError);
            }

            var result = await routing.SolveRouteAsync(request, ct).ConfigureAwait(false);
            var solved = NAServerResultMapping.MapRoute(
                result, request.OutSrid, includeRoutes: true, includeDirections: false);
            var document = new JsonObject
            {
                ["results"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["paramName"] = "Output_Routes",
                        ["dataType"] = "GPFeatureRecordSetLayer",
                        ["value"] = JsonSerializer.SerializeToNode(
                            solved.Routes, NAServerJsonContext.Default.NAServerRouteFeatureSet),
                    },
                    new JsonObject
                    {
                        ["paramName"] = "Solve_Succeeded",
                        ["dataType"] = "GPBoolean",
                        ["value"] = result.Solved,
                    },
                },
                ["messages"] = new JsonArray(),
            };
            return Results.Text(
                NAServerMetadata.Serialize(document, string.Equals(format, "pjson", StringComparison.OrdinalIgnoreCase)),
                "application/json");
        }
        catch (NAServerParameterTranslation.NAServerParameterException exception)
        {
            return StandardErrorHelpers.CreateBadRequest(context, exception.Message);
        }
    }

    private static Dictionary<string, string> TranslateParameters(IReadOnlyDictionary<string, string> parameters)
    {
        var translated = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase);
        foreach (var key in translated.Keys)
        {
            if (!AllowedParameters.Contains(key))
            {
                throw new NAServerParameterTranslation.NAServerParameterException(
                    $"FindRoutes parameter '{key}' is not supported by this ordered-stop, minutes-only tool.");
            }
        }

        // Check every spelling independently so conflicting aliases cannot hide unsupported options.
        foreach (var key in new[] { "Measurement_Units", "measurementUnits" })
        {
            if (translated.TryGetValue(key, out var units) &&
                !units.Equals("Minutes", StringComparison.OrdinalIgnoreCase))
            {
                throw new NAServerParameterTranslation.NAServerParameterException(
                    "FindRoutes Measurement_Units supports only Minutes.");
            }
        }
        if (translated.TryGetValue("impedanceAttributeName", out var impedance) &&
            !impedance.Equals(NAServerMetadata.TimeAttributeName, StringComparison.OrdinalIgnoreCase))
        {
            throw new NAServerParameterTranslation.NAServerParameterException(
                "FindRoutes supports only the TravelTime impedance, reported in Minutes.");
        }

        foreach (var key in new[] { "Reorder_Stops_to_Find_Optimal_Routes", "Reorder_Stops_to_Find_Optimal_Route", "findBestSequence" })
        {
            if (translated.TryGetValue(key, out var value) && (!bool.TryParse(value, out var reorder) || reorder))
            {
                throw new NAServerParameterTranslation.NAServerParameterException(
                    "FindRoutes Reorder_Stops_to_Find_Optimal_Routes supports only false; stops are visited in input order.");
            }
        }

        foreach (var (source, target) in new[]
                 {
                     ("Travel_Mode", "travelMode"),
                     ("Point_Barriers", "barriers"),
                     ("Line_Barriers", "polylineBarriers"),
                     ("Polygon_Barriers", "polygonBarriers"),
                 })
        {
            if (!translated.TryGetValue(source, out var value))
            {
                continue;
            }
            if (translated.TryGetValue(target, out var other) && !string.Equals(value, other, StringComparison.Ordinal))
            {
                throw new NAServerParameterTranslation.NAServerParameterException(
                    $"FindRoutes parameters '{source}' and '{target}' conflict.");
            }
            translated[target] = value;
        }

        return translated;
    }
}

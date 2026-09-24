using Honua.Protocols.GeoServices.NAServer.Models;
using Honua.Infrastructure.Models;
using System.Text.Json;
using System.Text.Json.Nodes;
// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Protocols.GeoServices.NAServer;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.GeoServices.GPServer;

/// <summary>
/// The NetworkAnalysisUtilities tasks (<c>GetTravelModes</c>, <c>GetToolInfo</c>) every
/// GPServer resolves by name beside its process-catalog tasks (#5035).
/// </summary>
/// <remarks>
/// ArcGIS Pro and <c>arcpy.nax</c> take a stand-alone routing service as
/// <c>{url: .../NAServer, utilityUrl: .../GPServer}</c> and execute these two tasks on
/// the utility service before any solve; without them the dictionary is refused
/// client-side. They are synchronous, read-only projections of the routing provider,
/// so they neither enter the job runtime nor require the job authorization the
/// catalog tasks do: anonymous, like the NAServer solves they describe.
/// </remarks>
internal static partial class GPServerEndpoints
{
    private const string NetworkAnalysisUtilitiesContentType = "application/json";

    private static bool IsPrettyJsonRequest(HttpContext context, IReadOnlyDictionary<string, string> parameters)
    {
        var format = parameters.TryGetValue("f", out var parameterFormat)
            ? parameterFormat
            : context.Request.Query["f"].ToString();
        return format.Trim().Equals("pjson", StringComparison.OrdinalIgnoreCase);
    }

    private static IResult HandleNetworkAnalysisUtilityTaskInfo(HttpContext context, string taskName)
    {
        var pretty = context.Request.Query["f"].ToString().Trim().Equals("pjson", StringComparison.OrdinalIgnoreCase);
        return Results.Text(
            NAServerMetadata.Serialize(NAServerMetadata.BuildUtilityTaskInfo(taskName), pretty),
            NetworkAnalysisUtilitiesContentType);
    }

    private static async Task<IResult> HandleNetworkAnalysisUtilityExecuteAsync(
        HttpContext context,
        string taskName,
        CancellationToken ct)
    {
        var parameters = await GPServerParameterTranslation.ReadRequestParametersAsync(context, ct);
        var formatError = ValidateJsonFormat(context, parameters);
        if (formatError is not null)
        {
            return formatError;
        }

        var routing = context.RequestServices.GetRequiredService<IRoutingProvider>();
        var datasets = context.RequestServices.GetRequiredService<INetworkDatasetResolver>();
        var configuration = context.RequestServices.GetRequiredService<IOptions<RoutingConfiguration>>().Value;
        var dataset = await NAServerEndpoints.ResolveDatasetAsync(datasets, configuration, ct).ConfigureAwait(false);
        var pretty = IsPrettyJsonRequest(context, parameters);
        var capabilities = await routing.GetCapabilitiesAsync(ct).ConfigureAwait(false);

        if (taskName.Equals(NAServerMetadata.FindRoutesTask, StringComparison.OrdinalIgnoreCase))
        {
            return await HandleFindRoutesExecuteAsync(
                context, parameters, routing, configuration, capabilities, pretty, ct).ConfigureAwait(false);
        }

        if (taskName.Equals(NAServerMetadata.GetTravelModesTask, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Text(
                NAServerMetadata.Serialize(NAServerMetadata.BuildGetTravelModesResult(dataset, capabilities), pretty),
                NetworkAnalysisUtilitiesContentType);
        }

        // GetToolInfo always answers with a toolInfo document: ArcGIS Pro's native reader
        // dereferences an error envelope here. includeNetworkSourceInfo is accepted and
        // ignored, matching the ArcGIS Enterprise reference response.
        parameters.TryGetValue("serviceName", out var serviceName);
        parameters.TryGetValue("toolName", out var toolName);
        var document = NAServerMetadata.BuildGetToolInfoResult(serviceName, toolName, capabilities, configuration);
        return Results.Text(NAServerMetadata.Serialize(document, pretty), NetworkAnalysisUtilitiesContentType);
    }

    /// <summary>
    /// <c>FindRoutes</c>: Esri's ready-to-use routing tool, projected onto the NAServer
    /// route solve (#5192).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately a translation in front of the existing solve rather than a second
    /// solver. The only difference between this and <c>NAServer/Route/solve</c> is the
    /// spelling of the parameters: Esri's ready-to-use tool names them <c>Stops</c>,
    /// <c>Travel_Mode</c> and <c>Reorder_Stops_to_Find_Optimal_Route</c> where NAServer
    /// names them <c>stops</c>, <c>travelMode</c> and <c>findBestSequence</c>. Mapping
    /// the names and delegating keeps one implementation, so the two surfaces cannot
    /// answer differently for the same stops - which is what the certification oracle
    /// compares.
    /// </para>
    /// <para>
    /// Synchronous, matching the task resource: there is no job to poll, so the result
    /// is returned in the execute envelope rather than through submitJob.
    /// </para>
    /// </remarks>
    private static async Task<IResult> HandleFindRoutesExecuteAsync(
        HttpContext context,
        IReadOnlyDictionary<string, string> parameters,
        IRoutingProvider routing,
        RoutingConfiguration configuration,
        RoutingProviderCapabilities capabilities,
        bool pretty,
        CancellationToken ct)
    {
        if (!capabilities.SupportsRoute)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context, "Route solves are not supported by the configured routing provider.");
        }

        // Esri's ready-to-use parameter names, mapped onto the NAServer spelling. Only
        // names are translated; values are passed through untouched so the same
        // validation and caps apply.
        var translated = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase);
        foreach (var (esri, naserver) in new[]
                 {
                     ("Stops", "stops"),
                     ("Travel_Mode", "travelMode"),
                     ("Reorder_Stops_to_Find_Optimal_Route", "findBestSequence"),
                     ("Measurement_Units", "impedanceAttributeName"),
                 })
        {
            if (translated.TryGetValue(esri, out var value) && !translated.ContainsKey(naserver))
            {
                translated[naserver] = value;
            }
        }

        try
        {
            var caps = NAServerInputCaps.FromConfiguration(configuration);
            var request = NAServerParameterTranslation.BuildRouteSolveRequest(translated, caps);
            var result = await routing.SolveRouteAsync(request, ct).ConfigureAwait(false);
            var solved = NAServerResultMapping.MapRoute(
                result, request.OutSrid, includeRoutes: true, includeDirections: false);

            var routes = JsonSerializer.SerializeToNode(
                solved.Routes, NAServerJsonContext.Default.NAServerRouteFeatureSet);
            var document = new JsonObject
            {
                ["results"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["paramName"] = "Output_Routes",
                        ["dataType"] = "GPFeatureRecordSetLayer",
                        ["value"] = routes,
                    },
                    new JsonObject
                    {
                        ["paramName"] = "Solve_Succeeded",
                        ["dataType"] = "GPBoolean",
                        ["value"] = solved.Routes?.Features is { Length: > 0 },
                    },
                },
                ["messages"] = new JsonArray(),
            };
            return Results.Text(
                NAServerMetadata.Serialize(document, pretty), NetworkAnalysisUtilitiesContentType);
        }
        catch (NAServerParameterTranslation.NAServerParameterException)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context, "Invalid FindRoutes parameters: Stops requires at least two locations.");
        }
    }
}

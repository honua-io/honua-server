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

        if (taskName.Equals(NAServerMetadata.GetTravelModesTask, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Text(
                NAServerMetadata.Serialize(NAServerMetadata.BuildGetTravelModesResult(dataset), pretty),
                NetworkAnalysisUtilitiesContentType);
        }

        // GetToolInfo always answers with a toolInfo document: ArcGIS Pro's native reader
        // dereferences an error envelope here. includeNetworkSourceInfo is accepted and
        // ignored, matching the ArcGIS Enterprise reference response.
        parameters.TryGetValue("serviceName", out var serviceName);
        parameters.TryGetValue("toolName", out var toolName);
        var capabilities = await routing.GetCapabilitiesAsync(ct).ConfigureAwait(false);
        var document = NAServerMetadata.BuildGetToolInfoResult(serviceName, toolName, capabilities, configuration);
        return Results.Text(NAServerMetadata.Serialize(document, pretty), NetworkAnalysisUtilitiesContentType);
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Infrastructure.Models;
using Honua.Protocols.GeoServices.GPServer.Models;
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

    private static bool IsRoutingUtilityService(string serviceId)
        => serviceId.Equals("Routing", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> HasRoutingCatalogServiceAsync(HttpContext context, CancellationToken ct)
    {
        // Check the unfiltered canonical graph, so a hidden tenant's or protocol-
        // disabled publication cannot be mistaken for an absent service.
        var provider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await provider.GetCurrentAsync(ct).ConfigureAwait(false);
        return snapshot.Graph.Services.Any(service => IsRoutingUtilityService(service.Metadata.Name));
    }

    private static async Task<IResult?> ValidateRoutingUtilityTaskAsync(HttpContext context, string taskName, CancellationToken ct)
    {
        if (!IsRoutingUtilityService(context.Request.RouteValues["serviceId"]?.ToString() ?? string.Empty))
        {
            return null;
        }

        if (await HasRoutingCatalogServiceAsync(context, ct).ConfigureAwait(false))
        {
            var validation = await ValidateServiceAsync(context,
                context.Request.RouteValues["serviceId"]?.ToString() ?? string.Empty,
                ResolveLogger(context), ct).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                return validation.ErrorResult!;
            }
        }

        var routing = context.RequestServices.GetRequiredService<IRoutingProvider>();
        var capabilities = await routing.GetCapabilitiesAsync(ct).ConfigureAwait(false);
        var supported = taskName.Equals(NAServerMetadata.FindRoutesTask, StringComparison.OrdinalIgnoreCase)
            ? capabilities.SupportsRoute
            : taskName.Equals(NAServerMetadata.GenerateServiceAreasTask, StringComparison.OrdinalIgnoreCase)
                ? capabilities.SupportsServiceArea
                : capabilities.SupportsRoute || capabilities.SupportsServiceArea;
        return supported ? null : StandardErrorHelpers.CreateNotFound(context,
            $"Routing task '{taskName}' is not supported by the configured routing provider.");
    }

    private static async Task<IResult> HandleRoutingUtilityServiceInfoAsync(HttpContext context, CancellationToken ct)
    {
        var routing = context.RequestServices.GetRequiredService<IRoutingProvider>();
        var capabilities = await routing.GetCapabilitiesAsync(ct).ConfigureAwait(false);
        var tasks = new List<string>();
        if (capabilities.SupportsRoute)
        {
            tasks.Add(NAServerMetadata.FindRoutesTask);
        }

        if (capabilities.SupportsServiceArea)
        {
            tasks.Add(NAServerMetadata.GenerateServiceAreasTask);
        }

        if (tasks.Count == 0)
        {
            return StandardErrorHelpers.CreateNotFound(context, "The configured routing provider does not support synchronous routing tools.");
        }

        tasks.Add(NAServerMetadata.GetTravelModesTask);
        tasks.Add(NAServerMetadata.GetToolInfoTask);

        // https://developers.arcgis.com/rest/services-reference/enterprise/gp-service/
        // These are REST synchronous tools, not the unrelated catalog's SOAP/job tasks.
        return Results.Json(new GPServiceInfoResponse
        {
            ServiceDescription = "Routing tools for the configured routing provider",
            ExecutionType = "esriExecutionTypeSynchronous",
            Capabilities = string.Empty,
            ResultMapServerName = string.Empty,
            Tasks = [.. tasks]
        }, GPServerJsonContext.Default.GPServiceInfoResponse, contentType: NetworkAnalysisUtilitiesContentType);
    }

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
        var routingError = await ValidateRoutingUtilityTaskAsync(context, taskName, ct).ConfigureAwait(false);
        if (routingError is not null)
        {
            return routingError;
        }

        if (taskName.Equals(NAServerMetadata.FindRoutesTask, StringComparison.OrdinalIgnoreCase))
        {
            return await FindRoutesWebTool.ExecuteAsync(context, ct).ConfigureAwait(false);
        }

        if (taskName.Equals(NAServerMetadata.GenerateServiceAreasTask, StringComparison.OrdinalIgnoreCase))
        {
            return await GenerateServiceAreasWebTool.ExecuteAsync(context, ct).ConfigureAwait(false);
        }

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

}

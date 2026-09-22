// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Infrastructure.Models;
using Honua.Protocols.GeoServices.GPServer.Models;
using Honua.Protocols.GeoServices.NAServer;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.GeoServices.GPServer;

/// <summary>
/// The NetworkAnalysisUtilities tasks (<c>GetTravelModes</c>, <c>GetToolInfo</c>) that
/// the dedicated synchronous routing GP service publishes (#5035).
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

    private static readonly object NetworkAnalysisUtilityRequestKey = new();

    internal static bool IsNetworkAnalysisUtilityRequest(HttpContext context)
        => context.Items.TryGetValue(NetworkAnalysisUtilityRequestKey, out var synthetic) && synthetic is true;

    internal static bool IsNetworkAnalysisUtilityService(string serviceId)
        => serviceId.Equals(NAServerMetadata.UtilityServiceId, StringComparison.OrdinalIgnoreCase)
            || serviceId.Equals(NAServerMetadata.PortalRoutingServiceId, StringComparison.OrdinalIgnoreCase);

    // Synthetic availability is separate from request resolution. A published
    // service always keeps its canonical identity, access policy and execution.
    // This gate controls only the anonymous fallback and portal advertisement.
    internal static async Task<IResult?> ValidateNetworkAnalysisUtilityServiceAsync(HttpContext context, CancellationToken ct)
    {
        var graph = context.RequestServices.GetService<IMetadataV2GraphProvider>();
        var routing = context.RequestServices.GetService<IRoutingProvider>();
        var datasets = context.RequestServices.GetService<INetworkDatasetResolver>();
        var options = context.RequestServices.GetService<IOptions<RoutingConfiguration>>();
        if (graph is null || routing is null || datasets is null || options is null)
        {
            return StandardErrorHelpers.CreateServiceUnavailable(context, "Routing utilities are unavailable.", retryable: true);
        }
        try
        {
            var snapshot = await graph.GetCurrentAsync(ct).ConfigureAwait(false);
            if (snapshot.Index.ServicesById.Values.Any(service =>
                IsNetworkAnalysisUtilityService(service.Metadata.Id) || IsNetworkAnalysisUtilityService(service.Metadata.Name)))
            {
                return StandardErrorHelpers.CreateNotFound(context, "Routing utilities are unavailable.");
            }
            return await ValidateNetworkAnalysisUtilityDependenciesAsync(context, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            return StandardErrorHelpers.CreateServiceUnavailable(context, "Routing utilities are unavailable.", retryable: true);
        }
    }

    // Ordinary callers reach this only after service authorization and canonical task classification.
    // Ordinary compatibility aliases must not inherit the synthetic-name collision gate.
    private static async Task<IResult?> ValidateNetworkAnalysisUtilityDependenciesAsync(HttpContext context, CancellationToken ct)
    {
        var routing = context.RequestServices.GetService<IRoutingProvider>();
        var datasets = context.RequestServices.GetService<INetworkDatasetResolver>();
        var options = context.RequestServices.GetService<IOptions<RoutingConfiguration>>();
        if (routing is null || datasets is null || options is null)
        {
            return StandardErrorHelpers.CreateServiceUnavailable(context, "Routing utilities are unavailable.", retryable: true);
        }
        try
        {
            _ = await routing.GetCapabilitiesAsync(ct).ConfigureAwait(false);
            var dataset = await NAServerEndpoints.ResolveDatasetAsync(datasets, options.Value, ct).ConfigureAwait(false);
            if (dataset.TravelProfiles.Count == 0)
            {
                return StandardErrorHelpers.CreateServiceUnavailable(context, "Routing utilities are unavailable.", retryable: true);
            }
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            return StandardErrorHelpers.CreateServiceUnavailable(context, "Routing utilities are unavailable.", retryable: true);
        }
    }

    private static async Task<(bool Synthetic, IResult? Error)> ClassifyNetworkAnalysisUtilityRequestAsync(
        HttpContext context, string serviceId, CancellationToken ct)
    {
        if (!IsNetworkAnalysisUtilityService(serviceId))
        {
            return (false, null);
        }
        var graph = context.RequestServices.GetService<IMetadataV2GraphProvider>();
        if (graph is null)
        {
            return (false, StandardErrorHelpers.CreateServiceUnavailable(context, "Service catalog is unavailable.", retryable: true));
        }
        try
        {
            var snapshot = await graph.GetCurrentAsync(ct).ConfigureAwait(false);
            // Include disabled/non-routable/protocol-disabled entries here: those
            // are canonical denials, never an excuse to expose synthetic compute.
            if (snapshot.Index.ServicesById.Values.Any(service =>
                string.Equals(service.Metadata.Id, serviceId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(service.Metadata.Name, serviceId, StringComparison.OrdinalIgnoreCase)))
            {
                return (false, null);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            return (false, StandardErrorHelpers.CreateServiceUnavailable(context, "Service catalog is unavailable.", retryable: true));
        }
        var error = await ValidateNetworkAnalysisUtilityServiceAsync(context, ct).ConfigureAwait(false);
        return (error is null, error);
    }

    internal static GPTaskInfoResponse BuildNetworkAnalysisUtilityTaskInfo(string taskName)
        => NAServerMetadata.BuildUtilityTaskInfo(taskName).Deserialize(GPServerJsonContext.Default.GPTaskInfoResponse)
            ?? throw new InvalidOperationException("Routing utility metadata could not be projected.");

    internal static bool IsNetworkAnalysisUtilityTask(Honua.Core.Features.Geoprocessing.Abstractions.IProcessCatalog catalog, bool syntheticUtility, string taskName)
        => NAServerMetadata.IsUtilityTask(taskName) && (syntheticUtility
            || !catalog.ListProcesses().Any(process => string.Equals(process.ProcessId, taskName, StringComparison.OrdinalIgnoreCase)));

    internal static IEnumerable<string> BuildServiceTaskNames(Honua.Core.Features.Geoprocessing.Abstractions.IProcessCatalog catalog, bool syntheticUtility)
        => syntheticUtility ? NAServerMetadata.UtilityTaskNames : BuildPublishedTaskNames(catalog);

    internal static GPTaskInfoResponse? ResolveServiceTaskInfo(Honua.Core.Features.Geoprocessing.Abstractions.IProcessCatalog catalog, bool syntheticUtility, string taskName)
    {
        if (syntheticUtility)
        {
            return NAServerMetadata.UtilityTaskNames.Contains(taskName, StringComparer.Ordinal)
                ? BuildNetworkAnalysisUtilityTaskInfo(taskName) : null;
        }
        var definition = ResolveTaskDefinition(catalog, taskName);
        return definition is not null && GPServerExecutionPolicy.IsJobCallable(definition) ? BuildTaskInfo(taskName, definition) : null;
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
        CancellationToken ct,
        Func<IReadOnlyDictionary<string, string>>? readSoapParameters = null)
    {
        if (!IsNetworkAnalysisUtilityRequest(context))
        {
            var availabilityError = await ValidateNetworkAnalysisUtilityDependenciesAsync(context, ct).ConfigureAwait(false);
            if (availabilityError is not null)
            {
                return availabilityError;
            }
        }
        var parameters = readSoapParameters is null
            ? await GPServerParameterTranslation.ReadRequestParametersAsync(context, ct)
            : readSoapParameters();
        var formatError = ValidateJsonFormat(context, parameters);
        if (formatError is not null)
        {
            return formatError;
        }

        if (parameters.Keys.Any(key => key.StartsWith("env:", StringComparison.OrdinalIgnoreCase)))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Routing utility metadata does not support geoprocessing environment overrides.");
        }
        var document = await ExecuteNetworkAnalysisUtilityAsync(context, taskName, parameters, ct).ConfigureAwait(false);
        if (document is null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "The routing utility tool is not supported.");
        }
        if (readSoapParameters is not null)
        {
            var typed = document.Deserialize(GPServerJsonContext.Default.GPExecuteResponse)
                ?? throw new InvalidOperationException("Routing utility result could not be projected.");
            return Results.Json(typed, GPServerJsonContext.Default.GPExecuteResponse, contentType: NetworkAnalysisUtilitiesContentType);
        }
        return Results.Text(NAServerMetadata.Serialize(document, IsPrettyJsonRequest(context, parameters)), NetworkAnalysisUtilitiesContentType);
    }

    private static async Task<JsonObject?> ExecuteNetworkAnalysisUtilityAsync(
        HttpContext context, string taskName, IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        var routing = context.RequestServices.GetRequiredService<IRoutingProvider>();
        var datasets = context.RequestServices.GetRequiredService<INetworkDatasetResolver>();
        var configuration = context.RequestServices.GetRequiredService<IOptions<RoutingConfiguration>>().Value;
        var dataset = await NAServerEndpoints.ResolveDatasetAsync(datasets, configuration, ct).ConfigureAwait(false);

        if (taskName.Equals(NAServerMetadata.GetTravelModesTask, StringComparison.OrdinalIgnoreCase))
        {
            return NAServerMetadata.BuildGetTravelModesResult(dataset);
        }

        parameters.TryGetValue("serviceName", out var serviceName);
        parameters.TryGetValue("toolName", out var toolName);
        var includeSources = parameters.TryGetValue("includeNetworkSourceInfo", out var include)
            && (include.Equals("true", StringComparison.OrdinalIgnoreCase) || include == "1");
        var capabilities = await routing.GetCapabilitiesAsync(ct).ConfigureAwait(false);
        return NAServerMetadata.BuildGetToolInfoResult(
            serviceName, toolName, capabilities, dataset, configuration, includeSources);
    }
}

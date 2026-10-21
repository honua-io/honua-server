// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml.Linq;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Geoprocessing;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Protocols.GeoServices.GPServer.Models;
using Honua.Routing.Features.Routing.Abstractions;
using static Honua.Protocols.GeoServices.Soap.ArcGisSoapProtocol;

namespace Honua.Protocols.GeoServices.GPServer;

internal static partial class GPServerEndpoints
{
    internal const string AsyncRouteServiceId = "RoutingRoute";

    internal static bool IsAsyncRouteService(string serviceId)
        => serviceId.Equals(AsyncRouteServiceId, StringComparison.OrdinalIgnoreCase);

    internal static async Task<bool> CanPublishAsyncRouteAsync(HttpContext context, CancellationToken ct)
    {
        var catalog = context.RequestServices.GetService<IProcessCatalog>();
        var definition = catalog is null ? null : ResolveTaskDefinition(catalog, "FindRoutes");
        if (definition is null || definition.ProcessId != RoutingProcessDefinitions.Route || !GPServerExecutionPolicy.IsJobCallable(definition))
        {
            return false;
        }
        var graph = context.RequestServices.GetService<IMetadataV2GraphProvider>();
        if (graph is null)
        {
            return false;
        }
        var snapshot = await graph.GetCurrentAsync(ct).ConfigureAwait(false);
        return !snapshot.Graph.Services.Any(service =>
            string.Equals(service.Metadata.Id, AsyncRouteServiceId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(service.Metadata.Name, AsyncRouteServiceId, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<bool> IsVirtualAsyncRouteServiceAsync(HttpContext context, CancellationToken ct)
        => IsAsyncRouteService(context.Request.RouteValues["serviceId"]?.ToString() ?? string.Empty)
            && !await HasRoutingCatalogServiceAsync(context, ct).ConfigureAwait(false);

    private static async Task<ServiceResourceValidationHelpers.ServiceValidationV2Result> ValidateVirtualAsyncRouteAsync(
        HttpContext context, CancellationToken ct)
    {
        var task = context.Request.RouteValues["taskName"]?.ToString();
        var definition = ResolveTaskDefinition(context.RequestServices.GetRequiredService<IProcessCatalog>(), "FindRoutes");
        var capabilities = await context.RequestServices.GetRequiredService<IRoutingProvider>()
            .GetCapabilitiesAsync(ct).ConfigureAwait(false);
        return definition is not null && definition.ProcessId == RoutingProcessDefinitions.Route
            && GPServerExecutionPolicy.IsJobCallable(definition) && capabilities.SupportsRoute
            && (string.IsNullOrEmpty(task) || task.Equals("FindRoutes", StringComparison.OrdinalIgnoreCase))
            ? new(true, null, null)
            : new(false, null, StandardErrorHelpers.CreateNotFound(context, "The requested routing task is not available."));
    }

    private static IResult AsyncRouteServiceInfo()
        => Results.Json(new RoutingGPServiceInfoResponse
        {
            ServiceDescription = "Provider-backed ordered route analysis through the durable managed job runtime",
            ExecutionType = "esriExecutionTypeAsynchronous",
            Capabilities = string.Empty,
            ResultMapServerName = string.Empty,
            Tasks = ["FindRoutes"],
        }, GPServerJsonContext.Default.RoutingGPServiceInfoResponse, contentType: "application/json");

    private static async Task<IResult?> TryHandleAsyncRouteSoapAsync(
        HttpContext context, XElement operation, XNamespace soap, CancellationToken ct)
    {
        if (!await IsVirtualAsyncRouteServiceAsync(context, ct).ConfigureAwait(false))
        {
            return null;
        }
        var validation = await ValidateVirtualAsyncRouteAsync(context, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return CreateSoapFaultFromResult(validation.ErrorResult!, "Route analysis is not available.", soap);
        }
        var name = operation.Name.LocalName;
        if (GPServerSoapExecution.IsExecutionOperation(name))
        {
            // Submission, polling, result access and cancellation keep the canonical
            // authorizer, persisted service/task binding and publication fence.
            return await HandleSoapExecutionAsync(context, operation, soap, ct).ConfigureAwait(false);
        }
        var definition = ResolveTaskDefinition(context.RequestServices.GetRequiredService<IProcessCatalog>(), "FindRoutes");
        if (definition is null)
        {
            return CreateSoapFault("The route process is not registered.", StatusCodes.Status404NotFound, soap);
        }
        XElement result;
        if (name == "GetToolInfo")
        {
            var arguments = operation.Elements().ToArray();
            if (arguments.Length != 1 || arguments[0].Name != XName.Get("ToolName") || arguments[0].HasElements
                || arguments[0].Value != "FindRoutes")
            {
                return CreateSoapFault("GetToolInfo requires the FindRoutes ToolName.", StatusCodes.Status400BadRequest, soap);
            }
            result = GPServerSoapEndpoints.BuildToolInfo(BuildTaskInfo("FindRoutes", definition));
            result.Name = "Result";
        }
        else
        {
            if (operation.HasElements || !string.IsNullOrWhiteSpace(operation.Value))
            {
                return CreateSoapFault("This operation does not accept arguments.", StatusCodes.Status400BadRequest, soap);
            }
            result = name switch
            {
                "GetExecutionType" => new XElement("Result", "esriExecutionTypeAsynchronous"),
                "GetResultMapServerName" => new XElement("Result", string.Empty),
                "GetToolNames" or "GetTaskNames" => new XElement("Result", new XElement("String", "FindRoutes")),
                "GetToolInfos" or "GetTaskInfos" => new XElement("Result", GPServerSoapEndpoints.BuildToolInfo(BuildTaskInfo("FindRoutes", definition))),
                _ => new XElement("Unsupported"),
            };
            if (result.Name == "Unsupported")
            {
                return CreateSoapFault("The requested GP operation is not implemented.", StatusCodes.Status501NotImplemented, soap);
            }
        }
        return CreateSoapResponse(soap, operation.Name.Namespace, name + "Response", result);
    }
}

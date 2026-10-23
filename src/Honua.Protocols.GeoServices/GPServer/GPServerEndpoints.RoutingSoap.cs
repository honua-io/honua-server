// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Xml.Linq;
using Honua.Geoprocessing;
using Honua.Protocols.GeoServices.GPServer.Models;
using Honua.Protocols.GeoServices.NAServer;
using Honua.Routing.Features.Routing.Abstractions;
using Microsoft.AspNetCore.Http.HttpResults;
using static Honua.Protocols.GeoServices.Soap.ArcGisSoapProtocol;

namespace Honua.Protocols.GeoServices.GPServer;

internal static partial class GPServerEndpoints
{
    /// <summary>Projects the provider-level Routing service without changing catalog GP services.</summary>
    internal static async Task<IResult?> TryHandleRoutingSoapAsync(
        HttpContext context, XElement operation, XNamespace soap, CancellationToken ct)
    {
        var serviceId = context.Request.RouteValues["serviceId"]?.ToString() ?? string.Empty;
        if (!IsRoutingUtilityService(serviceId) || await HasRoutingCatalogServiceAsync(context, ct).ConfigureAwait(false))
        {
            // A real publication, including a hidden or protocol-disabled collision,
            // must retain the ordinary catalog's service and tenant authorization.
            return null;
        }

        var provider = context.RequestServices.GetRequiredService<IRoutingProvider>();
        var tasks = BuildRoutingUtilityTaskNames(await provider.GetCapabilitiesAsync(ct).ConfigureAwait(false));
        if (tasks.Length == 0)
        {
            return CreateSoapFault("The configured routing provider does not support this service.", StatusCodes.Status404NotFound, soap);
        }

        var name = operation.Name.LocalName;
        try
        {
            XElement result;
            switch (name)
            {
                case "GetToolInfos":
                case "GetTaskInfos":
                    if (HasRoutingSoapArguments(operation))
                    {
                        return CreateSoapFault("This operation does not accept arguments.", StatusCodes.Status400BadRequest, soap);
                    }
                    result = new XElement("Result", tasks.Select(task => GPServerSoapEndpoints.BuildToolInfo(BuildRoutingSoapTaskInfo(task))));
                    break;
                case "GetToolNames":
                case "GetTaskNames":
                    if (HasRoutingSoapArguments(operation))
                    {
                        return CreateSoapFault("This operation does not accept arguments.", StatusCodes.Status400BadRequest, soap);
                    }
                    result = new XElement("Result", tasks.Select(task => new XElement("String", task)));
                    break;
                case "GetExecutionType":
                case "GetResultMapServerName":
                    if (HasRoutingSoapArguments(operation))
                    {
                        return CreateSoapFault("This operation does not accept arguments.", StatusCodes.Status400BadRequest, soap);
                    }
                    result = new XElement("Result", name == "GetExecutionType" ? RoutingUtilityExecutionType : string.Empty);
                    break;
                case "GetToolInfo":
                    var arguments = operation.Elements().ToArray();
                    if (arguments.Length != 1 || arguments[0].Name != XName.Get("ToolName") || arguments[0].HasElements)
                    {
                        return CreateSoapFault("GetToolInfo requires one ToolName argument.", StatusCodes.Status400BadRequest, soap);
                    }
                    if (!tasks.Contains(arguments[0].Value, StringComparer.Ordinal))
                    {
                        return CreateSoapFault("The requested task was not found.", StatusCodes.Status404NotFound, soap);
                    }
                    result = GPServerSoapEndpoints.BuildToolInfo(BuildRoutingSoapTaskInfo(arguments[0].Value));
                    result.Name = "Result";
                    break;
                case "Execute":
                    var taskName = operation.Element("ToolName")?.Value ?? string.Empty;
                    if (!tasks.Contains(taskName, StringComparer.Ordinal))
                    {
                        return CreateSoapFault("The requested task was not found.", StatusCodes.Status404NotFound, soap);
                    }
                    var taskDefinition = BuildRoutingSoapTaskInfo(taskName);
                    var response = await HandleNetworkAnalysisUtilityExecuteAsync(context, taskName, ct,
                        () => GPServerSoapExecution.ReadSubmission(operation, taskDefinition)).ConfigureAwait(false);
                    if (response is not ContentHttpResult { ResponseContent: { } json } content || content.StatusCode is >= 400)
                    {
                        return CreateSoapFaultFromResult(response, "The routing operation could not be completed.", soap);
                    }
                    var executed = JsonSerializer.Deserialize(json, GPServerJsonContext.Default.GPExecuteResponse)
                        ?? throw new InvalidOperationException("The routing result could not be projected.");
                    result = GPServerSoapExecution.BuildResult(executed.Results ?? [], executed.Messages ?? []);
                    break;
                default:
                    return CreateSoapFault("The requested operation is not supported by the synchronous routing service.",
                        StatusCodes.Status501NotImplemented, soap);
            }

            return CreateSoapResponse(soap, operation.Name.Namespace, name + "Response", result);
        }
        catch (GeoprocessingValidationException error)
        {
            return CreateSoapFault(error.Message, StatusCodes.Status400BadRequest, soap);
        }
    }

    private static bool HasRoutingSoapArguments(XElement operation)
        => operation.HasElements || !string.IsNullOrWhiteSpace(operation.Value);

    private static GPTaskInfoResponse BuildRoutingSoapTaskInfo(string taskName)
        => NAServerMetadata.BuildUtilityTaskInfo(taskName).Deserialize(GPServerJsonContext.Default.GPTaskInfoResponse)
           ?? throw new InvalidOperationException("The routing task definition could not be projected.");
}

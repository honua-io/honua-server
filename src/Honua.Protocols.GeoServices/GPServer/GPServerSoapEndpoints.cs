// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Xml.Linq;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Infrastructure.Helpers;
using Honua.Protocols.GeoServices.GPServer.Models;
using Honua.ServiceDefaults;
using static Honua.Protocols.GeoServices.Soap.ArcGisSoapProtocol;

namespace Honua.Protocols.GeoServices.GPServer;

/// <summary>
/// ArcGIS SOAP discovery adapter over the same authorized process catalog as
/// GPServer REST. Execution must use the canonical job runtime.
/// </summary>
internal static class GPServerSoapEndpoints
{
    private static readonly HashSet<string> PythonKeywords = new(
        "False None True and as assert async await break class continue def del elif else except finally for from global if import in is lambda nonlocal not or pass raise return try while with yield".Split(' '),
        StringComparer.Ordinal);

    internal static void MapGPServerSoapEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/services/{serviceId}/GPServer", HandleRequestAsync)
            .WithName("ArcGisSoapGPServer")
            .WithDisplayName("ArcGIS SOAP GPServer")
            .WithSummary("Discover GPServer tasks through ArcGIS SOAP")
            .WithTags("GPServer")
            .Produces(StatusCodes.Status200OK, contentType: "text/xml", additionalContentTypes: ["application/soap+xml"])
            .Produces(StatusCodes.Status400BadRequest, contentType: "text/xml", additionalContentTypes: ["application/soap+xml"])
            .Produces(StatusCodes.Status401Unauthorized, contentType: "text/xml", additionalContentTypes: ["application/soap+xml"])
            .Produces(StatusCodes.Status403Forbidden, contentType: "text/xml", additionalContentTypes: ["application/soap+xml"])
            .Produces(StatusCodes.Status404NotFound, contentType: "text/xml", additionalContentTypes: ["application/soap+xml"])
            .Produces(StatusCodes.Status415UnsupportedMediaType, contentType: "text/xml", additionalContentTypes: ["application/soap+xml"])
            .Produces(StatusCodes.Status500InternalServerError, contentType: "text/xml", additionalContentTypes: ["application/soap+xml"])
            .Produces(StatusCodes.Status501NotImplemented, contentType: "text/xml", additionalContentTypes: ["application/soap+xml"])
            .Produces(StatusCodes.Status503ServiceUnavailable, contentType: "text/xml", additionalContentTypes: ["application/soap+xml"])
            // Service access is enforced by the same helper used by REST metadata.
            .AllowAnonymous();
    }

    private static async Task<IResult> HandleRequestAsync(string serviceId, HttpContext context)
    {
        var request = await TryReadSoapRequestAsync(context).ConfigureAwait(false);
        if (request.ErrorResult is not null)
        {
            return request.ErrorResult;
        }

        var operation = request.Operation!;
        var soap = request.SoapNamespace!;
        var name = operation.Name.LocalName;
        var ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(GPServerSoapEndpoints));
        using var scope = HonuaTelemetryScope.StartFeature($"soap-{name}", "GPServer", "*", context.TraceIdentifier);
        scope.WithTag(HonuaTelemetry.Tags.ServiceId, serviceId)
            .WithTag(HonuaTelemetry.Tags.Operation, name);
        try
        {
            var validation = await GPServerEndpoints.ValidateServiceAsync(context, serviceId, logger, ct).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                return Complete(scope, CreateSoapFaultFromResult(validation.ErrorResult!, "GP service was not found or is not accessible.", soap));
            }

            var catalog = context.RequestServices.GetRequiredService<IProcessCatalog>();
            XElement result;
            switch (name)
            {
                case "GetToolInfos":
                case "GetTaskInfos":
                    if (operation.HasElements || !string.IsNullOrWhiteSpace(operation.Value))
                    {
                        return Complete(scope, CreateSoapFault("This operation does not accept arguments.", StatusCodes.Status400BadRequest, soap));
                    }
                    result = new XElement("Result", GPServerEndpoints.BuildPublishedTaskNames(catalog)
                        .Select(task => BuildToolInfo(GPServerEndpoints.BuildTaskInfo(task, GPServerEndpoints.ResolveTaskDefinition(catalog, task)!))));
                    break;
                case "GetToolNames":
                case "GetTaskNames":
                    if (operation.HasElements || !string.IsNullOrWhiteSpace(operation.Value))
                    {
                        return Complete(scope, CreateSoapFault("This operation does not accept arguments.", StatusCodes.Status400BadRequest, soap));
                    }
                    result = new XElement("Result", GPServerEndpoints.BuildPublishedTaskNames(catalog).Select(task => new XElement("String", ToSoapToolName(task))));
                    break;
                case "GetToolInfo":
                    var arguments = operation.Elements().ToArray();
                    if (arguments.Length != 1 || arguments[0].Name != XName.Get("ToolName") || arguments[0].HasElements)
                    {
                        return Complete(scope, CreateSoapFault("GetToolInfo requires one ToolName argument.", StatusCodes.Status400BadRequest, soap));
                    }
                    var taskName = arguments[0].Value;
                    var canonicalName = GPServerEndpoints.BuildPublishedTaskNames(catalog)
                        .FirstOrDefault(candidate => string.Equals(ToSoapToolName(candidate), taskName, StringComparison.Ordinal));
                    var definition = canonicalName is null ? null : GPServerEndpoints.ResolveTaskDefinition(catalog, canonicalName);
                    if (definition is null || !GPServerExecutionPolicy.IsJobCallable(definition))
                    {
                        return Complete(scope, CreateSoapFault("The requested task was not found.", StatusCodes.Status404NotFound, soap));
                    }
                    result = BuildToolInfo(GPServerEndpoints.BuildTaskInfo(canonicalName!, definition));
                    result.Name = "Result";
                    break;
                case "GetExecutionType":
                case "GetResultMapServerName":
                    if (operation.HasElements || !string.IsNullOrWhiteSpace(operation.Value))
                    {
                        return Complete(scope, CreateSoapFault("This operation does not accept arguments.", StatusCodes.Status400BadRequest, soap));
                    }
                    result = new XElement("Result", name == "GetExecutionType" ? "esriExecutionTypeAsynchronous" : string.Empty);
                    break;
                default:
                    return Complete(scope, CreateSoapFault("The requested GPServer SOAP operation is not implemented.", StatusCodes.Status501NotImplemented, soap));
            }

            return Complete(scope, CreateSoapResponse(soap, operation.Name.Namespace, name + "Response", result));
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            scope.RecordException(exception);
            GPServerLog.SoapOperationFailed(logger, serviceId, name, exception);
            return Complete(scope, CreateSoapFault("The GPServer operation could not be completed.",
                exception is OperationCanceledException ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError, soap));
        }
    }

    private static XElement BuildToolInfo(GPTaskInfoResponse task)
        => new("GPToolInfo",
            new XElement("Name", ToSoapToolName(task.Name)),
            new XElement("DisplayName", task.DisplayName),
            new XElement("Category", task.Category),
            new XElement("Help", task.Description),
            new XElement("ParameterInfo", (task.Parameters ?? []).Select(parameter => new XElement("GPParameterInfo",
                new XElement("Name", parameter.Name),
                new XElement("DisplayName", parameter.DisplayName),
                new XElement("Category", string.Empty),
                new XElement("DataType", parameter.DataType),
                new XElement("Direction", parameter.Direction),
                new XElement("ParamType", parameter.ParameterType),
                parameter.ChoiceList is null ? null : new XElement("ChoiceList", parameter.ChoiceList.Select(value => new XElement("String", value))),
                BuildDefaultValue(parameter)))));

    private static string ToSoapToolName(string name)
    {
        // ArcPy builds Python function declarations directly from SOAP Name.
        // Keep conventional aliases such as Buffer; encode canonical ids with
        // punctuation without collisions. The underscore reserves the encoded
        // namespace because an unencoded name contains only letters/digits.
        return name.Length > 0 && char.IsAsciiLetter(name[0]) && name.All(char.IsAsciiLetterOrDigit) && !PythonKeywords.Contains(name)
            ? name
            : "Honua_" + Convert.ToHexString(Encoding.UTF8.GetBytes(name));
    }

    private static XElement? BuildDefaultValue(GPParameterInfo parameter)
    {
        if (parameter.DefaultValue is null)
        {
            return null;
        }

        // Scalar defaults are typed GPValue instances, not REST defaultValue text.
        // Complex defaults need their own wire mapping before being advertised.
        return parameter.DataType is "GPString" or "GPLong" or "GPDouble" or "GPBoolean" or "GPDate"
            ? new XElement("Value", new XAttribute(XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"), "tns:" + parameter.DataType),
                new XElement("Value", parameter.DefaultValue))
            : null;
    }

    private static IResult Complete(HonuaTelemetryScope scope, IResult result)
    {
        var status = (result as IStatusCodeHttpResult)?.StatusCode ?? StatusCodes.Status200OK;
        scope.WithTag("http.response.status_code", status);
        if (status < 400)
        {
            scope.SetSuccess(1);
        }
        else
        {
            scope.SetError();
        }
        return result;
    }
}

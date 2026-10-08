// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Xml.Linq;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Progress;
using Honua.Protocols.GeoServices;
using Honua.Protocols.GeoServices.Catalog;
using Honua.Protocols.GeoServices.GPServer;
using Honua.Protocols.GeoServices.GPServer.Models;
using Honua.Server.Features.PrintingTools.Models;
using static Honua.Protocols.GeoServices.Soap.ArcGisSoapProtocol;

namespace Honua.Server.Features.PrintingTools;

internal static partial class PrintingToolsEndpoints
{
    internal const string SoapParametersItemKey = "Honua.PrintingTools.SoapParameters";
    internal const string ForceJsonResultItemKey = "Honua.PrintingTools.ForceJsonResult";

    private static async Task<IResult> HandleGpServiceInfo(HttpContext context, CancellationToken cancellationToken)
    {
        cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var format = await GeoServicesRequestValueHelpers.ReadFormValueOrDefaultAsync(
            context.Request, "f", context.Request.Query["f"].FirstOrDefault(), cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(format)
            && !format.Equals("json", StringComparison.OrdinalIgnoreCase)
            && !format.Equals("pjson", StringComparison.OrdinalIgnoreCase))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Output format must be json or pjson.");
        }

        var response = new GPServiceInfoResponse
        {
            ServiceDescription = $"Geoprocessing service for {PrintingToolsServiceCatalog.QualifiedName}",
            Tasks =
            [
                PrintingToolsServiceCatalog.ExportTaskName,
                PrintingToolsServiceCatalog.LayoutTaskName
            ],
            ExecutionType = PrintingToolsServiceCatalog.ExecutionType,
            Capabilities = "",
            ResultMapServerName = "",
            MaximumRecords = 1000
        };
        return Results.Json(response, GPServerJsonContext.Default.GPServiceInfoResponse, contentType: "application/json");
    }

    private static IResult HandleLayoutTaskInfo(HttpContext context)
    {
        var response = new PrintServiceInfoResponse
        {
            Name = PrintingToolsServiceCatalog.LayoutTaskName,
            DisplayName = "Get Layout Templates Info",
            Parameters =
            [
                new PrintServiceParameter
                {
                    Name = "Output_JSON",
                    DataType = "GPString",
                    DisplayName = "Output JSON",
                    Direction = "esriGPParameterDirectionOutput"
                }
            ]
        };
        return Results.Json(response, PrintingToolsJsonContext.Default.PrintServiceInfoResponse, contentType: "application/json");
    }

    private static async Task<IResult> HandleGpSoapAsync(HttpContext context)
    {
        var request = await TryReadSoapRequestAsync(context).ConfigureAwait(false);
        if (request.ErrorResult is not null)
        {
            return request.ErrorResult;
        }

        var operation = request.Operation!;
        var soap = request.SoapNamespace!;
        var name = operation.Name.LocalName;
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        try
        {
            if (GPServerSoapExecution.IsExecutionOperation(name))
            {
                return await HandleGpSoapExecutionAsync(context, operation, soap, cancellationToken).ConfigureAwait(false);
            }

            if ((operation.HasElements || !string.IsNullOrWhiteSpace(operation.Value)) && name != "GetToolInfo")
            {
                return CreateSoapFault("This operation does not accept arguments.", StatusCodes.Status400BadRequest, soap);
            }

            XElement result;
            switch (name)
            {
                case "GetToolInfos":
                case "GetTaskInfos":
                    result = new XElement("Result", BuildTasks(context).Select(GPServerSoapEndpoints.BuildToolInfo));
                    break;
                case "GetToolNames":
                case "GetTaskNames":
                    result = new XElement("Result", BuildTasks(context).Select(task => new XElement("String", task.Name)));
                    break;
                case "GetToolInfo":
                    var arguments = operation.Elements().ToArray();
                    if (arguments.FirstOrDefault(argument => argument.Name.LocalName != "ToolName") is { } unsupported)
                    {
                        return CreateSoapFault(
                            $"GetToolInfo does not accept the '{unsupported.Name.LocalName}' argument; its only argument is ToolName.",
                            StatusCodes.Status400BadRequest,
                            soap);
                    }

                    if (arguments.Length != 1 || arguments[0].HasElements)
                    {
                        return CreateSoapFault("GetToolInfo requires one ToolName argument.", StatusCodes.Status400BadRequest, soap);
                    }

                    var task = FindTask(context, arguments[0].Value);
                    if (task is null)
                    {
                        return CreateSoapFault("The requested task was not found.", StatusCodes.Status404NotFound, soap);
                    }

                    result = GPServerSoapEndpoints.BuildToolInfo(task);
                    result.Name = "Result";
                    break;
                case "GetExecutionType":
                    result = new XElement("Result", PrintingToolsServiceCatalog.ExecutionType);
                    break;
                case "GetResultMapServerName":
                    result = new XElement("Result", string.Empty);
                    break;
                default:
                    return CreateSoapFault(
                        "The requested GPServer SOAP operation is not implemented.",
                        StatusCodes.Status501NotImplemented,
                        soap);
            }

            return CreateSoapResponse(soap, operation.Name.Namespace, name + "Response", result);
        }
        catch (GeoprocessingValidationException exception)
        {
            return CreateSoapFault(exception.Message, StatusCodes.Status400BadRequest, soap);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return CreateSoapFault(
                "The GPServer operation could not be completed.",
                exception is OperationCanceledException
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status500InternalServerError,
                soap);
        }
    }

    private static async Task<IResult> HandleGpSoapExecutionAsync(
        HttpContext context,
        XElement operation,
        XNamespace soap,
        CancellationToken cancellationToken)
    {
        var name = operation.Name.LocalName;
        var originalRoutes = context.Request.RouteValues;
        context.Request.RouteValues = new RouteValueDictionary(originalRoutes);
        try
        {
            if (name is "SubmitJob" or "Execute")
            {
                var toolName = operation.Element("ToolName")?.Value ?? string.Empty;
                var task = FindTask(context, toolName);
                if (task is null)
                {
                    return CreateSoapFault("The requested task was not found.", StatusCodes.Status404NotFound, soap);
                }

                if (name == "SubmitJob" && toolName == PrintingToolsServiceCatalog.LayoutTaskName)
                {
                    return CreateSoapFault(
                        "Get Layout Templates Info Task executes synchronously. Use Execute.",
                        StatusCodes.Status400BadRequest,
                        soap);
                }

                var parameters = GPServerSoapExecution.ReadSubmission(operation, task);
                var environment = parameters.Keys.FirstOrDefault(key => key.StartsWith("env:", StringComparison.OrdinalIgnoreCase));
                if (environment is not null)
                {
                    throw new GeoprocessingValidationException(
                        $"PrintingTools does not support the '{environment}' environment setting.");
                }

                context.Items[SoapParametersItemKey] = parameters;
                if (name == "Execute" && toolName == PrintingToolsServiceCatalog.LayoutTaskName)
                {
                    var layout = await HandleGetLayoutTemplatesInfo(context, cancellationToken).ConfigureAwait(false);
                    if ((layout as IValueHttpResult)?.Value is not LayoutTemplatesInfoResponse info
                        || info.Results is not { Length: > 0 } results
                        || results[0].Value is not { } templates)
                    {
                        return FaultFromResult(layout, soap, "The GP operation failed.");
                    }

                    var json = JsonSerializer.Serialize(templates, PrintingToolsJsonContext.Default.EsriLayoutTemplateInfoArray);
                    var output = GPServerSoapExecution.BuildResult(
                        [new GPResultResponse { ParamName = "Output_JSON", DataType = "GPString", Value = json }],
                        ToGpMessages(info.Messages));
                    return CreateSoapResponse(soap, operation.Name.Namespace, name + "Response", output);
                }

                context.Items[ForceJsonResultItemKey] = true;
                var response = name == "SubmitJob"
                    ? await HandleSubmitJob(context, cancellationToken).ConfigureAwait(false)
                    : await HandleExecute(context, cancellationToken).ConfigureAwait(false);
                if ((response as IValueHttpResult)?.Value is PrintSubmitJobResponse submitted && submitted.JobId is not null)
                {
                    return CreateSoapResponse(soap, operation.Name.Namespace, name + "Response", new XElement("Result", submitted.JobId));
                }

                if ((response as IValueHttpResult)?.Value is PrintExecuteResponse executed
                    && executed.Results is { Length: > 0 } printed
                    && printed[0].Value?.Url is { Length: > 0 } url)
                {
                    var reference = UrlReference(Absolutize(context, url));
                    var output = GPServerSoapExecution.BuildResult(
                        [new GPResultResponse { ParamName = "Output_File", DataType = "GPDataFile", Value = reference }],
                        ToGpMessages(executed.Messages));
                    return CreateSoapResponse(soap, operation.Name.Namespace, name + "Response", output);
                }

                return FaultFromResult(response, soap, "The GP operation failed.");
            }

            var jobId = GPServerSoapExecution.ReadJobId(operation);
            context.Request.RouteValues["jobId"] = jobId;
            if (name == "CancelJob")
            {
                return await HandleGpSoapCancelAsync(context, operation, soap, jobId, cancellationToken).ConfigureAwait(false);
            }

            var statusResponse = await HandleJobStatus(context, cancellationToken).ConfigureAwait(false);
            if ((statusResponse as IValueHttpResult)?.Value is not PrintJobStatusResponse status)
            {
                return FaultFromResult(statusResponse, soap, "The GP operation failed.");
            }

            XElement result;
            switch (name)
            {
                case "GetJobStatus":
                    result = new XElement("Result", status.JobStatus);
                    break;
                case "GetJobToolName":
                    result = new XElement("Result", PrintingToolsServiceCatalog.ExportTaskName);
                    break;
                case "GetJobMessages":
                    result = GPServerSoapExecution.BuildMessages(ToGpMessages(status.Messages));
                    result.Name = "Result";
                    break;
                case "GetJobResult":
                    GPServerSoapExecution.ValidateResultOptions(operation.Element("Options"));
                    if (status.JobStatus != "esriJobSucceeded")
                    {
                        return FaultFromResult(
                            StandardErrorHelpers.CreatePreconditionFailed(context, "Job results are available only after successful execution."),
                            soap,
                            "Job results are available only after successful execution.");
                    }

                    var names = GPServerSoapExecution.ReadResultNames(operation, ["Output_File"]);
                    var outputs = new List<GPResultResponse>();
                    foreach (var outputName in names)
                    {
                        if (!string.Equals(outputName, "Output_File", StringComparison.Ordinal))
                        {
                            return CreateSoapFault("The requested result was not found.", StatusCodes.Status404NotFound, soap);
                        }

                        var resultResponse = await HandleJobResult(context, cancellationToken).ConfigureAwait(false);
                        if ((resultResponse as IValueHttpResult)?.Value is not PrintResult printed || printed.Value?.Url is not { Length: > 0 } url)
                        {
                            return FaultFromResult(resultResponse, soap, "The GP operation failed.");
                        }

                        outputs.Add(new GPResultResponse
                        {
                            ParamName = "Output_File",
                            DataType = "GPDataFile",
                            Value = UrlReference(Absolutize(context, url))
                        });
                    }

                    result = GPServerSoapExecution.BuildResult(outputs, ToGpMessages(status.Messages));
                    break;
                default:
                    return CreateSoapFault(
                        "The requested GPServer SOAP operation is not implemented.",
                        StatusCodes.Status501NotImplemented,
                        soap);
            }

            return CreateSoapResponse(soap, operation.Name.Namespace, name + "Response", result);
        }
        finally
        {
            context.Request.RouteValues = originalRoutes;
        }
    }

    private static async Task<IResult> HandleGpSoapCancelAsync(
        HttpContext context,
        XElement operation,
        XNamespace soap,
        string jobId,
        CancellationToken cancellationToken)
    {
        var progressStore = context.RequestServices.GetRequiredService<IUniversalProgressStore>();
        var progress = await progressStore.GetProgressAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (progress is not PrintProgress print || !CanReadJob(print, context))
        {
            return CreateSoapFault("Print job was not found.", StatusCodes.Status404NotFound, soap);
        }

        if (print.Status is OperationStatus.Queued or OperationStatus.Processing)
        {
            context.RequestServices.GetRequiredService<PrintJobCancellationTokens>().Cancel(jobId);
            if (print.WithCancellation(DateTimeOffset.UtcNow, "Cancelled") is PrintProgress cancelled)
            {
                await progressStore.TrySetProgressAsync(
                    jobId,
                    cancelled,
                    print.Status,
                    PrintingToolsRequestHandlers.ResultTtl,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return CreateSoapResponse(soap, operation.Name.Namespace, "CancelJobResponse", null);
    }

    private static GPTaskInfoResponse[] BuildTasks(HttpContext context)
    {
        var (formatChoices, templateChoices) = ResolveAdvertisedChoices(context);
        return
        [
            new GPTaskInfoResponse
            {
                Name = PrintingToolsServiceCatalog.ExportTaskName,
                DisplayName = "Export Web Map",
                Description = "",
                Category = "",
                ExecutionType = PrintingToolsServiceCatalog.ExecutionType,
                Parameters =
                [
                    Parameter("Web_Map_as_JSON", "Web Map as JSON", "GPString", "esriGPParameterDirectionInput", "esriGPParameterTypeRequired", ""),
                    Parameter("Format", "Format", "GPString", "esriGPParameterDirectionInput", "esriGPParameterTypeOptional", PrintOutputFormat.Png32, formatChoices),
                    Parameter("Layout_Template", "Layout Template", "GPString", "esriGPParameterDirectionInput", "esriGPParameterTypeOptional", "MAP_ONLY", templateChoices),
                    Parameter("Output_File", "Output File", "GPDataFile", "esriGPParameterDirectionOutput", "esriGPParameterTypeRequired", null)
                ]
            },
            new GPTaskInfoResponse
            {
                Name = PrintingToolsServiceCatalog.LayoutTaskName,
                DisplayName = "Get Layout Templates Info",
                Description = "",
                Category = "",
                ExecutionType = PrintingToolsServiceCatalog.ExecutionType,
                Parameters =
                [
                    Parameter("Output_JSON", "Output JSON", "GPString", "esriGPParameterDirectionOutput", "esriGPParameterTypeRequired", null)
                ]
            }
        ];
    }

    private static GPTaskInfoResponse? FindTask(HttpContext context, string? name)
        => BuildTasks(context).FirstOrDefault(task => string.Equals(task.Name, name, StringComparison.Ordinal));

    private static GPParameterInfo Parameter(
        string name,
        string displayName,
        string dataType,
        string direction,
        string parameterType,
        string? defaultValue,
        string[]? choiceList = null)
        => new()
        {
            Name = name,
            DisplayName = displayName,
            DataType = dataType,
            Direction = direction,
            ParameterType = parameterType,
            DefaultValue = defaultValue is null ? null : JsonText(defaultValue),
            ChoiceList = choiceList
        };

    private static JsonElement JsonText(string value)
        => JsonSerializer.SerializeToElement(value, GPServerJsonContext.Default.String);

    private static JsonElement UrlReference(string url)
    {
        using var document = JsonDocument.Parse($"{{\"url\":{JsonSerializer.Serialize(url, GPServerJsonContext.Default.String)}}}");
        return document.RootElement.Clone();
    }

    private static GPJobMessage[] ToGpMessages(PrintJobMessage[]? messages)
        => (messages ?? []).Select(message => new GPJobMessage { Type = message.Type, Description = message.Description }).ToArray();

    private static string Absolutize(HttpContext context, string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
        {
            return absolute.AbsoluteUri;
        }

        var baseUrl = BaseUrlResolver.GetBaseUrl(context).TrimEnd('/');
        return url.StartsWith('/') ? baseUrl + url : baseUrl + "/" + url;
    }

    private static IResult FaultFromResult(IResult response, XNamespace soap, string fallback)
    {
        var message = (response as IValueHttpResult)?.Value is Microsoft.AspNetCore.Mvc.ProblemDetails problem
            ? problem.Detail ?? problem.Title ?? fallback
            : fallback;
        return CreateSoapFaultFromResult(response, message, soap);
    }
}

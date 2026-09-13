// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml.Linq;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Geoprocessing;
using Honua.Infrastructure.Models;
using Honua.Protocols.GeoServices.GPServer.Models;
using static Honua.Protocols.GeoServices.Soap.ArcGisSoapProtocol;

namespace Honua.Protocols.GeoServices.GPServer;

internal static partial class GPServerEndpoints
{
    // SOAP has one service URL rather than REST's task/job/parameter URLs. Supply
    // those route bindings to the existing handlers; never bypass their service,
    // process, ownership, terminal-state or output-reprojection checks.
    internal static async Task<IResult> HandleSoapExecutionAsync(
        HttpContext context, XElement operation, XNamespace soap, CancellationToken ct)
    {
        var originalRoutes = context.Request.RouteValues;
        context.Request.RouteValues = new RouteValueDictionary(originalRoutes);
        var name = operation.Name.LocalName;
        var logger = ResolveLogger(context);
        try
        {
            IResult response;
            XElement result;
            if (name is "SubmitJob" or "Execute")
            {
                var taskName = operation.Element("ToolName")?.Value ?? string.Empty;
                context.Request.RouteValues["taskName"] = taskName;
                IReadOnlyDictionary<string, string> ReadParameters()
                {
                    var catalog = context.RequestServices.GetRequiredService<IProcessCatalog>();
                    var definition = ResolveTaskDefinition(catalog, taskName)
                        ?? throw new GeoprocessingNotFoundException("The requested task was not found.");
                    return GPServerSoapExecution.ReadSubmission(operation, BuildTaskInfo(taskName, definition));
                }

                // Parsing is deferred until the canonical handler authorizes the
                // caller, exactly as it is for REST form parameters.
                response = name == "SubmitJob"
                    ? await HandleSubmitJob(context, ct, ReadParameters).ConfigureAwait(false)
                    : await HandleExecute(context, ct, ReadParameters).ConfigureAwait(false);
                if ((response as IValueHttpResult)?.Value is GPSubmitJobResponse submitted)
                {
                    result = new XElement("Result", submitted.JobId);
                }
                else if ((response as IValueHttpResult)?.Value is GPExecuteResponse executed)
                {
                    result = GPServerSoapExecution.BuildResult(executed.Results ?? [], executed.Messages ?? []);
                }
                else
                {
                    return ExecutionFault(response, soap);
                }
            }
            else
            {
                var jobId = GPServerSoapExecution.ReadJobId(operation);
                var jobService = context.RequestServices.GetRequiredService<IGeoprocessingJobService>();
                var job = await jobService.GetJobAsync(jobId, context.User, ct).ConfigureAwait(false);
                var taskName = job.Spec.Parameters.GetValueOrDefault(GeoprocessingProtocolMetadataKeys.GPServerTaskName) ?? string.Empty;
                context.Request.RouteValues["taskName"] = taskName;
                context.Request.RouteValues["jobId"] = jobId;
                var serviceId = originalRoutes["serviceId"]?.ToString() ?? string.Empty;
                var bindingError = ValidateJobBinding(context, logger, job, serviceId, taskName);
                if (bindingError is not null)
                {
                    return ExecutionFault(bindingError, soap);
                }

                response = name == "CancelJob"
                    ? await HandleCancelJob(context, ct).ConfigureAwait(false)
                    : await HandleJobStatus(context, ct).ConfigureAwait(false);
                if ((response as IValueHttpResult)?.Value is not GPJobStatusResponse status)
                {
                    return ExecutionFault(response, soap);
                }

                switch (name)
                {
                    case "GetJobStatus":
                    case "CancelJob":
                        result = new XElement("Result", status.JobStatus);
                        break;
                    case "GetJobToolName":
                        result = new XElement("Result", taskName);
                        break;
                    case "GetJobMessages":
                        result = GPServerSoapExecution.BuildMessages(status.Messages ?? []);
                        result.Name = "Result";
                        break;
                    case "GetJobResult":
                        GPServerSoapExecution.ValidateResultOptions(operation.Element("Options"));
                        if (status.JobStatus != "esriJobSucceeded")
                        {
                            return ExecutionFault(StandardErrorHelpers.CreatePreconditionFailed(context,
                                "Job results are available only after successful execution."), soap);
                        }
                        var outputs = new List<GPResultResponse>();
                        var names = GPServerSoapExecution.ReadResultNames(operation, status.Results?.Keys ?? []);
                        foreach (var outputName in names)
                        {
                            context.Request.RouteValues["paramName"] = outputName;
                            response = await HandleJobResult(context, ct).ConfigureAwait(false);
                            if ((response as IValueHttpResult)?.Value is not GPResultResponse output)
                            {
                                return ExecutionFault(response, soap);
                            }
                            outputs.Add(output);
                        }
                        result = GPServerSoapExecution.BuildResult(outputs, status.Messages ?? []);
                        break;
                    default:
                        throw new InvalidOperationException("Unsupported SOAP execution operation.");
                }
            }

            return CreateSoapResponse(soap, operation.Name.Namespace, name + "Response", result);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return ExecutionFault(MapExceptionToResult(context, logger, name, exception), soap);
        }
        finally
        {
            context.Request.RouteValues = originalRoutes;
        }
    }

    private static IResult ExecutionFault(IResult response, XNamespace soap)
        => CreateSoapFaultFromResult(response,
            (response as IValueHttpResult)?.Value is Microsoft.AspNetCore.Mvc.ProblemDetails problem
                ? problem.Detail ?? problem.Title ?? "The GP operation failed."
                : "The GP operation failed.", soap);
}

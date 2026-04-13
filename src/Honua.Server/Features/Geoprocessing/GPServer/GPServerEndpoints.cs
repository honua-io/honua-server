// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Server.Features.Geoprocessing.GPServer.Models;
using Honua.Server.Features.Infrastructure.Models;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.Geoprocessing.GPServer;

/// <summary>
/// Maps GeoServices GPServer REST endpoints as a protocol adapter
/// over the canonical process runtime.
/// </summary>
internal static class GPServerEndpoints
{
    private const string RouteBase = "/rest/services/{serviceId}/GPServer";

    /// <summary>
    /// Maps GPServer endpoints.
    /// </summary>
    public static IEndpointRouteBuilder MapGPServerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Service info
        endpoints.MapGet(RouteBase,
                static (HttpContext context, CancellationToken ct) => HandleServiceInfo(context, ct))
            .WithDisplayName("GPServer Service Info")
            .WithName("GPServerServiceInfo")
            .WithSummary("Get GPServer service metadata")
            .WithDescription("Returns metadata about the GPServer service including available tasks")
            .WithTags("GPServer");

        // Task info
        endpoints.MapGet($"{RouteBase}/{{taskName}}",
                static (HttpContext context, CancellationToken ct) => HandleTaskInfo(context, ct))
            .WithDisplayName("GPServer Task Info")
            .WithName("GPServerTaskInfo")
            .WithSummary("Get GP task metadata")
            .WithDescription("Returns metadata about a specific GP task including parameters")
            .WithTags("GPServer");

        // Synchronous execute (POST + GET per Esri GP contract)
        endpoints.MapPost($"{RouteBase}/{{taskName}}/execute",
                static (HttpContext context, CancellationToken ct) => HandleExecute(context, ct))
            .WithDisplayName("GPServer Execute Task")
            .WithName("GPServerExecute")
            .WithSummary("Execute a GP task synchronously")
            .WithDescription("Executes a GP task and returns results inline")
            .WithTags("GPServer");

        endpoints.MapGet($"{RouteBase}/{{taskName}}/execute",
                static (HttpContext context, CancellationToken ct) => HandleExecute(context, ct))
            .WithDisplayName("GPServer Execute Task (GET)")
            .WithName("GPServerExecuteGet")
            .WithSummary("Execute a GP task synchronously using GET")
            .WithDescription("Executes a GP task and returns results inline")
            .WithTags("GPServer");

        // Async submit job (POST + GET per Esri GP contract)
        endpoints.MapPost($"{RouteBase}/{{taskName}}/submitJob",
                static (HttpContext context, CancellationToken ct) => HandleSubmitJob(context, ct))
            .WithDisplayName("GPServer Submit Job")
            .WithName("GPServerSubmitJob")
            .WithSummary("Submit an asynchronous GP job")
            .WithDescription("Queues a GP task for background processing and returns a job ID")
            .WithTags("GPServer");

        endpoints.MapGet($"{RouteBase}/{{taskName}}/submitJob",
                static (HttpContext context, CancellationToken ct) => HandleSubmitJob(context, ct))
            .WithDisplayName("GPServer Submit Job (GET)")
            .WithName("GPServerSubmitJobGet")
            .WithSummary("Submit an asynchronous GP job using GET")
            .WithDescription("Queues a GP task for background processing and returns a job ID")
            .WithTags("GPServer");

        // Job status
        endpoints.MapGet($"{RouteBase}/{{taskName}}/jobs/{{jobId}}",
                static (HttpContext context, CancellationToken ct) => HandleJobStatus(context, ct))
            .WithDisplayName("GPServer Job Status")
            .WithName("GPServerJobStatus")
            .WithSummary("Get the status of a GP job")
            .WithDescription("Returns the current status and progress of a GP job")
            .WithTags("GPServer");

        // Per-output result
        endpoints.MapGet($"{RouteBase}/{{taskName}}/jobs/{{jobId}}/results/{{paramName}}",
                static (HttpContext context, CancellationToken ct) => HandleJobResult(context, ct))
            .WithDisplayName("GPServer Job Result")
            .WithName("GPServerJobResult")
            .WithSummary("Get a specific output result from a completed GP job")
            .WithDescription("Returns the value of a named output parameter from a completed job")
            .WithTags("GPServer");

        // Cancel job (GET per Esri convention + POST for modern clients)
        endpoints.MapGet($"{RouteBase}/{{taskName}}/jobs/{{jobId}}/cancel",
                static (HttpContext context, CancellationToken ct) => HandleCancelJob(context, ct))
            .WithDisplayName("GPServer Cancel Job")
            .WithName("GPServerCancelJob")
            .WithSummary("Cancel a GP job")
            .WithDescription("Cancels an in-flight GP job")
            .WithTags("GPServer");

        endpoints.MapPost($"{RouteBase}/{{taskName}}/jobs/{{jobId}}/cancel",
                static (HttpContext context, CancellationToken ct) => HandleCancelJob(context, ct))
            .WithDisplayName("GPServer Cancel Job (POST)")
            .WithName("GPServerCancelJobPost")
            .WithSummary("Cancel a GP job")
            .WithDescription("Cancels an in-flight GP job")
            .WithTags("GPServer");

        return endpoints;
    }

    // -----------------------------------------------------------------------
    // Handlers
    // -----------------------------------------------------------------------

    private static Task<IResult> HandleServiceInfo(HttpContext context, CancellationToken ct)
    {
        var serviceId = context.Request.RouteValues["serviceId"]?.ToString() ?? "";
        EnrichActivity("ServiceInfo", serviceId);
        var logger = ResolveLogger(context);
        GPServerLog.ServiceInfoRequested(logger, serviceId);

        var response = new GPServiceInfoResponse
        {
            ServiceDescription = $"Geoprocessing service for {serviceId}",
            ExecutionType = "esriExecutionTypeAsynchronous",
            Tasks = [] // Stub until process catalog is formalized
        };

        return Task.FromResult(Results.Json(
            response, GPServerJsonContext.Default.GPServiceInfoResponse,
            contentType: "application/json"));
    }

    private static Task<IResult> HandleTaskInfo(HttpContext context, CancellationToken ct)
    {
        var serviceId = context.Request.RouteValues["serviceId"]?.ToString() ?? "";
        var taskName = context.Request.RouteValues["taskName"]?.ToString() ?? "";
        EnrichActivity("TaskInfo", serviceId, taskName);
        var logger = ResolveLogger(context);
        GPServerLog.TaskInfoRequested(logger, serviceId, taskName);

        var response = new GPTaskInfoResponse
        {
            Name = taskName,
            DisplayName = taskName,
            ExecutionType = "esriExecutionTypeAsynchronous",
            Parameters = [] // Stub until process catalog is formalized
        };

        return Task.FromResult(Results.Json(
            response, GPServerJsonContext.Default.GPTaskInfoResponse,
            contentType: "application/json"));
    }

    private static async Task<IResult> HandleExecute(HttpContext context, CancellationToken ct)
    {
        var serviceId = context.Request.RouteValues["serviceId"]?.ToString() ?? "";
        var taskName = context.Request.RouteValues["taskName"]?.ToString() ?? "";
        EnrichActivity("Execute", serviceId, taskName);
        var logger = ResolveLogger(context);
        GPServerLog.ExecuteRequested(logger, taskName);

        // Synchronous execute is not yet available (#721 ExecutePlan).
        // Return 501 with structured error per design.
        return StandardErrorHelpers.CreateNotImplemented(context,
            "Synchronous GP task execution is not yet available. " +
            "Use submitJob for asynchronous execution.");
    }

    private static async Task<IResult> HandleSubmitJob(HttpContext context, CancellationToken ct)
    {
        var serviceId = context.Request.RouteValues["serviceId"]?.ToString() ?? "";
        var taskName = context.Request.RouteValues["taskName"]?.ToString() ?? "";
        EnrichActivity("SubmitJob", serviceId, taskName);
        var logger = ResolveLogger(context);

        var parameters = await GPServerParameterTranslation.ReadRequestParametersAsync(context);

        // Reject unsupported GP environment controls (env:outSR, env:processSR, context)
        // with a clear 400 instead of silently stripping them.
        var envError = RejectUnsupportedEnvControls(context, logger, parameters);
        if (envError != null)
        {
            return envError;
        }

        // Build a minimal AnalysisPlan from the GP task parameters.
        // The serviceId:taskName becomes the scoped process identity;
        // parameters map to step inputs.
        var plan = new AnalysisPlan
        {
            PlanId = $"gpserver-{Guid.NewGuid():N}",
            IntentId = $"gpserver:{serviceId}:{taskName}",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "step-1",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = $"{serviceId}:{taskName}",
                    Inputs = GPServerParameterTranslation.TranslateInbound(
                        FilterGpParameters(parameters))
                }
            ]
        };

        // Persist the GPServer route binding so status/result/cancel can
        // validate that the route serviceId/taskName match the originating job.
        var protocolMetadata = new Dictionary<string, string>
        {
            ["gpserver.serviceId"] = serviceId,
            ["gpserver.taskName"] = taskName
        };

        var jobService = context.RequestServices.GetRequiredService<IGeoprocessingJobService>();

        try
        {
            var jobRecord = await jobService.SubmitJobAsync(
                plan, null, context.User, protocolMetadata, ct);

            var esriStatus = GPServerStatusMapping.ToEsriJobStatus(jobRecord.Status);
            GPServerLog.JobSubmitted(logger, jobRecord.OperationId, taskName);

            var response = new GPSubmitJobResponse
            {
                JobId = jobRecord.OperationId,
                JobStatus = esriStatus
            };

            return Results.Json(response, GPServerJsonContext.Default.GPSubmitJobResponse,
                contentType: "application/json", statusCode: 202);
        }
        catch (Exception ex)
        {
            return MapExceptionToResult(context, logger, "SubmitJob", ex);
        }
    }

    private static async Task<IResult> HandleJobStatus(HttpContext context, CancellationToken ct)
    {
        var serviceId = context.Request.RouteValues["serviceId"]?.ToString() ?? "";
        var taskName = context.Request.RouteValues["taskName"]?.ToString() ?? "";
        EnrichActivity("JobStatus", serviceId, taskName);
        var jobId = context.Request.RouteValues["jobId"]?.ToString();
        var logger = ResolveLogger(context);

        if (string.IsNullOrWhiteSpace(jobId))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Missing jobId.");
        }

        var jobService = context.RequestServices.GetRequiredService<IGeoprocessingJobService>();

        try
        {
            var job = await jobService.GetJobAsync(jobId, context.User, ct);

            var bindingError = ValidateJobBinding(context, logger, job, serviceId, taskName);
            if (bindingError != null)
            {
                return bindingError;
            }

            var esriStatus = GPServerStatusMapping.ToEsriJobStatus(job.Status);
            GPServerLog.JobStatusPolled(logger, jobId, esriStatus);

            var messages = new List<GPJobMessage>();
            if (job.CurrentPhase != null)
            {
                messages.Add(new GPJobMessage
                {
                    Type = "esriJobMessageTypeInformative",
                    Description = job.CurrentPhase
                });
            }

            foreach (var warning in job.Warnings)
            {
                messages.Add(new GPJobMessage
                {
                    Type = "esriJobMessageTypeWarning",
                    Description = warning
                });
            }

            if (job.ErrorMessage != null)
            {
                messages.Add(new GPJobMessage
                {
                    Type = "esriJobMessageTypeError",
                    Description = job.ErrorMessage
                });
            }

            // Include result references when job succeeded
            Dictionary<string, GPJobResultRef>? results = null;
            if (job.Status == ExecutionJobStatus.Succeeded)
            {
                // Result references are populated when artifact storage is available.
                // For now, return empty results dict to signal shape correctness.
                results = new Dictionary<string, GPJobResultRef>();
            }

            var response = new GPJobStatusResponse
            {
                JobId = jobId,
                JobStatus = esriStatus,
                Messages = [.. messages],
                Results = results
            };

            return Results.Json(response, GPServerJsonContext.Default.GPJobStatusResponse,
                contentType: "application/json");
        }
        catch (Exception ex)
        {
            return MapExceptionToResult(context, logger, "JobStatus", ex);
        }
    }

    private static async Task<IResult> HandleJobResult(HttpContext context, CancellationToken ct)
    {
        var serviceId = context.Request.RouteValues["serviceId"]?.ToString() ?? "";
        var taskName = context.Request.RouteValues["taskName"]?.ToString() ?? "";
        EnrichActivity("JobResult", serviceId, taskName);
        var jobId = context.Request.RouteValues["jobId"]?.ToString();
        var paramName = context.Request.RouteValues["paramName"]?.ToString();
        var logger = ResolveLogger(context);

        if (string.IsNullOrWhiteSpace(jobId))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Missing jobId.");
        }

        if (string.IsNullOrWhiteSpace(paramName))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Missing paramName.");
        }

        GPServerLog.JobResultRequested(logger, jobId, paramName);

        var jobService = context.RequestServices.GetRequiredService<IGeoprocessingJobService>();

        try
        {
            // Validate route binding before accessing results.
            var job = await jobService.GetJobAsync(jobId, context.User, ct);
            var bindingError = ValidateJobBinding(context, logger, job, serviceId, taskName);
            if (bindingError != null)
            {
                return bindingError;
            }

            // GetJobResultsAsync currently throws GeoprocessingNotFoundException
            // because result storage is not yet implemented.
            var results = await jobService.GetJobResultsAsync(jobId, context.User, ct);

            // Find the artifact matching the requested parameter name
            var artifact = results.Artifacts.FirstOrDefault(a =>
                string.Equals(
                    GPServerParameterTranslation.ResolveOutputParameterName(a),
                    paramName,
                    StringComparison.OrdinalIgnoreCase));

            if (artifact == null)
            {
                return StandardErrorHelpers.CreateNotFound(context,
                    $"Output parameter '{paramName}' not found in job results.");
            }

            var response = new GPResultResponse
            {
                ParamName = paramName,
                DataType = GPServerParameterTranslation.ToEsriDataType(artifact.Kind),
                Value = artifact.Uri ?? artifact.Label
            };

            return Results.Json(response, GPServerJsonContext.Default.GPResultResponse,
                contentType: "application/json");
        }
        catch (Exception ex)
        {
            return MapExceptionToResult(context, logger, "JobResult", ex);
        }
    }

    private static async Task<IResult> HandleCancelJob(HttpContext context, CancellationToken ct)
    {
        var serviceId = context.Request.RouteValues["serviceId"]?.ToString() ?? "";
        var taskName = context.Request.RouteValues["taskName"]?.ToString() ?? "";
        EnrichActivity("CancelJob", serviceId, taskName);
        var jobId = context.Request.RouteValues["jobId"]?.ToString();
        var logger = ResolveLogger(context);

        if (string.IsNullOrWhiteSpace(jobId))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Missing jobId.");
        }

        GPServerLog.JobCancelRequested(logger, jobId);

        var jobService = context.RequestServices.GetRequiredService<IGeoprocessingJobService>();

        try
        {
            // Validate route binding before attempting cancellation.
            var existing = await jobService.GetJobAsync(jobId, context.User, ct);
            var bindingError = ValidateJobBinding(context, logger, existing, serviceId, taskName);
            if (bindingError != null)
            {
                return bindingError;
            }

            await jobService.CancelJobAsync(jobId, context.User, ct);

            // Re-read the job to return the updated status
            ExecutionJobRecord? job;
            try
            {
                job = await jobService.GetJobAsync(jobId, context.User, ct);
            }
            catch (GeoprocessingNotFoundException)
            {
                // Job may have been cleaned up after cancellation
                job = null;
            }

            var esriStatus = job != null
                ? GPServerStatusMapping.ToEsriJobStatus(job.Status)
                : "esriJobCancelled";

            var response = new GPJobStatusResponse
            {
                JobId = jobId,
                JobStatus = esriStatus,
                Messages =
                [
                    new GPJobMessage
                    {
                        Type = "esriJobMessageTypeInformative",
                        Description = "Job cancellation requested."
                    }
                ]
            };

            return Results.Json(response, GPServerJsonContext.Default.GPJobStatusResponse,
                contentType: "application/json");
        }
        catch (Exception ex)
        {
            return MapExceptionToResult(context, logger, "CancelJob", ex);
        }
    }

    // -----------------------------------------------------------------------
    // Shared helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Validates that the route <paramref name="serviceId"/> and <paramref name="taskName"/>
    /// match the protocol metadata stored with the job when it was submitted.
    /// Returns a 404 result when the binding does not match or is absent; null when it matches.
    /// Jobs without GPServer binding metadata (e.g. submitted via gRPC) are rejected
    /// to prevent cross-protocol job access through arbitrary GPServer routes.
    /// </summary>
    private static IResult? ValidateJobBinding(
        HttpContext context, ILogger logger, ExecutionJobRecord job,
        string serviceId, string taskName)
    {
        var storedService = job.Spec.Parameters.GetValueOrDefault("gpserver.serviceId");
        var storedTask = job.Spec.Parameters.GetValueOrDefault("gpserver.taskName");

        // Jobs without GPServer binding metadata were not submitted through GPServer.
        // Reject them to prevent cross-protocol job access.
        if (storedService == null && storedTask == null)
        {
            GPServerLog.JobBindingMismatch(logger, job.OperationId, serviceId, taskName);
            return StandardErrorHelpers.CreateNotFound(context,
                $"Job '{job.OperationId}' does not belong to service '{serviceId}' task '{taskName}'.");
        }

        if (string.Equals(storedService, serviceId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(storedTask, taskName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        GPServerLog.JobBindingMismatch(logger, job.OperationId, serviceId, taskName);
        return StandardErrorHelpers.CreateNotFound(context,
            $"Job '{job.OperationId}' does not belong to service '{serviceId}' task '{taskName}'.");
    }

    /// <summary>
    /// Rejects unsupported GP environment controls (<c>env:outSR</c>, <c>env:processSR</c>,
    /// <c>context</c>) with a structured 400 error instead of silently stripping them.
    /// Returns null when no unsupported controls are present.
    /// </summary>
    private static IResult? RejectUnsupportedEnvControls(
        HttpContext context, ILogger logger, IReadOnlyDictionary<string, string> allParams)
    {
        List<string>? unsupported = null;
        foreach (var key in allParams.Keys)
        {
            if (key.StartsWith("env:", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "context", StringComparison.OrdinalIgnoreCase))
            {
                unsupported ??= [];
                unsupported.Add(key);
            }
        }

        if (unsupported == null)
        {
            return null;
        }

        var names = string.Join(", ", unsupported);
        GPServerLog.UnsupportedEnvControlsRejected(logger, names);
        return StandardErrorHelpers.CreateBadRequest(context,
            $"GP environment controls are not yet supported: {names}. " +
            "Remove these parameters or wait for engine support.");
    }

    /// <summary>
    /// Filters out protocol-level parameters (f, token, etc.) from GP task inputs.
    /// Environment controls must be validated with <see cref="RejectUnsupportedEnvControls"/>
    /// before calling this method.
    /// </summary>
    private static Dictionary<string, string> FilterGpParameters(
        IReadOnlyDictionary<string, string> allParams)
    {
        var filtered = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in allParams)
        {
            // Skip GeoServices protocol parameters
            if (string.Equals(key, "f", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "token", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "callback", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "returnMessages", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            filtered[key] = value;
        }

        return filtered;
    }

    private static IResult MapExceptionToResult(
        HttpContext context, ILogger logger, string operation, Exception ex)
    {
        return ex switch
        {
            GeoprocessingAuthorizationException authEx =>
                authEx.RequiresAuthentication
                    ? StandardErrorHelpers.CreateUnauthorized(context, authEx.Message)
                    : StandardErrorHelpers.CreateForbidden(context, authEx.Message),

            GeoprocessingApprovalRequiredException approvalEx =>
                LogAndReturn(logger, operation, approvalEx.Message,
                    StandardErrorHelpers.CreateForbidden(context, approvalEx.Message)),

            GeoprocessingNotFoundException notFoundEx =>
                LogAndReturn(logger, operation, notFoundEx.Message,
                    StandardErrorHelpers.CreateNotFound(context, notFoundEx.Message)),

            GeoprocessingPreconditionFailedException preconditionEx =>
                LogAndReturn(logger, operation, preconditionEx.Message,
                    StandardErrorHelpers.CreateBadRequest(context, preconditionEx.Message)),

            GeoprocessingValidationException validationEx =>
                LogAndReturn(logger, operation, validationEx.Message,
                    StandardErrorHelpers.CreateBadRequest(context, validationEx.Message)),

            GeoprocessingStoreUnavailableException storeEx =>
                LogAndReturn(logger, operation, storeEx.Message,
                    StandardErrorHelpers.CreateServiceUnavailable(context, storeEx.Message)),

            GeoprocessingIdempotencyConflictException conflictEx =>
                LogAndReturn(logger, operation, conflictEx.Message,
                    StandardErrorHelpers.CreateConflict(context, conflictEx.Message)),

            _ => LogAndReturn(logger, operation, ex.Message,
                StandardErrorHelpers.CreateInternalServerError(context,
                    "An error occurred while processing the GP request."))
        };
    }

    private static IResult LogAndReturn(ILogger logger, string operation, string error, IResult result)
    {
        GPServerLog.RequestFailed(logger, operation, error);
        return result;
    }

    private static void EnrichActivity(string operation, string? serviceId = null, string? taskName = null)
    {
        var activity = Activity.Current;
        if (activity == null)
        {
            return;
        }

        activity.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.GPServer);
        activity.SetTag(HonuaTelemetry.Tags.Operation, operation);
        if (!string.IsNullOrEmpty(serviceId))
        {
            activity.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
        }

        if (!string.IsNullOrEmpty(taskName))
        {
            activity.SetTag("honua.gp.task_name", taskName);
        }
    }

    private static ILogger ResolveLogger(HttpContext context)
        => context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Honua.Server.GPServerEndpoints");
}

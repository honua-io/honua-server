// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Geoprocessing;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Protocols.GeoServices.GPServer.Models;
using Honua.ServiceDefaults;

namespace Honua.Protocols.GeoServices.GPServer;

/// <summary>
/// Maps GeoServices GPServer REST endpoints as a protocol adapter
/// over the canonical process runtime.
/// </summary>
internal static class GPServerEndpoints
{
    private const string RouteBase = "/rest/services/{serviceId}/GPServer";
    private const string ProtocolName = "GPServer";
    private static readonly HashSet<string> FormContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/x-www-form-urlencoded",
        "multipart/form-data"
    };

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

        // PUBLIC by design (#1144): GeoServices REST POST mirror of the GET
        // service-info read; returns the same metadata as the GET form. Marked
        // AllowAnonymous so the audit architecture guard records the
        // intentional decision.
        endpoints.MapPost(RouteBase,
                static (HttpContext context, CancellationToken ct) => HandleServiceInfo(context, ct))
            .WithDisplayName("GPServer Service Info (POST)")
            .WithName("GPServerServiceInfoPost")
            .WithSummary("Get GPServer service metadata")
            .WithDescription("Returns metadata about the GPServer service including available tasks")
            .WithTags("GPServer")
            .AllowAnonymous();

        // Task info
        endpoints.MapGet($"{RouteBase}/{{taskName}}",
                static (HttpContext context, CancellationToken ct) => HandleTaskInfo(context, ct))
            .WithDisplayName("GPServer Task Info")
            .WithName("GPServerTaskInfo")
            .WithSummary("Get GP task metadata")
            .WithDescription("Returns metadata about a specific GP task including parameters")
            .WithTags("GPServer");

        // PUBLIC by design (#1144): GeoServices REST POST mirror of the GET
        // task-info read; returns the same metadata as the GET form. Marked
        // AllowAnonymous so the audit architecture guard records the
        // intentional decision.
        endpoints.MapPost($"{RouteBase}/{{taskName}}",
                static (HttpContext context, CancellationToken ct) => HandleTaskInfo(context, ct))
            .WithDisplayName("GPServer Task Info (POST)")
            .WithName("GPServerTaskInfoPost")
            .WithSummary("Get GP task metadata")
            .WithDescription("Returns metadata about a specific GP task including parameters")
            .WithTags("GPServer")
            .AllowAnonymous();

        // Async submit job (POST + GET per Esri GP contract)
        // HANDLER-AUTHORIZED (#1144): the handler calls
        // IGeoprocessingJobService.EnsureCallerAuthorized before reading the
        // request body, so 401/403 are returned ahead of 400 for unauth
        // callers. Marked AllowAnonymous to record the explicit decision.
        endpoints.MapPost($"{RouteBase}/{{taskName}}/submitJob",
                static (HttpContext context, CancellationToken ct) => HandleSubmitJob(context, ct))
            .WithDisplayName("GPServer Submit Job")
            .WithName("GPServerSubmitJob")
            .WithSummary("Submit an asynchronous GP job")
            .WithDescription("Queues a GP task for background processing and returns a job ID")
            .WithTags("GPServer")
            .AllowAnonymous();

        endpoints.MapGet($"{RouteBase}/{{taskName}}/submitJob",
                static (HttpContext context, CancellationToken ct) => HandleSubmitJob(context, ct))
            .WithDisplayName("GPServer Submit Job (GET)")
            .WithName("GPServerSubmitJobGet")
            .WithSummary("Submit an asynchronous GP job using GET")
            .WithDescription("Queues a GP task for background processing and returns a job ID")
            .WithTags("GPServer");

        // Note: the generic <c>/{taskName}/execute</c> route is intentionally NOT
        // published — sync-eligible tasks run via <c>/{taskName}</c> directly, async
        // tasks via <c>/{taskName}/submitJob</c>. The /execute path is reserved for
        // future per-task explicit routes (a 404 here is the contract — see
        // GPServerEndpointTests.ExecuteGet_GenericAsyncTaskRouteIsNotPublished).

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

        // HANDLER-AUTHORIZED (#1144): GetJobAsync/CancelJobAsync resolve
        // ownership against HttpContext.User; unauthenticated callers receive
        // GeoprocessingAuthorizationException → 401. Marked AllowAnonymous so
        // the audit guard records the intentional decision.
        endpoints.MapPost($"{RouteBase}/{{taskName}}/jobs/{{jobId}}/cancel",
                static (HttpContext context, CancellationToken ct) => HandleCancelJob(context, ct))
            .WithDisplayName("GPServer Cancel Job (POST)")
            .WithName("GPServerCancelJobPost")
            .WithSummary("Cancel a GP job")
            .WithDescription("Cancels an in-flight GP job")
            .WithTags("GPServer")
            .AllowAnonymous();

        return endpoints;
    }

    // -----------------------------------------------------------------------
    // Handlers
    // -----------------------------------------------------------------------

    private static async Task<IResult> HandleServiceInfo(HttpContext context, CancellationToken ct)
    {
        ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var serviceId = context.Request.RouteValues["serviceId"]?.ToString() ?? "";
        EnrichActivity("ServiceInfo", serviceId);
        var logger = ResolveLogger(context);
        GPServerLog.ServiceInfoRequested(logger, serviceId);
        var formatError = await ValidateMetadataJsonFormatAsync(context, ct);
        if (formatError != null)
        {
            return formatError;
        }

        var serviceValidation = await ValidateServiceAsync(context, serviceId, logger, ct);
        if (!serviceValidation.IsValid)
        {
            return serviceValidation.ErrorResult!;
        }

        var processCatalog = context.RequestServices.GetRequiredService<IProcessCatalog>();

        var response = new GPServiceInfoResponse
        {
            ServiceDescription = $"Geoprocessing service for {serviceId}",
            ExecutionType = "esriExecutionTypeAsynchronous",
            Capabilities = string.Empty,
            ResultMapServerName = string.Empty,
            Tasks = [.. processCatalog.ListProcesses().Select(process => process.ProcessId)]
        };

        return Results.Json(
            response, GPServerJsonContext.Default.GPServiceInfoResponse,
            contentType: "application/json");
    }

    private static async Task<IResult> HandleTaskInfo(HttpContext context, CancellationToken ct)
    {
        ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var serviceId = context.Request.RouteValues["serviceId"]?.ToString() ?? "";
        var taskName = context.Request.RouteValues["taskName"]?.ToString() ?? "";
        EnrichActivity("TaskInfo", serviceId, taskName);
        var logger = ResolveLogger(context);
        GPServerLog.TaskInfoRequested(logger, serviceId, taskName);
        var formatError = await ValidateMetadataJsonFormatAsync(context, ct);
        if (formatError != null)
        {
            return formatError;
        }

        var serviceValidation = await ValidateServiceAsync(context, serviceId, logger, ct);
        if (!serviceValidation.IsValid)
        {
            return serviceValidation.ErrorResult!;
        }

        var processCatalog = context.RequestServices.GetRequiredService<IProcessCatalog>();
        var definition = ResolveTaskDefinition(processCatalog, taskName);
        if (definition == null)
        {
            GPServerLog.TaskResolutionUnavailable(logger, serviceId, taskName);
            return SetSpanErrorAndReturn(
                StandardErrorHelpers.CreateNotFound(
                    context,
                    $"Task '{taskName}' on service '{serviceId}' was not found."),
                "Task not found");
        }

        return Results.Json(
            BuildTaskInfo(definition),
            GPServerJsonContext.Default.GPTaskInfoResponse,
            contentType: "application/json");
    }

    private static async Task<IResult> HandleSubmitJob(HttpContext context, CancellationToken ct)
    {
        ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var serviceId = context.Request.RouteValues["serviceId"]?.ToString() ?? "";
        var taskName = context.Request.RouteValues["taskName"]?.ToString() ?? "";
        EnrichActivity("SubmitJob", serviceId, taskName);
        var logger = ResolveLogger(context);

        var jobService = context.RequestServices.GetRequiredService<IGeoprocessingJobService>();

        try
        {
            var serviceValidation = await ValidateServiceAsync(context, serviceId, logger, ct);
            if (!serviceValidation.IsValid)
            {
                return serviceValidation.ErrorResult!;
            }

            // Auth must precede parameter reading to guarantee 401/403 before 400
            // on invalid input from unauthenticated callers (see IGeoprocessingJobService contract).
            jobService.EnsureCallerAuthorized(
                context.User,
                OperatorResourceType.Process,
                OperatorOperation.Execute);

            var contentTypeError = ValidateFormPostContentType(context);
            if (contentTypeError is not null)
            {
                return contentTypeError;
            }

            var parameters = await GPServerParameterTranslation.ReadRequestParametersAsync(context, ct);
            var formatError = ValidateJsonFormat(context, parameters);
            if (formatError != null)
            {
                return formatError;
            }

            // Reject unsupported GP environment controls (env:outSR, env:processSR, context)
            // with a clear 400 instead of silently stripping them. This is the
            // ESTABLISHED async submitJob contract that cross-repo integration tests
            // assert; env:outSR honoring is an ADDITIVE feature of the sync execute
            // route only (see HandleExecute / TryParseEnvControls). See #1228.
            var envError = RejectUnsupportedEnvControls(context, logger, parameters);
            if (envError != null)
            {
                return envError;
            }

            var processCatalog = context.RequestServices.GetRequiredService<IProcessCatalog>();
            var definition = ResolveTaskDefinition(processCatalog, taskName);
            if (definition == null)
            {
                GPServerLog.TaskResolutionUnavailable(logger, serviceId, taskName);
                return SetSpanErrorAndReturn(
                    StandardErrorHelpers.CreateNotFound(
                        context,
                        $"Task '{taskName}' on service '{serviceId}' was not found."),
                    "Task not found");
            }

            var planResult = BuildSubmissionPlan(definition, serviceId, parameters);
            if (planResult.CapabilityError is not null)
            {
                return SetSpanErrorAndReturn(
                    StandardErrorHelpers.CreateBadRequest(context, planResult.CapabilityError),
                    "FeatureSet requires feature-collection execution");
            }

            var plan = planResult.Plan!;
            // submitJob rejects all env:* controls above, so there are none to record.
            var protocolMetadata = BuildProtocolMetadata(serviceId, taskName, definition, parameters, default);
            var job = await jobService.SubmitJobAsync(
                plan,
                idempotencyKey: null,
                context.User,
                protocolMetadata,
                ct);

            var response = new GPSubmitJobResponse
            {
                JobId = job.OperationId,
                JobStatus = GPServerStatusMapping.ToEsriJobStatus(job.Status)
            };

            return Results.Json(
                response,
                GPServerJsonContext.Default.GPSubmitJobResponse,
                contentType: "application/json");
        }
        catch (Exception ex)
        {
            return MapExceptionToResult(context, logger, "SubmitJob", ex);
        }
    }

    private static async Task<IResult> HandleExecute(HttpContext context, CancellationToken ct)
    {
        ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var serviceId = context.Request.RouteValues["serviceId"]?.ToString() ?? "";
        var taskName = context.Request.RouteValues["taskName"]?.ToString() ?? "";
        EnrichActivity("Execute", serviceId, taskName);
        var logger = ResolveLogger(context);

        var jobService = context.RequestServices.GetRequiredService<IGeoprocessingJobService>();

        try
        {
            var serviceValidation = await ValidateServiceAsync(context, serviceId, logger, ct);
            if (!serviceValidation.IsValid)
            {
                return serviceValidation.ErrorResult!;
            }

            // Auth must precede parameter reading to guarantee 401/403 before 400.
            jobService.EnsureCallerAuthorized(
                context.User,
                OperatorResourceType.Process,
                OperatorOperation.Execute);

            var contentTypeError = ValidateFormPostContentType(context);
            if (contentTypeError is not null)
            {
                return contentTypeError;
            }

            var parameters = await GPServerParameterTranslation.ReadRequestParametersAsync(context, ct);
            var formatError = ValidateJsonFormat(context, parameters);
            if (formatError != null)
            {
                return formatError;
            }

            var envError = TryParseEnvControls(context, logger, parameters, out var envControls);
            if (envError != null)
            {
                return envError;
            }

            var processCatalog = context.RequestServices.GetRequiredService<IProcessCatalog>();
            var definition = ResolveTaskDefinition(processCatalog, taskName);
            if (definition == null)
            {
                GPServerLog.TaskResolutionUnavailable(logger, serviceId, taskName);
                return SetSpanErrorAndReturn(
                    StandardErrorHelpers.CreateNotFound(
                        context,
                        $"Task '{taskName}' on service '{serviceId}' was not found."),
                    "Task not found");
            }

            // Respect the task's ExecutionType: only sync-eligible tasks may run
            // inline. Async-only tasks get a clear, correct capability message
            // pointing at submitJob instead of a faked synchronous result.
            if (!GPServerExecutionPolicy.IsSynchronous(definition))
            {
                return SetSpanErrorAndReturn(
                    StandardErrorHelpers.CreateBadRequest(context,
                        $"Task '{definition.ProcessId}' is asynchronous ({GPServerExecutionPolicy.AsynchronousExecutionType}). " +
                        "Use the submitJob route and poll the job status; the synchronous execute route is only " +
                        "available for sync-eligible (deterministic single-geometry) tasks."),
                    "Task is not sync-eligible");
            }

            var planResult = BuildSubmissionPlan(definition, serviceId, parameters);
            if (planResult.CapabilityError is not null)
            {
                return SetSpanErrorAndReturn(
                    StandardErrorHelpers.CreateBadRequest(context, planResult.CapabilityError),
                    "FeatureSet requires feature-collection execution");
            }

            var protocolMetadata = BuildProtocolMetadata(serviceId, taskName, definition, parameters, envControls);
            var job = await jobService.SubmitJobAsync(
                planResult.Plan!,
                idempotencyKey: null,
                context.User,
                protocolMetadata,
                ct);

            // Synchronous contract: block until the canonical runtime reaches a
            // terminal state, then return results inline. Execution itself flows
            // through the same job runtime as submitJob — sync is a protocol
            // projection, not a separate engine.
            var terminal = await PollUntilTerminalAsync(jobService, job.OperationId, context.User, ct);

            if (terminal.Status == ExecutionJobStatus.Failed)
            {
                return BuildExecuteFailureResponse(terminal, "esriJobFailed");
            }

            if (terminal.Status == ExecutionJobStatus.Cancelled)
            {
                return BuildExecuteFailureResponse(terminal, "esriJobCancelled");
            }

            var workingSrid = ResolveWorkingSrid(parameters, planResult.InputSpatialReference);
            return await BuildExecuteSuccessResponseAsync(
                jobService, terminal, context.User, envControls, workingSrid, ct);
        }
        catch (TimeoutException)
        {
            return SetSpanErrorAndReturn(
                StandardErrorHelpers.CreateRequestTimeout(context,
                    "Synchronous GP execution did not complete within the request timeout. " +
                    "Use submitJob for long-running execution."),
                "Synchronous execution timed out");
        }
        catch (Exception ex)
        {
            return MapExceptionToResult(context, logger, "Execute", ex);
        }
    }

    /// <summary>
    /// Polls the job service until the job reaches a terminal state. The
    /// canonical runtime executes the job out-of-band (worker host); the
    /// synchronous protocol contract blocks the request thread until completion
    /// or until the request-scoped cancellation token trips.
    /// </summary>
    private static async Task<ExecutionJobRecord> PollUntilTerminalAsync(
        IGeoprocessingJobService jobService,
        string jobId,
        System.Security.Claims.ClaimsPrincipal user,
        CancellationToken ct)
    {
        var delay = TimeSpan.FromMilliseconds(50);
        var maxDelay = TimeSpan.FromMilliseconds(500);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var job = await jobService.GetJobAsync(jobId, user, ct);
            if (GeoprocessingJobService.IsTerminal(job.Status))
            {
                return job;
            }

            await Task.Delay(delay, ct);
            delay = delay < maxDelay ? delay + delay : maxDelay;
        }
    }

    private static IResult BuildExecuteFailureResponse(ExecutionJobRecord job, string esriStatus)
    {
        var messages = new List<GPJobMessage>();
        if (job.ErrorMessage != null)
        {
            messages.Add(new GPJobMessage
            {
                Type = "esriJobMessageTypeError",
                Description = job.ErrorMessage
            });
        }

        var response = new GPExecuteResponse
        {
            Results = [],
            Messages = [.. messages],
            JobStatus = esriStatus
        };

        return SetSpanErrorAndReturn(
            Results.Json(response, GPServerJsonContext.Default.GPExecuteResponse, contentType: "application/json"),
            $"Synchronous execution {esriStatus}");
    }

    private static async Task<IResult> BuildExecuteSuccessResponseAsync(
        IGeoprocessingJobService jobService,
        ExecutionJobRecord job,
        System.Security.Claims.ClaimsPrincipal user,
        EnvControls envControls,
        int workingSrid,
        CancellationToken ct)
    {
        var results = new List<GPResultResponse>();
        var messages = new List<GPJobMessage>();

        foreach (var warning in job.Warnings)
        {
            messages.Add(new GPJobMessage { Type = "esriJobMessageTypeWarning", Description = warning });
        }

        AnalysisResultPackage? resultPackage = null;
        try
        {
            resultPackage = await jobService.GetJobResultsAsync(job.OperationId, user, ct);
        }
        catch (GeoprocessingNotFoundException)
        {
            // No persisted result package — return an empty results envelope with
            // an informative message rather than a fabricated value.
            messages.Add(new GPJobMessage
            {
                Type = "esriJobMessageTypeInformative",
                Description = "Job succeeded but no result package was available for inline retrieval."
            });
        }

        if (resultPackage != null)
        {
            var allKinds = resultPackage.Artifacts.Select(a => a.Kind).ToArray();
            for (var index = 0; index < resultPackage.Artifacts.Count; index++)
            {
                var artifact = resultPackage.Artifacts[index];
                var paramName = ResolvePublishedOutputParameterName(job, artifact, index, allKinds);
                var dataType = GPServerParameterTranslation.ToEsriDataType(artifact.Kind);
                var value = artifact.Uri ?? artifact.Label;

                if (envControls.OutSr is { } outSr && artifact.Kind == ArtifactKind.FeatureLayer)
                {
                    var outcome = GPServerOutputReprojection.TryReprojectGeoJsonValue(value, workingSrid, outSr);
                    if (outcome.Reprojected)
                    {
                        value = outcome.Value;
                    }
                    else if (outcome.CapabilityMessage is not null)
                    {
                        messages.Add(new GPJobMessage
                        {
                            Type = "esriJobMessageTypeWarning",
                            Description = outcome.CapabilityMessage
                        });
                    }
                }
                else if (envControls.OutSr is { } requestedSr && artifact.Kind != ArtifactKind.FeatureLayer)
                {
                    messages.Add(new GPJobMessage
                    {
                        Type = "esriJobMessageTypeWarning",
                        Description =
                            $"env:outSR={requestedSr} was ignored: output '{paramName}' is a " +
                            $"{artifact.Kind} and is not a reprojectable geometry."
                    });
                }

                results.Add(new GPResultResponse
                {
                    ParamName = paramName,
                    DataType = dataType,
                    Value = value
                });
            }
        }

        var response = new GPExecuteResponse
        {
            Results = [.. results],
            Messages = messages.Count > 0 ? [.. messages] : null,
            JobStatus = "esriJobSucceeded"
        };

        return Results.Json(response, GPServerJsonContext.Default.GPExecuteResponse, contentType: "application/json");
    }

    private static async Task<IResult> HandleJobStatus(HttpContext context, CancellationToken ct)
    {
        ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var serviceId = context.Request.RouteValues["serviceId"]?.ToString() ?? "";
        var taskName = context.Request.RouteValues["taskName"]?.ToString() ?? "";
        EnrichActivity("JobStatus", serviceId, taskName);
        var jobId = context.Request.RouteValues["jobId"]?.ToString();
        var logger = ResolveLogger(context);

        if (string.IsNullOrWhiteSpace(jobId))
        {
            return SetSpanErrorAndReturn(
                StandardErrorHelpers.CreateBadRequest(context, "Missing jobId."),
                "Missing jobId");
        }

        var formatError = ValidateJsonFormat(context);
        if (formatError != null)
        {
            return formatError;
        }

        var jobService = context.RequestServices.GetRequiredService<IGeoprocessingJobService>();

        try
        {
            var serviceValidation = await ValidateServiceAsync(context, serviceId, logger, ct);
            if (!serviceValidation.IsValid)
            {
                return serviceValidation.ErrorResult!;
            }

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
                results = new Dictionary<string, GPJobResultRef>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var resultPackage = await jobService.GetJobResultsAsync(jobId, context.User, ct);
                    foreach (var outputName in ResolvePublishedOutputParameterNames(job, resultPackage))
                    {
                        results[outputName] = new GPJobResultRef
                        {
                            ParamUrl = $"results/{Uri.EscapeDataString(outputName)}"
                        };
                    }
                }
                catch (GeoprocessingNotFoundException)
                {
                    // Result-package persistence is not yet universal across all
                    // execution backends. Preserve the GPServer shape with an empty
                    // results dictionary until a package becomes available.
                }
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
        ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var serviceId = context.Request.RouteValues["serviceId"]?.ToString() ?? "";
        var taskName = context.Request.RouteValues["taskName"]?.ToString() ?? "";
        EnrichActivity("JobResult", serviceId, taskName);
        var jobId = context.Request.RouteValues["jobId"]?.ToString();
        var paramName = context.Request.RouteValues["paramName"]?.ToString();
        var logger = ResolveLogger(context);

        if (string.IsNullOrWhiteSpace(jobId))
        {
            return SetSpanErrorAndReturn(
                StandardErrorHelpers.CreateBadRequest(context, "Missing jobId."),
                "Missing jobId");
        }

        if (string.IsNullOrWhiteSpace(paramName))
        {
            return SetSpanErrorAndReturn(
                StandardErrorHelpers.CreateBadRequest(context, "Missing paramName."),
                "Missing paramName");
        }

        GPServerLog.JobResultRequested(logger, jobId, paramName);
        var formatError = ValidateJsonFormat(context);
        if (formatError != null)
        {
            return formatError;
        }

        var jobService = context.RequestServices.GetRequiredService<IGeoprocessingJobService>();

        try
        {
            var serviceValidation = await ValidateServiceAsync(context, serviceId, logger, ct);
            if (!serviceValidation.IsValid)
            {
                return serviceValidation.ErrorResult!;
            }

            // Validate route binding before accessing results.
            var job = await jobService.GetJobAsync(jobId, context.User, ct);
            var bindingError = ValidateJobBinding(context, logger, job, serviceId, taskName);
            if (bindingError != null)
            {
                return bindingError;
            }

            var results = await jobService.GetJobResultsAsync(jobId, context.User, ct);
            var artifact = ResolveArtifactByPublishedOutputName(job, results, paramName, out var publishedName);

            if (artifact == null)
            {
                return SetSpanErrorAndReturn(
                    StandardErrorHelpers.CreateNotFound(context,
                        $"Output parameter '{paramName}' not found in job results."),
                    $"Output parameter '{paramName}' not found");
            }

            var response = new GPResultResponse
            {
                ParamName = publishedName,
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
        ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var serviceId = context.Request.RouteValues["serviceId"]?.ToString() ?? "";
        var taskName = context.Request.RouteValues["taskName"]?.ToString() ?? "";
        EnrichActivity("CancelJob", serviceId, taskName);
        var jobId = context.Request.RouteValues["jobId"]?.ToString();
        var logger = ResolveLogger(context);

        if (string.IsNullOrWhiteSpace(jobId))
        {
            return SetSpanErrorAndReturn(
                StandardErrorHelpers.CreateBadRequest(context, "Missing jobId."),
                "Missing jobId");
        }

        GPServerLog.JobCancelRequested(logger, jobId);
        var formatError = ValidateJsonFormat(context);
        if (formatError != null)
        {
            return formatError;
        }

        var jobService = context.RequestServices.GetRequiredService<IGeoprocessingJobService>();

        try
        {
            var serviceValidation = await ValidateServiceAsync(context, serviceId, logger, ct);
            if (!serviceValidation.IsValid)
            {
                return serviceValidation.ErrorResult!;
            }

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

    private static Task<ServiceResourceValidationHelpers.ServiceValidationV2Result> ValidateServiceAsync(
        HttpContext context,
        string serviceId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        return ServiceResourceValidationHelpers.ValidateServiceV2Async(
            resourceValidator,
            serviceId,
            ProtocolName,
            context,
            id => GPServerLog.ServiceNotFound(logger, id),
            requireServiceAccess: true,
            cancellationToken);
    }

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
        var storedService = job.Spec.Parameters.GetValueOrDefault(GeoprocessingProtocolMetadataKeys.GPServerServiceId);
        var storedTask = job.Spec.Parameters.GetValueOrDefault(GeoprocessingProtocolMetadataKeys.GPServerTaskName);

        // Jobs without GPServer binding metadata were not submitted through GPServer.
        // Reject them to prevent cross-protocol job access.
        if (storedService == null && storedTask == null)
        {
            GPServerLog.JobBindingMismatch(logger, job.OperationId, serviceId, taskName);
            return SetSpanErrorAndReturn(
                StandardErrorHelpers.CreateNotFound(context,
                    $"Job '{job.OperationId}' does not belong to service '{serviceId}' task '{taskName}'."),
                "Job binding mismatch");
        }

        if (string.Equals(storedService, serviceId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(storedTask, taskName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        GPServerLog.JobBindingMismatch(logger, job.OperationId, serviceId, taskName);
        return SetSpanErrorAndReturn(
            StandardErrorHelpers.CreateNotFound(context,
                $"Job '{job.OperationId}' does not belong to service '{serviceId}' task '{taskName}'."),
            "Job binding mismatch");
    }

    /// <summary>
    /// Rejects unsupported GP environment controls (<c>env:outSR</c>, <c>env:processSR</c>)
    /// with a structured 400 error instead of silently stripping them.
    /// Returns null when no unsupported controls are present.
    /// <para>
    /// This guard backs the ESTABLISHED async <c>submitJob</c> contract (cross-repo
    /// integration tests assert env:* → 400). Honoring <c>env:outSR</c> /
    /// <c>env:processSR</c> is an additive feature of the synchronous <c>execute</c>
    /// route only, which uses <see cref="TryParseEnvControls"/> instead. See #1228.
    /// </para>
    /// </summary>
    private static IResult? RejectUnsupportedEnvControls(
        HttpContext context, ILogger logger, IReadOnlyDictionary<string, string> allParams)
    {
        List<string>? unsupported = null;
        foreach (var key in allParams.Keys)
        {
            if (key.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
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
        return SetSpanErrorAndReturn(
            StandardErrorHelpers.CreateBadRequest(context,
                $"GP environment controls are not yet supported: {names}. " +
                "Remove these parameters or wait for engine support."),
            $"Unsupported GP env controls: {names}");
    }

    /// <summary>
    /// Parsed GP environment controls. <c>env:outSR</c> and <c>env:processSR</c>
    /// are honored (output reprojection / informational); any other <c>env:*</c>
    /// control is unsupported and surfaced for a 400 response.
    /// </summary>
    private readonly record struct EnvControls(int? OutSr, int? ProcessSr);

    /// <summary>
    /// Parses GP environment controls. <c>env:outSR</c> and <c>env:processSR</c>
    /// are recognised and returned (and no longer rejected); any other
    /// <c>env:*</c> control yields a structured 400. SR values may be a bare
    /// WKID or a spatial-reference JSON object (<c>{ "wkid": 3857 }</c>).
    /// </summary>
    private static IResult? TryParseEnvControls(
        HttpContext context,
        ILogger logger,
        IReadOnlyDictionary<string, string> allParams,
        out EnvControls controls)
    {
        controls = default;
        int? outSr = null;
        int? processSr = null;
        List<string>? unsupported = null;

        foreach (var (key, value) in allParams)
        {
            if (!key.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(key, "env:outSR", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseSpatialReferenceValue(value, out outSr))
                {
                    return SetSpanErrorAndReturn(
                        StandardErrorHelpers.CreateBadRequest(context,
                            $"env:outSR value '{value}' is not a valid WKID or spatial-reference object."),
                        "Invalid env:outSR");
                }
            }
            else if (string.Equals(key, "env:processSR", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseSpatialReferenceValue(value, out processSr))
                {
                    return SetSpanErrorAndReturn(
                        StandardErrorHelpers.CreateBadRequest(context,
                            $"env:processSR value '{value}' is not a valid WKID or spatial-reference object."),
                        "Invalid env:processSR");
                }
            }
            else
            {
                unsupported ??= [];
                unsupported.Add(key);
            }
        }

        if (unsupported != null)
        {
            var names = string.Join(", ", unsupported);
            GPServerLog.UnsupportedEnvControlsRejected(logger, names);
            return SetSpanErrorAndReturn(
                StandardErrorHelpers.CreateBadRequest(context,
                    $"GP environment controls are not yet supported: {names}. " +
                    "Remove these parameters or wait for engine support."),
                $"Unsupported GP env controls: {names}");
        }

        controls = new EnvControls(outSr, processSr);
        return null;
    }

    private static bool TryParseSpatialReferenceValue(string? value, out int? srid)
    {
        srid = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var trimmed = value.Trim();
        if (int.TryParse(trimmed, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var bare))
        {
            srid = bare;
            return true;
        }

        if (trimmed[0] == '{')
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    if (root.TryGetProperty("wkid", out var wkid) &&
                        wkid.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        srid = wkid.GetInt32();
                        return true;
                    }

                    if (root.TryGetProperty("latestWkid", out var latest) &&
                        latest.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        srid = latest.GetInt32();
                        return true;
                    }
                }
            }
            catch (System.Text.Json.JsonException)
            {
                return false;
            }
        }

        return false;
    }

    private static IResult? ValidateFormPostContentType(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method) ||
            context.Request.ContentLength is null or 0 ||
            context.Request.HasFormContentType)
        {
            return null;
        }

        var mediaType = string.IsNullOrWhiteSpace(context.Request.ContentType)
            ? "(missing)"
            : context.Request.ContentType.Split(';', 2)[0].Trim();

        return ValidationErrorHelpers.CreateUnsupportedMediaType(context, mediaType, FormContentTypes);
    }

    private static async Task<IResult?> ValidateMetadataJsonFormatAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var contentTypeError = ValidateFormPostContentType(context);
        if (contentTypeError is not null)
        {
            return contentTypeError;
        }

        if (HttpMethods.IsPost(context.Request.Method) &&
            context.Request.ContentLength is > 0)
        {
            var parameters = await GPServerParameterTranslation.ReadRequestParametersAsync(context, cancellationToken);
            return ValidateJsonFormat(context, parameters);
        }

        return ValidateJsonFormat(context);
    }

    private static ProcessDefinition? ResolveTaskDefinition(IProcessCatalog processCatalog, string? taskName)
        => string.IsNullOrWhiteSpace(taskName) ? null : processCatalog.GetProcess(taskName);

    private static GPTaskInfoResponse BuildTaskInfo(ProcessDefinition definition)
    {
        var parameters = new List<GPParameterInfo>(definition.Parameters.Count + definition.OutputArtifactKinds.Count);
        foreach (var parameter in definition.Parameters)
        {
            parameters.Add(new GPParameterInfo
            {
                Name = parameter.Name,
                DisplayName = parameter.DisplayName,
                Description = parameter.Description,
                DataType = GPServerParameterTranslation.ToEsriDataType(parameter.ValueType),
                Direction = "esriGPParameterDirectionInput",
                DefaultValue = parameter.DefaultValue,
                ParameterType = parameter.Required
                    ? "esriGPParameterTypeRequired"
                    : "esriGPParameterTypeOptional"
            });
        }

        for (var index = 0; index < definition.OutputArtifactKinds.Count; index++)
        {
            var kind = definition.OutputArtifactKinds[index];
            var outputName = BuildOutputParameterName(kind, index, definition.OutputArtifactKinds);
            parameters.Add(new GPParameterInfo
            {
                Name = outputName,
                DisplayName = outputName,
                Description = $"Output artifact of type {kind}.",
                DataType = GPServerParameterTranslation.ToEsriDataType(kind),
                Direction = "esriGPParameterDirectionOutput",
                ParameterType = "esriGPParameterTypeRequired"
            });
        }

        return new GPTaskInfoResponse
        {
            Name = definition.ProcessId,
            DisplayName = definition.Title,
            Description = definition.Description,
            Category = definition.Category,
            HelpUrl = string.Empty,
            // ADVERTISED contract stays asynchronous (the value trunk advertised and
            // that cross-repo integration tests assert). The synchronous `execute`
            // route is purely additive capability and gates itself via
            // GPServerExecutionPolicy.IsSynchronous; it does not change advertised
            // task/service metadata. See #1228.
            ExecutionType = "esriExecutionTypeAsynchronous",
            Parameters = [.. parameters]
        };
    }

    /// <summary>
    /// Result of building a submission plan: either an executable
    /// <see cref="AnalysisPlan"/> (with the working input SRID, when derivable
    /// from an esriGeometry/FeatureSet) or a capability error explaining why the
    /// supplied inputs cannot be translated to the canonical contract.
    /// </summary>
    private readonly record struct SubmissionPlanResult(
        AnalysisPlan? Plan,
        string? CapabilityError,
        int? InputSpatialReference);

    private static SubmissionPlanResult BuildSubmissionPlan(
        ProcessDefinition definition,
        string serviceId,
        IReadOnlyDictionary<string, string> rawParameters)
    {
        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in rawParameters)
        {
            if (IsProtocolControlParameter(key))
            {
                continue;
            }

            inputs[key] = value;
        }

        // Additive ArcGIS-compatible input translation: rewrite esriGeometry JSON
        // and single-feature FeatureSet payloads into canonical base64-WKB + srid.
        // Native string / base64-WKB inputs pass through untouched. Multi-feature
        // FeatureSets surface a capability error rather than dropping features.
        var esriResult = GPServerEsriInputTranslation.Translate(inputs);
        if (esriResult.CapabilityMessage is not null)
        {
            return new SubmissionPlanResult(Plan: null, esriResult.CapabilityMessage, esriResult.InputSpatialReference);
        }

        var translatedInputs = GPServerParameterTranslation.TranslateInbound(esriResult.Inputs);
        var taskSlug = definition.ProcessId.Replace(".", "-", StringComparison.Ordinal);

        var plan = new AnalysisPlan
        {
            PlanId = $"gpserver-{serviceId}-{taskSlug}-{Guid.NewGuid():N}",
            IntentId = $"gpserver:{serviceId}:{definition.ProcessId}",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = $"gp-task-{taskSlug}",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = definition.ProcessId,
                    Inputs = translatedInputs
                }
            ],
            Outputs = definition.OutputArtifactKinds
        };

        return new SubmissionPlanResult(plan, CapabilityError: null, esriResult.InputSpatialReference);
    }

    /// <summary>
    /// Resolves the working SRID for output reprojection: the explicit canonical
    /// <c>srid</c>/<c>toSrid</c>/<c>targetSrid</c> input if present, otherwise the
    /// SRID derived from a translated esriGeometry/FeatureSet input.
    /// </summary>
    private static int ResolveWorkingSrid(
        IReadOnlyDictionary<string, string> rawParameters,
        int? derivedSrid)
    {
        foreach (var key in new[] { "srid", "toSrid", "targetSrid", "outSr" })
        {
            if (rawParameters.TryGetValue(key, out var value) &&
                int.TryParse(value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed) &&
                parsed > 0)
            {
                return parsed;
            }
        }

        return derivedSrid ?? 0;
    }

    private static Dictionary<string, string> BuildProtocolMetadata(
        string serviceId,
        string taskName,
        ProcessDefinition definition,
        IReadOnlyDictionary<string, string> rawParameters,
        EnvControls envControls)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["submittedVia"] = "GPServer",
            [GeoprocessingProtocolMetadataKeys.GPServerServiceId] = serviceId,
            [GeoprocessingProtocolMetadataKeys.GPServerTaskName] = taskName
        };

        for (var index = 0; index < definition.OutputArtifactKinds.Count; index++)
        {
            var outputName = BuildOutputParameterName(
                definition.OutputArtifactKinds[index],
                index,
                definition.OutputArtifactKinds);
            metadata[$"{GeoprocessingProtocolMetadataKeys.OutputNamePrefix}{index}"] = outputName;
            metadata[$"{GeoprocessingProtocolMetadataKeys.GPServerOutputNamePrefix}{index}"] = outputName;
        }

        if (rawParameters.TryGetValue("context", out var contextValue) &&
            !string.IsNullOrWhiteSpace(contextValue))
        {
            metadata[GeoprocessingProtocolMetadataKeys.GPServerContext] = contextValue;
        }

        if (envControls.OutSr is { } outSr)
        {
            metadata[GeoprocessingProtocolMetadataKeys.GPServerOutSr] =
                outSr.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (envControls.ProcessSr is { } processSr)
        {
            metadata[GeoprocessingProtocolMetadataKeys.GPServerProcessSr] =
                processSr.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return metadata;
    }

    private static bool IsProtocolControlParameter(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return true;
        }

        return string.Equals(key, "f", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "token", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "context", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("env:", StringComparison.OrdinalIgnoreCase);
    }

    private static IResult? ValidateJsonFormat(
        HttpContext context,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        string? format = null;
        if (parameters?.TryGetValue("f", out var parameterFormat) == true)
        {
            format = parameterFormat;
        }
        else if (context.Request.Query.TryGetValue("f", out var queryFormat))
        {
            format = queryFormat.ToString();
        }

        if (string.IsNullOrWhiteSpace(format) ||
            format.Equals("json", StringComparison.OrdinalIgnoreCase) ||
            format.Equals("pjson", StringComparison.OrdinalIgnoreCase) ||
            format.Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return SetSpanErrorAndReturn(
            StandardErrorHelpers.CreateBadRequest(
                context,
                "Unsupported output format",
                [$"Format '{format}' is not supported. Use f=json."]),
            "Unsupported GPServer output format");
    }

    private static string BuildOutputParameterName(
        ArtifactKind kind,
        int index,
        IReadOnlyList<ArtifactKind> allKinds)
    {
        var baseName = kind switch
        {
            ArtifactKind.FeatureLayer => "outputFeatureLayer",
            ArtifactKind.Table => "outputTable",
            ArtifactKind.Raster => "outputRaster",
            ArtifactKind.File => "outputFile",
            ArtifactKind.Report => "outputReport",
            ArtifactKind.Map => "outputMap",
            ArtifactKind.Scalar => "outputScalar",
            ArtifactKind.AppBundle => "outputBundle",
            _ => "output"
        };

        var duplicateCount = allKinds.Count(candidate => candidate == kind);
        if (duplicateCount <= 1)
        {
            return baseName;
        }

        var ordinal = 1;
        for (var i = 0; i <= index; i++)
        {
            if (allKinds[i] == kind)
            {
                ordinal++;
            }
        }

        return $"{baseName}{ordinal - 1}";
    }

    private static string[] ResolvePublishedOutputParameterNames(
        ExecutionJobRecord job,
        AnalysisResultPackage resultPackage)
    {
        if (resultPackage.Artifacts.Count == 0)
        {
            return [];
        }

        var allKinds = resultPackage.Artifacts.Select(artifact => artifact.Kind).ToArray();
        var names = new string[resultPackage.Artifacts.Count];
        for (var index = 0; index < resultPackage.Artifacts.Count; index++)
        {
            names[index] = ResolvePublishedOutputParameterName(
                job,
                resultPackage.Artifacts[index],
                index,
                allKinds);
        }

        return names;
    }

    private static ArtifactRef? ResolveArtifactByPublishedOutputName(
        ExecutionJobRecord job,
        AnalysisResultPackage resultPackage,
        string requestedParamName,
        out string? publishedName)
    {
        var allKinds = resultPackage.Artifacts.Select(artifact => artifact.Kind).ToArray();
        for (var index = 0; index < resultPackage.Artifacts.Count; index++)
        {
            var artifact = resultPackage.Artifacts[index];
            var outputName = ResolvePublishedOutputParameterName(job, artifact, index, allKinds);
            if (!string.Equals(outputName, requestedParamName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            publishedName = outputName;
            return artifact;
        }

        publishedName = null;
        return null;
    }

    private static string ResolvePublishedOutputParameterName(
        ExecutionJobRecord job,
        ArtifactRef artifact,
        int index,
        IReadOnlyList<ArtifactKind> allKinds)
    {
        if (artifact.Metadata.TryGetValue(
                GPServerParameterTranslation.OutputParameterMetadataKey,
                out var metadataName) &&
            !string.IsNullOrWhiteSpace(metadataName))
        {
            return metadataName;
        }

        if (job.Spec.Parameters.TryGetValue($"{GeoprocessingProtocolMetadataKeys.OutputNamePrefix}{index}", out var storedName) &&
            !string.IsNullOrWhiteSpace(storedName))
        {
            return storedName;
        }

        if (job.Spec.Parameters.TryGetValue($"{GeoprocessingProtocolMetadataKeys.GPServerOutputNamePrefix}{index}", out storedName) &&
            !string.IsNullOrWhiteSpace(storedName))
        {
            return storedName;
        }

        return BuildOutputParameterName(artifact.Kind, index, allKinds);
    }

    private static IResult MapExceptionToResult(
        HttpContext context, ILogger logger, string operation, Exception ex)
    {
        HonuaTelemetry.RecordException(Activity.Current, ex);

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
                    StandardErrorHelpers.CreatePreconditionFailed(context, preconditionEx.Message)),

            GeoprocessingValidationException validationEx =>
                LogAndReturn(logger, operation, validationEx.Message,
                    StandardErrorHelpers.CreateBadRequest(context, validationEx.Message)),

            GeoprocessingStoreUnavailableException storeEx =>
                LogAndReturn(logger, operation, storeEx.Message,
                    StandardErrorHelpers.CreateServiceUnavailable(context, storeEx.Message)),

            GeoprocessingIdempotencyConflictException conflictEx =>
                LogAndReturn(logger, operation, conflictEx.Message,
                    StandardErrorHelpers.CreateConflict(context, conflictEx.Message)),

            GeoprocessingAdmissionException admissionEx =>
                LogAndReturn(logger, operation, admissionEx.Message,
                    StandardErrorHelpers.CreateServiceUnavailable(
                        context, admissionEx.Message, admissionEx.RetryAfterSeconds)),

            TimeoutException timeoutEx =>
                LogAndReturn(logger, operation, timeoutEx.Message,
                    StandardErrorHelpers.CreateRequestTimeout(context, timeoutEx.Message)),

            OperationCanceledException canceledEx =>
                LogAndReturn(logger, operation, canceledEx.Message,
                    StandardErrorHelpers.CreateRequestTimeout(context, canceledEx.Message)),

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

    /// <summary>
    /// Tags the current <see cref="Activity"/> as errored and returns the result unchanged.
    /// Used for non-exception error paths (400/404/501) so that spans carry
    /// <c>error=true</c> and <c>ActivityStatusCode.Error</c> alongside the protocol/operation tags.
    /// </summary>
    private static IResult SetSpanErrorAndReturn(IResult result, string? message = null)
    {
        var activity = Activity.Current;
        if (activity != null)
        {
            activity.SetStatus(ActivityStatusCode.Error, message);
            activity.SetTag(HonuaTelemetry.Tags.Error, true);
        }

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
            activity.SetTag(HonuaTelemetry.Tags.TaskName, taskName);
        }
    }

    private static ILogger ResolveLogger(HttpContext context)
        => context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Honua.Server.GPServerEndpoints");
}

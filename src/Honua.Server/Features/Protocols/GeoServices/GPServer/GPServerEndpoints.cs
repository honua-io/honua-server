// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.GeoServices.GPServer.Models;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.Protocols.GeoServices.GPServer;

/// <summary>
/// Maps GeoServices GPServer REST endpoints as a protocol adapter
/// over the canonical process runtime.
/// </summary>
internal static class GPServerEndpoints
{
    private const string RouteBase = "/rest/services/{serviceId}/GPServer";
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

        endpoints.MapPost(RouteBase,
                static (HttpContext context, CancellationToken ct) => HandleServiceInfo(context, ct))
            .WithDisplayName("GPServer Service Info (POST)")
            .WithName("GPServerServiceInfoPost")
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

        endpoints.MapPost($"{RouteBase}/{{taskName}}",
                static (HttpContext context, CancellationToken ct) => HandleTaskInfo(context, ct))
            .WithDisplayName("GPServer Task Info (POST)")
            .WithName("GPServerTaskInfoPost")
            .WithSummary("Get GP task metadata")
            .WithDescription("Returns metadata about a specific GP task including parameters")
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
            // with a clear 400 instead of silently stripping them.
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

            var plan = BuildSubmissionPlan(definition, serviceId, parameters);
            var protocolMetadata = BuildProtocolMetadata(serviceId, taskName, definition, parameters);
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

    private static Task<ServiceResourceValidationHelpers.ServiceValidationResult> ValidateServiceAsync(
        HttpContext context,
        string serviceId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
        return ServiceResourceValidationHelpers.ValidateServiceAsync(
            resourceValidator,
            serviceId,
            ServiceProtocols.GPServer,
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
            ExecutionType = "esriExecutionTypeAsynchronous",
            Parameters = [.. parameters]
        };
    }

    private static AnalysisPlan BuildSubmissionPlan(
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

        var translatedInputs = GPServerParameterTranslation.TranslateInbound(inputs);
        var taskSlug = definition.ProcessId.Replace(".", "-", StringComparison.Ordinal);

        return new AnalysisPlan
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
    }

    private static Dictionary<string, string> BuildProtocolMetadata(
        string serviceId,
        string taskName,
        ProcessDefinition definition,
        IReadOnlyDictionary<string, string> rawParameters)
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

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Ogc.Common;
using Honua.Server.Features.OgcProcesses.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.OgcProcesses;

/// <summary>
/// OGC API Processes process discovery and execution endpoints.
/// </summary>
internal static class ProcessEndpoints
{
    private const string BasePath = "/ogc/processes";
    private const string Tag = "OGC API Processes";

    // V1 stub: single canonical process representing the Honua geoprocessing runtime.
    // CatalogService formalization is follow-on work; this stub allows the OGC adapter
    // to serve a valid process list and description while the catalog is being built.
    private const string CanonicalProcessId = "honua-geoprocessing";

    private static readonly OgcProcessSummary CanonicalProcessSummary = new()
    {
        Id = CanonicalProcessId,
        Title = "Honua Geoprocessing",
        Description = "Executes an analysis plan through the Honua canonical geoprocessing runtime.",
        Version = "1.0.0",
        JobControlOptions = ImmutableArray.Create("async-execute", "dismiss"),
        OutputTransmission = ImmutableArray.Create("value")
    };

    private static readonly OgcProcessDescription CanonicalProcessDescription = new()
    {
        Id = CanonicalProcessId,
        Title = "Honua Geoprocessing",
        Description = "Executes an analysis plan through the Honua canonical geoprocessing runtime. " +
                      "Accepts a plan specification with steps, inputs, and output expectations. " +
                      "Returns artifacts via the job results endpoint once execution completes.",
        Version = "1.0.0",
        JobControlOptions = ImmutableArray.Create("async-execute", "dismiss"),
        OutputTransmission = ImmutableArray.Create("value"),
        Inputs = ImmutableDictionary.CreateRange(new[]
        {
            KeyValuePair.Create("plan", new OgcProcessIoDescription
            {
                Title = "Analysis Plan",
                Description = "JSON-encoded analysis plan with steps, inputs, and output expectations.",
                Schema = new OgcProcessIoSchema { Type = "object", ContentMediaType = "application/json" }
            })
        }),
        Outputs = ImmutableDictionary.CreateRange(new[]
        {
            KeyValuePair.Create("results", new OgcProcessIoDescription
            {
                Title = "Results",
                Description = "Analysis result package containing artifacts produced by the plan execution.",
                Schema = new OgcProcessIoSchema { Type = "object", ContentMediaType = "application/json" }
            })
        })
    };

    public static void MapOgcProcessesProcessEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet($"{BasePath}/processes", GetProcessList)
            .WithTags(Tag)
            .WithName("OgcProcessesList")
            .WithSummary("List available processes")
            .Produces<OgcProcessList>()
            .ExcludeFromDescription();

        endpoints.MapGet($"{BasePath}/processes/{{processId}}", GetProcessDescription)
            .WithTags(Tag)
            .WithName("OgcProcessDescription")
            .WithSummary("Get process description")
            .Produces<OgcProcessDescription>()
            .Produces<OgcProcessError>(StatusCodes.Status404NotFound)
            .ExcludeFromDescription();

        endpoints.MapPost($"{BasePath}/processes/{{processId}}/execution", ExecuteProcess)
            .WithTags(Tag)
            .WithName("OgcProcessExecute")
            .WithSummary("Execute a process")
            .Accepts<OgcExecuteRequest>(MediaTypes.Json)
            .Produces<OgcStatusInfo>(StatusCodes.Status201Created)
            .Produces<OgcProcessError>(StatusCodes.Status404NotFound)
            .Produces<OgcProcessError>(StatusCodes.Status501NotImplemented)
            .ExcludeFromDescription();
    }

    private static IResult GetProcessList(
        HttpContext context,
        ILogger<OgcProcessesEndpointsLog> logger)
    {
        EnrichActivity("GetProcessList");
        OgcProcessesLog.ProcessListRequested(logger);

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var summary = CanonicalProcessSummary with
        {
            Links = ImmutableArray.Create(
                Link.Create(
                    $"{baseUrl}{BasePath}/processes/{CanonicalProcessId}",
                    RelationTypes.Self,
                    MediaTypes.Json,
                    "Process description"))
        };

        var processList = new OgcProcessList
        {
            Processes = ImmutableArray.Create(summary),
            Links = ImmutableArray.Create(
                Link.Create($"{baseUrl}{BasePath}/processes", RelationTypes.Self, MediaTypes.Json, "This document"))
        };

        return Results.Json(processList, OgcProcessesJsonContext.Default.OgcProcessList, MediaTypes.Json);
    }

    private static IResult GetProcessDescription(
        string processId,
        HttpContext context,
        ILogger<OgcProcessesEndpointsLog> logger)
    {
        EnrichActivity("GetProcess");
        OgcProcessesLog.ProcessDescriptionRequested(logger, processId);

        if (!string.Equals(processId, CanonicalProcessId, StringComparison.OrdinalIgnoreCase))
        {
            OgcProcessesLog.ProcessNotFound(logger, processId);
            return Results.Json(
                new OgcProcessError
                {
                    Type = "http://www.opengis.net/def/exceptions/ogcapi-processes-1/1.0/no-such-process",
                    Title = "No such process",
                    Status = StatusCodes.Status404NotFound,
                    Detail = $"Process '{processId}' does not exist."
                },
                OgcProcessesJsonContext.Default.OgcProcessError,
                MediaTypes.Json,
                StatusCodes.Status404NotFound);
        }

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var description = CanonicalProcessDescription with
        {
            Links = ImmutableArray.Create(
                Link.Create($"{baseUrl}{BasePath}/processes/{CanonicalProcessId}", RelationTypes.Self, MediaTypes.Json, "This document"),
                Link.Create($"{baseUrl}{BasePath}/processes/{CanonicalProcessId}/execution", "http://www.opengis.net/def/rel/ogc/1.0/execute", MediaTypes.Json, "Execute process"))
        };

        return Results.Json(description, OgcProcessesJsonContext.Default.OgcProcessDescription, MediaTypes.Json);
    }

    private static async Task<IResult> ExecuteProcess(
        string processId,
        [FromBody] OgcExecuteRequest request,
        HttpContext context,
        ILogger<OgcProcessesEndpointsLog> logger,
        IUniversalProgressStore progressStore,
        [FromServices] IExecutionJobStore? jobStore = null)
    {
        EnrichActivity("ExecuteProcess");

        if (!string.Equals(processId, CanonicalProcessId, StringComparison.OrdinalIgnoreCase))
        {
            OgcProcessesLog.ProcessNotFound(logger, processId);
            return Results.Json(
                new OgcProcessError
                {
                    Type = "http://www.opengis.net/def/exceptions/ogcapi-processes-1/1.0/no-such-process",
                    Title = "No such process",
                    Status = StatusCodes.Status404NotFound,
                    Detail = $"Process '{processId}' does not exist."
                },
                OgcProcessesJsonContext.Default.OgcProcessError,
                MediaTypes.Json,
                StatusCodes.Status404NotFound);
        }

        // OGC API Processes: Prefer: respond-async selects async execution.
        // V1 only supports async; sync execution returns 501.
        var preferAsync = context.Request.Headers.TryGetValue("Prefer", out var preferValues)
            && preferValues.Any(v => v != null && v.Contains("respond-async", StringComparison.OrdinalIgnoreCase));

        if (!preferAsync)
        {
            OgcProcessesLog.SyncExecutionNotSupported(logger, processId);
            return Results.Json(
                new OgcProcessError
                {
                    Type = "about:blank",
                    Title = "Synchronous execution not supported",
                    Status = StatusCodes.Status501NotImplemented,
                    Detail = "This process only supports asynchronous execution. " +
                             "Include 'Prefer: respond-async' header in the request."
                },
                OgcProcessesJsonContext.Default.OgcProcessError,
                MediaTypes.Json,
                StatusCodes.Status501NotImplemented);
        }

        // Validate that the request contains the required 'plan' input.
        if (request.Inputs == null
            || !request.Inputs.TryGetValue("plan", out var planElement)
            || planElement.ValueKind == System.Text.Json.JsonValueKind.Undefined
            || planElement.ValueKind == System.Text.Json.JsonValueKind.Null)
        {
            OgcProcessesLog.ExecutionRequestInvalid(logger, processId);
            return Results.Json(
                new OgcProcessError
                {
                    Type = "about:blank",
                    Title = "Invalid execution request",
                    Status = StatusCodes.Status400BadRequest,
                    Detail = "The 'inputs' object must contain a 'plan' property with a non-null analysis plan."
                },
                OgcProcessesJsonContext.Default.OgcProcessError,
                MediaTypes.Json,
                StatusCodes.Status400BadRequest);
        }

        OgcProcessesLog.ExecutionRequested(logger, processId, true);

        if (jobStore == null)
        {
            OgcProcessesLog.JobStoreUnavailable(logger);
            return Results.Json(
                new OgcProcessError
                {
                    Type = "about:blank",
                    Title = "Service unavailable",
                    Status = StatusCodes.Status503ServiceUnavailable,
                    Detail = "Job operations require Redis-backed durable storage."
                },
                OgcProcessesJsonContext.Default.OgcProcessError,
                MediaTypes.Json,
                StatusCodes.Status503ServiceUnavailable);
        }

        var planJson = planElement.GetRawText();
        var now = DateTimeOffset.UtcNow;
        var jobId = $"gp-{Guid.NewGuid():N}";

        var jobRecord = new ExecutionJobRecord
        {
            OperationId = jobId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Queued",
            Audit = new OperationAuditInfo
            {
                RequestedBy = context.User.Identity?.Name,
                RequestFingerprint = CreateRequestFingerprint(processId, planJson)
            },
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = $"ogc-processes:{processId}"
            }
        };

        await jobStore.TryCreateAsync(jobRecord, cancellationToken: context.RequestAborted).ConfigureAwait(false);

        var progress = GeoprocessingProgress.CreateForSubmittedJob(jobId, processId);
        await progressStore.SetProgressAsync(jobId, progress, TimeSpan.FromDays(7), context.RequestAborted)
            .ConfigureAwait(false);

        OgcProcessesLog.JobCreated(logger, jobId, processId);

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var statusInfo = OgcProcessesConversionHelpers.ToOgcStatusInfo(jobRecord, processId, baseUrl);

        context.Response.Headers["Location"] = $"{baseUrl}{BasePath}/jobs/{jobId}";
        context.Response.Headers["Preference-Applied"] = "respond-async";

        return Results.Json(statusInfo, OgcProcessesJsonContext.Default.OgcStatusInfo, MediaTypes.Json, StatusCodes.Status201Created);
    }

    private static string CreateRequestFingerprint(string processId, string planJson)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"ogc-execute:{processId}:{planJson}"));
        return Convert.ToHexString(hashBytes.AsSpan(0, 12)).ToLowerInvariant();
    }

    private static void EnrichActivity(string operation)
    {
        var activity = Activity.Current;
        if (activity == null) return;
        activity.SetTag(HonuaTelemetry.Tags.Protocol, "OGC-API-Processes");
        activity.SetTag(HonuaTelemetry.Tags.Operation, operation);
    }

}

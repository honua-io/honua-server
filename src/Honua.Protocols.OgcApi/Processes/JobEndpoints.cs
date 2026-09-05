// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Protocols.Ogc.Common;
using Honua.Protocols.Ogc.Api.Processes.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Honua.Protocols.Ogc.Api.Processes;

/// <summary>
/// OGC API Processes job lifecycle endpoints (status, results, dismiss, list).
/// </summary>
internal static class JobEndpoints
{
    private const string BasePath = "/ogc/processes";
    private const string Tag = "OGC API Processes";

    public static void MapOgcProcessesJobEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet($"{BasePath}/jobs", GetJobList)
            .WithTags(Tag)
            .WithName("OgcProcessesJobList")
            .WithSummary("List jobs")
            .Produces<OgcJobList>()
            .ExcludeFromDescription();

        endpoints.MapGet($"{BasePath}/jobs/{{jobId}}", GetJobStatus)
            .WithTags(Tag)
            .WithName("OgcProcessesJobStatus")
            .WithSummary("Get job status")
            .Produces<OgcStatusInfo>()
            .Produces<OgcProcessError>(StatusCodes.Status404NotFound)
            .ExcludeFromDescription();

        endpoints.MapGet($"{BasePath}/jobs/{{jobId}}/results", GetJobResults)
            .WithTags(Tag)
            .WithName("OgcProcessesJobResults")
            .WithSummary("Get job results")
            .Produces<OgcResultsDocument>()
            .Produces<OgcProcessError>(StatusCodes.Status404NotFound)
            .ExcludeFromDescription();

        // HANDLER-AUTHORIZED (#1144): the handler calls
        // OperatorApprovalGate.CheckAuthorizationAsync (with destructive=true) for
        // OperatorResourceType.Job + Execute before any mutation; unauth
        // callers receive an OGC-shaped 401/403. Marked AllowAnonymous so the
        // audit architecture guard records the explicit decision.
        endpoints.MapDelete($"{BasePath}/jobs/{{jobId}}", DismissJob)
            .WithTags(Tag)
            .WithName("OgcProcessesDismissJob")
            .WithSummary("Dismiss (cancel) a job")
            .Produces<OgcStatusInfo>()
            .Produces<OgcProcessError>(StatusCodes.Status404NotFound)
            .ExcludeFromDescription()
            .AllowAnonymous();
    }

    private static async Task<IResult> GetJobList(
        HttpContext context,
        ILogger<OgcProcessesEndpointsLog> logger,
        IOptions<OgcProcessesOptions> options,
        [FromServices] IGeoprocessingJobService jobService,
        [FromQuery] int? limit = null)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("ogc.processes.getjoblist");
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, "OGC-API-Processes");
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "GetJobList");

        var gate = context.RequestServices.GetRequiredService<OperatorApprovalGate>();
        var authDecision = await gate.CheckAuthorizationAsync(
            context.User,
            new OperatorAuthorizationRequest
            {
                ResourceType = OperatorResourceType.Job,
                Operation = OperatorOperation.Read
            },
            context.RequestAborted).ConfigureAwait(false);
        if (!authDecision.IsAllowed)
        {
            OgcProcessesLog.AuthorizationDenied(logger, OperatorResourceType.Job.ToString(), OperatorOperation.Read.ToString());
            return ProcessEndpoints.FormatOgcAuthError(authDecision);
        }

        OgcProcessesLog.JobListRequested(logger);

        if (limit is <= 0)
        {
            return Results.Json(
                new OgcProcessError
                {
                    Type = "about:blank",
                    Title = "Invalid limit",
                    Status = StatusCodes.Status400BadRequest,
                    Detail = "The 'limit' parameter must be a positive integer."
                },
                OgcProcessesJsonContext.Default.OgcProcessError,
                MediaTypes.Json,
                StatusCodes.Status400BadRequest);
        }

        var effectiveLimit = limit ?? options.Value.DefaultJobLimit;
        GeoprocessingJobListPage page;
        try
        {
            page = await jobService.ListJobsAsync(
                new GeoprocessingJobListFilter
                {
                    Limit = effectiveLimit,
                    Statuses =
                    [
                        ExecutionJobStatus.Queued,
                        ExecutionJobStatus.Provisioning,
                        ExecutionJobStatus.Running
                    ]
                },
                context.User,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (GeoprocessingStoreUnavailableException storeEx)
        {
            OgcProcessesLog.JobStoreUnavailable(logger);
            return JobStoreUnavailableResult(storeEx);
        }

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var statusInfosBuilder = ImmutableArray.CreateBuilder<OgcStatusInfo>();
        foreach (var job in page.Items)
        {
            var jobAuthDecision = await gate.CheckAuthorizationAsync(
                context.User,
                new OperatorAuthorizationRequest
                {
                    ResourceType = OperatorResourceType.Job,
                    ResourceId = job.OperationId,
                    Operation = OperatorOperation.Read
                },
                context.RequestAborted).ConfigureAwait(false);
            if (!jobAuthDecision.IsAllowed)
            {
                continue;
            }

            statusInfosBuilder.Add(OgcProcessesConversionHelpers.ToOgcStatusInfo(
                job, ResolveOgcProcessId(job), baseUrl));
            if (statusInfosBuilder.Count >= effectiveLimit)
            {
                break;
            }
        }

        var statusInfos = statusInfosBuilder.ToImmutable();

        var jobList = new OgcJobList
        {
            Jobs = statusInfos,
            Links = ImmutableArray.Create(
                Link.Create($"{baseUrl}{BasePath}/jobs", RelationTypes.Self, MediaTypes.Json, "This document"))
        };

        return Results.Json(jobList, OgcProcessesJsonContext.Default.OgcJobList, MediaTypes.Json);
    }

    private static async Task<IResult> GetJobStatus(
        string jobId,
        HttpContext context,
        ILogger<OgcProcessesEndpointsLog> logger,
        [FromServices] IGeoprocessingJobService jobService)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("ogc.processes.getjobstatus");
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, "OGC-API-Processes");
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "GetJobStatus");
        activity?.SetTag(HonuaTelemetry.Tags.JobId, jobId);

        var gate = context.RequestServices.GetRequiredService<OperatorApprovalGate>();
        var authDecision = await gate.CheckAuthorizationAsync(
            context.User,
            new OperatorAuthorizationRequest
            {
                ResourceType = OperatorResourceType.Job,
                Operation = OperatorOperation.Read
            },
            context.RequestAborted).ConfigureAwait(false);
        if (!authDecision.IsAllowed)
        {
            OgcProcessesLog.AuthorizationDenied(logger, OperatorResourceType.Job.ToString(), OperatorOperation.Read.ToString());
            return ProcessEndpoints.FormatOgcAuthError(authDecision);
        }

        OgcProcessesLog.JobStatusRequested(logger, jobId);

        ExecutionJobRecord job;
        try
        {
            job = await jobService.GetJobAsync(jobId, context.User, context.RequestAborted).ConfigureAwait(false);
        }
        catch (GeoprocessingNotFoundException)
        {
            OgcProcessesLog.JobNotFound(logger, jobId);
            return JobNotFoundResult(jobId);
        }
        catch (GeoprocessingAuthorizationException authEx)
        {
            OgcProcessesLog.AuthorizationDenied(logger, OperatorResourceType.Job.ToString(), OperatorOperation.Read.ToString());
            return ProcessEndpoints.FormatOgcAuthError(authEx.RequiresAuthentication);
        }
        catch (GeoprocessingStoreUnavailableException storeEx)
        {
            OgcProcessesLog.JobStoreUnavailable(logger);
            return JobStoreUnavailableResult(storeEx);
        }

        if (job.Spec.Kind != ExecutionJobKind.Geoprocessing)
        {
            OgcProcessesLog.JobNotFound(logger, jobId);
            return JobNotFoundResult(jobId);
        }

        authDecision = await gate.CheckAuthorizationAsync(
            context.User,
            new OperatorAuthorizationRequest
            {
                ResourceType = OperatorResourceType.Job,
                ResourceId = job.OperationId,
                Operation = OperatorOperation.Read
            },
            context.RequestAborted).ConfigureAwait(false);
        if (!authDecision.IsAllowed)
        {
            OgcProcessesLog.AuthorizationDenied(logger, OperatorResourceType.Job.ToString(), OperatorOperation.Read.ToString());
            return ProcessEndpoints.FormatOgcAuthError(authDecision);
        }

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var statusInfo = OgcProcessesConversionHelpers.ToOgcStatusInfo(
            job, ResolveOgcProcessId(job), baseUrl);

        return Results.Json(statusInfo, OgcProcessesJsonContext.Default.OgcStatusInfo, MediaTypes.Json);
    }

    private static async Task<IResult> GetJobResults(
        string jobId,
        HttpContext context,
        ILogger<OgcProcessesEndpointsLog> logger,
        [FromServices] IGeoprocessingJobService jobService,
        [FromServices] IOptionsMonitor<GeoprocessingExecutorOptions> executorOptions)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("ogc.processes.getjobresults");
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, "OGC-API-Processes");
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "GetJobResults");
        activity?.SetTag(HonuaTelemetry.Tags.JobId, jobId);

        var gate = context.RequestServices.GetRequiredService<OperatorApprovalGate>();
        var authDecision = await gate.CheckAuthorizationAsync(
            context.User,
            new OperatorAuthorizationRequest
            {
                ResourceType = OperatorResourceType.Job,
                Operation = OperatorOperation.Read
            },
            context.RequestAborted).ConfigureAwait(false);
        if (!authDecision.IsAllowed)
        {
            OgcProcessesLog.AuthorizationDenied(logger, OperatorResourceType.Job.ToString(), OperatorOperation.Read.ToString());
            return ProcessEndpoints.FormatOgcAuthError(authDecision);
        }

        OgcProcessesLog.JobResultsRequested(logger, jobId);

        ExecutionJobRecord job;
        try
        {
            job = await jobService.GetJobAsync(jobId, context.User, context.RequestAborted).ConfigureAwait(false);
        }
        catch (GeoprocessingNotFoundException)
        {
            OgcProcessesLog.JobNotFound(logger, jobId);
            return JobNotFoundResult(jobId);
        }
        catch (GeoprocessingAuthorizationException authEx)
        {
            OgcProcessesLog.AuthorizationDenied(logger, OperatorResourceType.Job.ToString(), OperatorOperation.Read.ToString());
            return ProcessEndpoints.FormatOgcAuthError(authEx.RequiresAuthentication);
        }
        catch (GeoprocessingStoreUnavailableException storeEx)
        {
            OgcProcessesLog.JobStoreUnavailable(logger);
            return JobStoreUnavailableResult(storeEx);
        }

        if (job.Spec.Kind != ExecutionJobKind.Geoprocessing)
        {
            OgcProcessesLog.JobNotFound(logger, jobId);
            return JobNotFoundResult(jobId);
        }

        authDecision = await gate.CheckAuthorizationAsync(
            context.User,
            new OperatorAuthorizationRequest
            {
                ResourceType = OperatorResourceType.Job,
                ResourceId = job.OperationId,
                Operation = OperatorOperation.Read
            },
            context.RequestAborted).ConfigureAwait(false);
        if (!authDecision.IsAllowed)
        {
            OgcProcessesLog.AuthorizationDenied(logger, OperatorResourceType.Job.ToString(), OperatorOperation.Read.ToString());
            return ProcessEndpoints.FormatOgcAuthError(authDecision);
        }

        if (!OgcProcessesConversionHelpers.IsTerminal(job.Status))
        {
            OgcProcessesLog.JobResultsNotAvailable(logger, jobId, OgcProcessesConversionHelpers.ToOgcStatus(job.Status));
            return Results.Json(
                new OgcProcessError
                {
                    Type = "http://www.opengis.net/def/exceptions/ogcapi-processes-1/1.0/result-not-ready",
                    Title = "Result not ready",
                    Status = StatusCodes.Status404NotFound,
                    Detail = $"Job '{jobId}' has not reached a terminal state (current: {OgcProcessesConversionHelpers.ToOgcStatus(job.Status)})."
                },
                OgcProcessesJsonContext.Default.OgcProcessError,
                MediaTypes.Json,
                StatusCodes.Status404NotFound);
        }

        // V1 publishes document-mode results by adapting the canonical
        // geoprocessing result package into OGC output members.
        if (job.Status == ExecutionJobStatus.Failed)
        {
            return BuildJobFailedResult(jobId, job.ErrorMessage);
        }

        if (job.Status == ExecutionJobStatus.Cancelled)
        {
            return BuildJobDismissedResult(jobId);
        }

        AnalysisResultPackage resultPackage;
        try
        {
            resultPackage = await jobService
                .GetJobResultsAsync(jobId, context.User, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (GeoprocessingNotFoundException)
        {
            OgcProcessesLog.JobNotFound(logger, jobId);
            return JobNotFoundResult(jobId);
        }
        catch (GeoprocessingStoreUnavailableException storeEx)
        {
            OgcProcessesLog.JobStoreUnavailable(logger);
            return JobStoreUnavailableResult(storeEx);
        }

        if (OgcProcessesExecutionMetadata.IsRaw(job.Spec.Parameters))
        {
            return await BuildRawResultsResponseAsync(job, context, resultPackage).ConfigureAwait(false);
        }

        if (OgcProcessesCiteEchoFixture.IsJob(job))
        {
            if (!TryToCiteEchoResultsDocument(
                    job,
                    resultPackage,
                    executorOptions.CurrentValue.MaxArtifactBytes,
                    out var citeResultsDocument))
            {
                return OgcProcessesResults.Error(
                    StatusCodes.Status500InternalServerError,
                    "Invalid certification result",
                    "The certification fixture produced invalid result evidence.");
            }

            return Results.Json(
                citeResultsDocument.Outputs ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal),
                OgcProcessesJsonContext.Default.DictionaryStringJsonElement,
                MediaTypes.Json,
                StatusCodes.Status200OK);
        }

        if (OgcProcessesExecutionMetadata.UsesValueTransmission(job.Spec.Parameters))
        {
            return await BuildValueResultsResponseAsync(job, context, resultPackage).ConfigureAwait(false);
        }

        return BuildResultsResponse(context, logger, jobId, job, resultPackage);
    }

    internal static IResult BuildJobFailedResult(string jobId, string? errorMessage)
    {
        // OGC API Processes Part 1 (OGC 18-062r2): use a registered OGC
        // exception type URI so clients can distinguish job failure from a
        // server fault. Both inline and polled execution share this mapping.
        return Results.Json(
            new OgcProcessError
            {
                Type = "http://www.opengis.net/def/exceptions/ogcapi-processes-1/1.0/job-failed",
                Title = "Job failed",
                Status = StatusCodes.Status500InternalServerError,
                Detail = errorMessage ?? $"Job '{jobId}' failed."
            },
            OgcProcessesJsonContext.Default.OgcProcessError,
            MediaTypes.Json,
            StatusCodes.Status500InternalServerError);
    }

    internal static IResult BuildJobDismissedResult(string jobId)
    {
        // Both inline and polled execution expose the same registered terminal-state
        // exception so clients can handle cancellation independently of observation path.
        return Results.Json(
            new OgcProcessError
            {
                Type = "http://www.opengis.net/def/exceptions/ogcapi-processes-1/1.0/job-dismissed",
                Title = "Job dismissed",
                Status = StatusCodes.Status410Gone,
                Detail = $"Job '{jobId}' has been dismissed."
            },
            OgcProcessesJsonContext.Default.OgcProcessError,
            MediaTypes.Json,
            StatusCodes.Status410Gone);
    }

    internal static IResult BuildResultsResponse(
        HttpContext context,
        ILogger logger,
        string jobId,
        ExecutionJobRecord job,
        AnalysisResultPackage resultPackage)
    {
        var resultsDocument = ToOgcResultsDocument(
            job,
            resultPackage,
            BaseUrlResolver.GetBaseUrl(context),
            jobId,
            context.RequestServices
                .GetService<Honua.Core.Features.Geoprocessing.Abstractions.IGeoprocessingOutputObjectStore>(),
            logger);
        return Results.Json(
            resultsDocument.Outputs ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            OgcProcessesJsonContext.Default.DictionaryStringJsonElement,
            MediaTypes.Json,
            StatusCodes.Status200OK);
    }

    internal static async Task<IResult> BuildValueResultsResponseAsync(
        ExecutionJobRecord job,
        HttpContext context,
        AnalysisResultPackage resultPackage)
    {
        var outputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var selectedOutputNames = GetSelectedOutputNames(job);
        for (var index = 0; index < resultPackage.Artifacts.Count; index++)
        {
            var artifact = resultPackage.Artifacts[index];
            var resolvedOutputName = ResolveOutputName(artifact, index);
            if (selectedOutputNames.Count > 0 && !selectedOutputNames.Contains(resolvedOutputName))
            {
                continue;
            }

            var materialized = await MaterializeArtifactAsync(context, artifact).ConfigureAwait(false);
            if (materialized.Payload == null)
            {
                return OgcProcessesResults.Error(
                    materialized.TooLarge ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status500InternalServerError,
                    materialized.TooLarge ? "Process result too large" : "Process result unavailable",
                    materialized.Error ?? "The process output could not be materialized as an inline value.");
            }

            if (!MediaTypeHeaderValue.TryParse(materialized.MediaType, out _))
            {
                return OgcProcessesResults.Error(
                    StatusCodes.Status500InternalServerError,
                    "Invalid process result",
                    "The process output declares an invalid media type.");
            }

            var value = BuildQualifiedOutputValue(materialized.Payload, materialized.MediaType, out var error);
            if (value.ValueKind == JsonValueKind.Undefined)
            {
                return OgcProcessesResults.Error(
                    StatusCodes.Status500InternalServerError,
                    "Invalid process result",
                    error ?? "The process output is not valid for its declared media type.");
            }

            outputs[ResolveUniqueOutputName(resolvedOutputName, outputs)] = value;
        }

        return Results.Json(
            outputs,
            OgcProcessesJsonContext.Default.DictionaryStringJsonElement,
            MediaTypes.Json,
            StatusCodes.Status200OK);
    }

    internal static async Task<IResult> BuildRawResultsResponseAsync(
        ExecutionJobRecord job,
        HttpContext context,
        AnalysisResultPackage resultPackage)
    {
        var values = new List<(string Name, byte[] Payload, string MediaType)>();
        var selectedOutputNames = GetSelectedOutputNames(job);
        for (var index = 0; index < resultPackage.Artifacts.Count; index++)
        {
            var artifact = resultPackage.Artifacts[index];
            var outputName = ResolveOutputName(artifact, index);
            if (selectedOutputNames.Count > 0 && !selectedOutputNames.Contains(outputName))
            {
                continue;
            }

            var materialized = await MaterializeArtifactAsync(context, artifact).ConfigureAwait(false);
            if (materialized.Payload == null)
            {
                return OgcProcessesResults.Error(
                    materialized.TooLarge ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status500InternalServerError,
                    materialized.TooLarge ? "Raw response too large" : "Raw response unavailable",
                    materialized.Error ?? "A raw process output could not be materialized.");
            }

            if (!MediaTypeHeaderValue.TryParse(materialized.MediaType, out _))
            {
                return OgcProcessesResults.Error(
                    StatusCodes.Status500InternalServerError,
                    "Invalid process result",
                    "The process output declares an invalid media type.");
            }

            values.Add((
                outputName,
                materialized.Payload,
                materialized.MediaType));
        }

        if (values.Count == 0)
        {
            return OgcProcessesResults.Error(
                StatusCodes.Status400BadRequest,
                "Raw response unavailable",
                "The process produced no requested value outputs.");
        }

        if (values.Count == 1)
        {
            return Results.Bytes(values[0].Payload, values[0].MediaType);
        }

        var boundary = $"honua-{Guid.NewGuid():N}";
        using var body = new MemoryStream();
        foreach (var value in values)
        {
            WriteUtf8(body, $"--{boundary}\r\nContent-Type: {value.MediaType}\r\nContent-ID: <{value.Name}>\r\n\r\n");
            body.Write(value.Payload);
            WriteUtf8(body, "\r\n");
        }

        WriteUtf8(body, $"--{boundary}--\r\n");
        return Results.Bytes(body.ToArray(), $"multipart/related; boundary=\"{boundary}\"");
    }

    private static async Task<IResult> DismissJob(
        string jobId,
        HttpContext context,
        ILogger<OgcProcessesEndpointsLog> logger,
        IGeoprocessingJobTerminalService terminalService)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("ogc.processes.dismissjob");
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, "OGC-API-Processes");
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "DismissJob");
        activity?.SetTag(HonuaTelemetry.Tags.JobId, jobId);

        OgcProcessesLog.JobDismissRequested(logger, jobId);
        GeoprocessingCancelResult result;
        try
        {
            result = await terminalService.CancelAsync(
                jobId,
                context.User,
                TimeSpan.FromSeconds(30),
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (GeoprocessingAuthorizationException authEx)
        {
            OgcProcessesLog.AuthorizationDenied(
                logger,
                OperatorResourceType.Job.ToString(),
                OperatorOperation.Execute.ToString());
            return ProcessEndpoints.FormatOgcAuthError(authEx.RequiresAuthentication);
        }
        catch (GeoprocessingApprovalRequiredException approvalEx)
        {
            OgcProcessesLog.DismissRejectedApprovalRequired(logger, jobId, approvalEx.PolicyRef);
            return ProcessEndpoints.FormatOgcApprovalError(approvalEx.PolicyRef, approvalEx.Message);
        }
        catch (GeoprocessingStoreUnavailableException storeEx)
        {
            return JobStoreUnavailableResult(storeEx);
        }

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        switch (result.Outcome)
        {
            case GeoprocessingCancelOutcome.Cancelled:
                return result.Job == null
                    ? JobNotFoundResult(jobId)
                    : BuildDismissOutcomeResult(baseUrl, jobId, result.Job);
            case GeoprocessingCancelOutcome.AlreadyTerminal:
                if (result.Job?.Status == ExecutionJobStatus.Cancelled)
                {
                    return OgcProcessesResults.Dismissed(
                        result.Job,
                        ResolveOgcProcessId(result.Job),
                        baseUrl);
                }

                var terminalStatus = result.Job == null
                    ? "unknown"
                    : OgcProcessesConversionHelpers.ToOgcStatus(result.Job.Status);
                OgcProcessesLog.DismissRejectedTerminal(logger, jobId, terminalStatus);
                return DismissConflict(
                    "Cannot dismiss completed job",
                    $"Job '{jobId}' is in terminal state '{terminalStatus}' and cannot be dismissed.");
            case GeoprocessingCancelOutcome.Unsupported:
                return DismissConflict(
                    "Cancellation not supported",
                    $"Job '{jobId}' runs on a backend which does not support dismissal.");
            case GeoprocessingCancelOutcome.Unconfirmed:
                return DismissConflict(
                    "Dismiss could not be confirmed",
                    $"Job '{jobId}' dismiss could not be confirmed after retries.");
            case GeoprocessingCancelOutcome.NotFound:
                OgcProcessesLog.JobNotFound(logger, jobId);
                return JobNotFoundResult(jobId);
            case GeoprocessingCancelOutcome.Timeout:
                return OgcProcessesResults.Error(
                    StatusCodes.Status408RequestTimeout,
                    "Dismiss timed out",
                    $"Job '{jobId}' dismiss did not complete within the bounded cancellation window.");
            case GeoprocessingCancelOutcome.ClientDisconnected:
                return Results.StatusCode(499);
            default:
                throw new InvalidOperationException($"Unexpected cancellation outcome '{result.Outcome}'.");
        }
    }

    private static IResult DismissConflict(string title, string detail)
        => Results.Json(
            new OgcProcessError
            {
                Type = "about:blank",
                Title = title,
                Status = StatusCodes.Status409Conflict,
                Detail = detail
            },
            OgcProcessesJsonContext.Default.OgcProcessError,
            MediaTypes.Json,
            StatusCodes.Status409Conflict);

    private static IResult JobStoreUnavailableResult(GeoprocessingStoreUnavailableException? exception = null)
        => OgcProcessesResults.StoreUnavailable(exception);

    private static IResult JobNotFoundResult(string jobId) => OgcProcessesResults.NoSuchJob(jobId);

    private static OgcResultsDocument ToOgcResultsDocument(
        ExecutionJobRecord job,
        AnalysisResultPackage resultPackage,
        string baseUrl,
        string jobId,
        Honua.Core.Features.Geoprocessing.Abstractions.IGeoprocessingOutputObjectStore? outputStore,
        ILogger logger)
    {
        var outputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var selectedOutputNames = job.Spec.Parameters
            .Where(entry => entry.Key.StartsWith(
                GeoprocessingProtocolMetadataKeys.OutputNamePrefix,
                StringComparison.Ordinal))
            .Select(entry => entry.Value)
            .Where(outputName => !string.IsNullOrWhiteSpace(outputName))
            .ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < resultPackage.Artifacts.Count; index++)
        {
            var artifact = resultPackage.Artifacts[index];
            var resolvedOutputName = ResolveOutputName(artifact, index);
            if (selectedOutputNames.Count > 0 && !selectedOutputNames.Contains(resolvedOutputName))
            {
                // Ordinary OGC process submissions persist every advertised name
                // when no selection is supplied, and only selected names otherwise.
                // Filtering by those durable bindings keeps execution free to
                // publish its canonical artifact set without leaking unrequested
                // outputs into the OGC results document. Canonical/legacy jobs with
                // no bindings retain their historical all-results behavior.
                continue;
            }

            // Staged output artifacts (#3089) link through the canonical authenticated
            // content route, so no provider location or expiring URL leaks into result
            // links. A link is only
            // advertised when this host's registered output store can actually serve
            // it — a worker-enabled/server-disabled (or mismatched) staging topology
            // must surface as an explicit unavailable state, not a guaranteed 503.
            var href = artifact.Uri;
            if (artifact.Metadata.TryGetValue(
                    Honua.Core.Features.Geoprocessing.Raster.RasterOutputArtifactMetadata.Staged, out var isStaged)
                && string.Equals(isStaged, "true", StringComparison.OrdinalIgnoreCase))
            {
                if (Honua.Core.Features.Geoprocessing.Raster.RasterOutputContentRoutes.CanServe(
                        outputStore,
                        artifact.Metadata.GetValueOrDefault(
                            Honua.Core.Features.Geoprocessing.Raster.RasterOutputArtifactMetadata.StoreProvider),
                        artifact.Metadata.GetValueOrDefault(
                            Honua.Core.Features.Geoprocessing.Raster.RasterOutputArtifactMetadata.StoreReference)))
                {
                    href = Honua.Core.Features.Geoprocessing.Raster.RasterOutputContentRoutes.Build(baseUrl, jobId, index);
                }
                else
                {
                    href = null;
                    OgcProcessesLog.ArtifactStoreUnavailable(logger, jobId, index);
                }
            }

            var outputName = ResolveUniqueOutputName(resolvedOutputName, outputs);
            outputs[outputName] = JsonSerializer.SerializeToElement(
                new OgcArtifactResult
                {
                    Id = artifact.ArtifactId,
                    Kind = artifact.Kind.ToString(),
                    Title = artifact.Label,
                    Href = href,
                    Type = artifact.ContentType
                },
                OgcProcessesJsonContext.Default.OgcArtifactResult);
        }

        return new OgcResultsDocument { Outputs = outputs };
    }

    private static HashSet<string> GetSelectedOutputNames(ExecutionJobRecord job)
        => job.Spec.Parameters
            .Where(entry => entry.Key.StartsWith(
                GeoprocessingProtocolMetadataKeys.OutputNamePrefix,
                StringComparison.Ordinal))
            .Select(entry => entry.Value)
            .Where(outputName => !string.IsNullOrWhiteSpace(outputName))
            .ToHashSet(StringComparer.Ordinal);

    private static async Task<MaterializedArtifact> MaterializeArtifactAsync(
        HttpContext context,
        ArtifactRef artifact)
    {
        var maxArtifactBytes = context.RequestServices
            .GetService<IOptions<GeoprocessingExecutorOptions>>()?.Value.MaxArtifactBytes
            ?? 50L * 1024L * 1024L;
        if (FeatureStreamArtifact.IsStreamReference(artifact.Uri))
        {
            return await MaterializeFeatureStreamAsync(context, artifact.Uri!, maxArtifactBytes).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(artifact.Uri)
            && artifact.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return ProcessEndpoints.TryDecodeDataUri(
                artifact.Uri,
                maxArtifactBytes,
                out var payload,
                out var mediaType,
                out var error)
                    ? new MaterializedArtifact(
                        payload,
                        string.IsNullOrWhiteSpace(mediaType)
                            ? artifact.ContentType ?? "application/octet-stream"
                            : mediaType,
                        null,
                        false)
                    : new MaterializedArtifact(null, artifact.ContentType ?? "application/octet-stream", error,
                        error?.Contains("exceeds", StringComparison.Ordinal) == true);
        }

        if (!artifact.Metadata.TryGetValue(RasterOutputArtifactMetadata.Staged, out var staged)
            || !string.Equals(staged, "true", StringComparison.OrdinalIgnoreCase)
            || !artifact.Metadata.TryGetValue(RasterOutputArtifactMetadata.ObjectKey, out var objectKey)
            || string.IsNullOrWhiteSpace(objectKey))
        {
            return new MaterializedArtifact(
                null,
                artifact.ContentType ?? "application/octet-stream",
                "The output is a reference, but this process advertises value transmission.",
                false);
        }

        var store = context.RequestServices.GetService<IGeoprocessingOutputObjectStore>();
        if (!RasterOutputContentRoutes.CanServe(
                store,
                artifact.Metadata.GetValueOrDefault(RasterOutputArtifactMetadata.StoreProvider),
                artifact.Metadata.GetValueOrDefault(RasterOutputArtifactMetadata.StoreReference)))
        {
            return new MaterializedArtifact(
                null,
                artifact.ContentType ?? "application/octet-stream",
                "The staged output store is unavailable.",
                false);
        }

        var info = await store!.GetInfoAsync(objectKey, context.RequestAborted).ConfigureAwait(false);
        if (info == null)
        {
            return new MaterializedArtifact(null, artifact.ContentType ?? "application/octet-stream", "The staged output no longer exists.", false);
        }

        if (info.SizeBytes > maxArtifactBytes)
        {
            return new MaterializedArtifact(null, artifact.ContentType ?? "application/octet-stream", "The output exceeds the configured artifact response limit.", true);
        }

        if (!await store.TryAcquireReadLeaseAsync(objectKey, TimeSpan.FromMinutes(5), context.RequestAborted)
                .ConfigureAwait(false))
        {
            return new MaterializedArtifact(null, artifact.ContentType ?? "application/octet-stream", "The staged output no longer exists.", false);
        }

        await using var stream = await store.OpenReadAsync(objectKey, context.RequestAborted).ConfigureAwait(false);
        if (stream == null)
        {
            return new MaterializedArtifact(null, artifact.ContentType ?? "application/octet-stream", "The staged output no longer exists.", false);
        }

        using var body = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, context.RequestAborted).ConfigureAwait(false);
            if (read == 0)
            {
                return new MaterializedArtifact(
                    body.ToArray(),
                    artifact.ContentType ?? "application/octet-stream",
                    null,
                    false);
            }

            if (body.Length + read > maxArtifactBytes)
            {
                return new MaterializedArtifact(null, artifact.ContentType ?? "application/octet-stream", "The output exceeds the configured artifact response limit.", true);
            }

            body.Write(buffer, 0, read);
        }
    }

    private static async Task<MaterializedArtifact> MaterializeFeatureStreamAsync(
        HttpContext context,
        string reference,
        long maxBytes)
    {
        const string mediaType = "application/geo+json";
        var outputRoot = context.RequestServices.GetService<IOptions<GeoprocessingExecutorOptions>>()?.Value.OutputRootDirectory;
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            return new MaterializedArtifact(null, mediaType, "The feature stream output store is unavailable.", false);
        }

        try
        {
            if (!FeatureStreamArtifact.TryOpenRead(reference, out _, out var features, maxBytes, outputRoot)
                || !FeatureStreamArtifact.TryParseStreamReference(reference, out var descriptor, out _))
            {
                return new MaterializedArtifact(null, mediaType, "The feature stream output is unavailable.", false);
            }

            // Use the actual backing-file size, not the size claimed in the reference.
            // This also bounds the largest line the canonical reader may materialize.
            if (new FileInfo(descriptor.Path).Length > maxBytes)
            {
                return new MaterializedArtifact(null, mediaType, "The output exceeds the configured artifact response limit.", true);
            }

            using var body = new MemoryStream();
            using var writer = new Utf8JsonWriter(body);
            var featureWriter = GeoJsonArtifactCodec.CreateWriter();
            writer.WriteStartObject();
            writer.WriteString("type", "FeatureCollection");
            writer.WriteStartArray("features");
            await foreach (var feature in features.WithCancellation(context.RequestAborted).ConfigureAwait(false))
            {
                writer.WriteRawValue(featureWriter.Write(feature));
                writer.Flush();
                if (body.Length > maxBytes)
                {
                    return new MaterializedArtifact(null, mediaType, "The output exceeds the configured artifact response limit.", true);
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
            return body.Length > maxBytes
                ? new MaterializedArtifact(null, mediaType, "The output exceeds the configured artifact response limit.", true)
                : new MaterializedArtifact(body.ToArray(), mediaType, null, false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or Newtonsoft.Json.JsonException)
        {
            return new MaterializedArtifact(null, mediaType, "The feature stream output is unavailable.", false);
        }
    }

    private static JsonElement BuildQualifiedOutputValue(
        byte[] payload,
        string mediaType,
        out string? error)
    {
        error = null;
        JsonElement? jsonValue = null;
        var mediaTypeEssence = mediaType.Split(';', 2, StringSplitOptions.TrimEntries)[0];
        if (mediaTypeEssence.EndsWith("/json", StringComparison.OrdinalIgnoreCase)
            || mediaTypeEssence.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var valueDocument = JsonDocument.Parse(payload);
                jsonValue = valueDocument.RootElement.Clone();
            }
            catch (JsonException)
            {
                error = "The output payload is invalid JSON.";
                return default;
            }
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("value");
            if (jsonValue.HasValue)
            {
                jsonValue.Value.WriteTo(writer);
            }
            else
            {
                writer.WriteStringValue(Convert.ToBase64String(payload));
            }

            writer.WriteString("mediaType", mediaType);
            if (!jsonValue.HasValue)
            {
                writer.WriteString("encoding", "base64");
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static void WriteUtf8(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes);
    }

    private readonly record struct MaterializedArtifact(
        byte[]? Payload,
        string MediaType,
        string? Error,
        bool TooLarge);

    private static bool TryToCiteEchoResultsDocument(
        ExecutionJobRecord job,
        AnalysisResultPackage resultPackage,
        long maxArtifactBytes,
        out OgcResultsDocument resultsDocument)
    {
        if (!OgcProcessesCiteEchoFixture.TryResolveOutputBindings(
                job.Spec.Parameters,
                out var outputIds)
            || resultPackage.Artifacts.Count != outputIds.Length)
        {
            resultsDocument = new OgcResultsDocument();
            return false;
        }

        var outputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        for (var index = 0; index < resultPackage.Artifacts.Count; index++)
        {
            var artifact = resultPackage.Artifacts[index];
            var outputName = ResolveOutputName(artifact, index);
            if (!string.Equals(outputName, outputIds[index], StringComparison.Ordinal)
                || !OgcProcessesCiteEchoExecutor.TryDecodeArtifact(
                    artifact.Uri,
                    maxArtifactBytes,
                    out var value))
            {
                resultsDocument = new OgcResultsDocument();
                return false;
            }

            outputs[outputName] = value;
        }

        resultsDocument = new OgcResultsDocument { Outputs = outputs };
        return true;
    }

    private static string ResolveOutputName(ArtifactRef artifact, int index)
    {
        if (artifact.Metadata.TryGetValue(
                GeoprocessingProtocolMetadataKeys.GeoServicesOutputParameterMetadataKey,
                out var outputName) &&
            !string.IsNullOrWhiteSpace(outputName))
        {
            return outputName;
        }

        return string.IsNullOrWhiteSpace(artifact.Label)
            ? $"output{index + 1}"
            : artifact.Label;
    }

    private static string ResolveUniqueOutputName(
        string outputName,
        Dictionary<string, JsonElement> existingOutputs)
    {
        if (!existingOutputs.ContainsKey(outputName))
        {
            return outputName;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{outputName}_{suffix}";
            if (!existingOutputs.ContainsKey(candidate))
            {
                return candidate;
            }
        }
    }

    private static IResult BuildDismissOutcomeResult(
        string baseUrl,
        string jobId,
        ExecutionJobRecord job)
        => job.Status == ExecutionJobStatus.Cancelled
            ? OgcProcessesResults.Dismissed(job, ResolveOgcProcessId(job), baseUrl)
            : Results.Json(
                OgcProcessesConversionHelpers.ToOgcStatusInfo(
                    job,
                    ResolveOgcProcessId(job),
                    baseUrl),
                OgcProcessesJsonContext.Default.OgcStatusInfo,
                MediaTypes.Json,
                StatusCodes.Status202Accepted);

    private static string ResolveOgcProcessId(ExecutionJobRecord job)
        => job.Spec.Parameters.TryGetValue("protocolProcessId", out var protocolProcessId)
            && !string.IsNullOrWhiteSpace(protocolProcessId)
                ? protocolProcessId
                : ProcessEndpoints.CanonicalProcessId;

}

internal sealed record OgcArtifactResult
{
    public required string Id { get; init; }

    public required string Kind { get; init; }

    public string? Title { get; init; }

    public string? Href { get; init; }

    public string? Type { get; init; }
}

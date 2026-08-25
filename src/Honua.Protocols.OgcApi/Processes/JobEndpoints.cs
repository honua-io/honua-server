// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Protocols.Ogc.Common;
using Honua.Protocols.Ogc.Api.Processes.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

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
        catch (GeoprocessingStoreUnavailableException)
        {
            OgcProcessesLog.JobStoreUnavailable(logger);
            return JobStoreUnavailableResult();
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
        catch (GeoprocessingStoreUnavailableException)
        {
            OgcProcessesLog.JobStoreUnavailable(logger);
            return JobStoreUnavailableResult();
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
        [FromServices] IGeoprocessingJobService jobService)
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
        catch (GeoprocessingStoreUnavailableException)
        {
            OgcProcessesLog.JobStoreUnavailable(logger);
            return JobStoreUnavailableResult();
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
            // OGC API Processes Part 1 (OGC 18-062r2): use a registered OGC
            // exception type URI so clients can distinguish job failure from a
            // server fault. "about:blank" conveys no semantic information and
            // prevents OGC CITE test runners from mapping the exception to the
            // correct conformance class.
            return Results.Json(
                new OgcProcessError
                {
                    Type = "http://www.opengis.net/def/exceptions/ogcapi-processes-1/1.0/job-failed",
                    Title = "Job failed",
                    Status = StatusCodes.Status500InternalServerError,
                    Detail = job.ErrorMessage ?? $"Job '{jobId}' failed."
                },
                OgcProcessesJsonContext.Default.OgcProcessError,
                MediaTypes.Json,
                StatusCodes.Status500InternalServerError);
        }

        if (job.Status == ExecutionJobStatus.Cancelled)
        {
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
        catch (GeoprocessingStoreUnavailableException)
        {
            OgcProcessesLog.JobStoreUnavailable(logger);
            return JobStoreUnavailableResult();
        }

        return BuildResultsResponse(context, logger, jobId, resultPackage);
    }

    internal static IResult BuildResultsResponse(
        HttpContext context,
        ILogger logger,
        string jobId,
        AnalysisResultPackage resultPackage)
    {
        var resultsDocument = ToOgcResultsDocument(
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
            return ProcessEndpoints.FormatOgcAuthError(authEx.RequiresAuthentication);
        }
        catch (GeoprocessingApprovalRequiredException approvalEx)
        {
            return OgcProcessesResults.Error(
                StatusCodes.Status409Conflict,
                "Approval required",
                approvalEx.Message);
        }
        catch (GeoprocessingStoreUnavailableException)
        {
            return JobStoreUnavailableResult();
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

    private static IResult JobStoreUnavailableResult() => OgcProcessesResults.StoreUnavailable();

    private static IResult JobNotFoundResult(string jobId) => OgcProcessesResults.NoSuchJob(jobId);

    private static OgcResultsDocument ToOgcResultsDocument(
        AnalysisResultPackage resultPackage,
        string baseUrl,
        string jobId,
        Honua.Core.Features.Geoprocessing.Abstractions.IGeoprocessingOutputObjectStore? outputStore,
        ILogger logger)
    {
        var outputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        for (var index = 0; index < resultPackage.Artifacts.Count; index++)
        {
            var artifact = resultPackage.Artifacts[index];

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

            var outputName = ResolveUniqueOutputName(ResolveOutputName(artifact, outputs.Count), outputs);
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
                MediaTypes.Json);

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

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.Server.Features.Protocols.Ogc.Api.Processes.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Protocols.Ogc.Api.Processes;

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

        endpoints.MapDelete($"{BasePath}/jobs/{{jobId}}", DismissJob)
            .WithTags(Tag)
            .WithName("OgcProcessesDismissJob")
            .WithSummary("Dismiss (cancel) a job")
            .Produces<OgcStatusInfo>()
            .Produces<OgcProcessError>(StatusCodes.Status404NotFound)
            .ExcludeFromDescription();
    }

    private static async Task<IResult> GetJobList(
        HttpContext context,
        ILogger<OgcProcessesEndpointsLog> logger,
        IOptions<OgcProcessesOptions> options,
        [FromQuery] int? limit = null,
        [FromServices] IExecutionJobStore? jobStore = null)
    {
        EnrichActivity("GetJobList");

        var gate = context.RequestServices.GetRequiredService<OperatorApprovalGate>();
        var authDecision = gate.CheckAuthorization(context.User, new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Job,
            Operation = OperatorOperation.Read
        });
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

        if (jobStore == null)
        {
            OgcProcessesLog.JobStoreUnavailable(logger);
            return JobStoreUnavailableResult();
        }

        var jobs = await jobStore.ListActiveAsync(
            ExecutionJobKind.Geoprocessing,
            context.RequestAborted).ConfigureAwait(false);

        var effectiveLimit = limit ?? options.Value.DefaultJobLimit;

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var statusInfos = jobs
            .Where(job => gate.CheckAuthorization(context.User, new OperatorAuthorizationRequest
            {
                ResourceType = OperatorResourceType.Job,
                ResourceId = job.OperationId,
                Operation = OperatorOperation.Read
            }).IsAllowed)
            .Take(effectiveLimit)
            .Select(j => OgcProcessesConversionHelpers.ToOgcStatusInfo(
                j, ProcessEndpoints.CanonicalProcessId, baseUrl))
            .ToImmutableArray();

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
        [FromServices] IExecutionJobStore? jobStore = null)
    {
        EnrichActivity("GetJobStatus");

        var gate = context.RequestServices.GetRequiredService<OperatorApprovalGate>();
        var authDecision = gate.CheckAuthorization(context.User, new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Job,
            Operation = OperatorOperation.Read
        });
        if (!authDecision.IsAllowed)
        {
            OgcProcessesLog.AuthorizationDenied(logger, OperatorResourceType.Job.ToString(), OperatorOperation.Read.ToString());
            return ProcessEndpoints.FormatOgcAuthError(authDecision);
        }

        OgcProcessesLog.JobStatusRequested(logger, jobId);

        if (jobStore == null)
        {
            OgcProcessesLog.JobStoreUnavailable(logger);
            return JobStoreUnavailableResult();
        }

        var job = await jobStore.GetAsync(jobId, context.RequestAborted).ConfigureAwait(false);
        if (job == null || job.Spec.Kind != ExecutionJobKind.Geoprocessing)
        {
            OgcProcessesLog.JobNotFound(logger, jobId);
            return JobNotFoundResult(jobId);
        }

        authDecision = gate.CheckAuthorization(context.User, new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Job,
            ResourceId = job.OperationId,
            Operation = OperatorOperation.Read
        });
        if (!authDecision.IsAllowed)
        {
            OgcProcessesLog.AuthorizationDenied(logger, OperatorResourceType.Job.ToString(), OperatorOperation.Read.ToString());
            return ProcessEndpoints.FormatOgcAuthError(authDecision);
        }

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var statusInfo = OgcProcessesConversionHelpers.ToOgcStatusInfo(
            job, ProcessEndpoints.CanonicalProcessId, baseUrl);

        return Results.Json(statusInfo, OgcProcessesJsonContext.Default.OgcStatusInfo, MediaTypes.Json);
    }

    private static async Task<IResult> GetJobResults(
        string jobId,
        HttpContext context,
        ILogger<OgcProcessesEndpointsLog> logger,
        [FromServices] IExecutionJobStore? jobStore = null)
    {
        EnrichActivity("GetJobResults");

        var gate = context.RequestServices.GetRequiredService<OperatorApprovalGate>();
        var authDecision = gate.CheckAuthorization(context.User, new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Job,
            Operation = OperatorOperation.Read
        });
        if (!authDecision.IsAllowed)
        {
            OgcProcessesLog.AuthorizationDenied(logger, OperatorResourceType.Job.ToString(), OperatorOperation.Read.ToString());
            return ProcessEndpoints.FormatOgcAuthError(authDecision);
        }

        OgcProcessesLog.JobResultsRequested(logger, jobId);

        if (jobStore == null)
        {
            OgcProcessesLog.JobStoreUnavailable(logger);
            return JobStoreUnavailableResult();
        }

        var job = await jobStore.GetAsync(jobId, context.RequestAborted).ConfigureAwait(false);
        if (job == null || job.Spec.Kind != ExecutionJobKind.Geoprocessing)
        {
            OgcProcessesLog.JobNotFound(logger, jobId);
            return JobNotFoundResult(jobId);
        }

        authDecision = gate.CheckAuthorization(context.User, new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Job,
            ResourceId = job.OperationId,
            Operation = OperatorOperation.Read
        });
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

        // V1: document-mode, by-value results only.
        // Result storage will be populated when the execution engine is available.
        // For now return an empty results document for successful jobs,
        // or an error for failed/dismissed jobs.
        if (job.Status == ExecutionJobStatus.Failed)
        {
            return Results.Json(
                new OgcProcessError
                {
                    Type = "about:blank",
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

        // OGC API Processes — Part 1 §7.11.1: a successful job must return a results
        // document, not a 404. The canonical process declares no value-typed outputs in
        // V1, so the spec-conformant body is an empty object. Clients walking the job
        // lifecycle now see 200 OK + `{}` instead of "not available".
        // When the artifact store is wired up the dictionary will be populated from the
        // job's ArtifactRefs; until then the endpoint stays spec-legal.
        return Results.Text("{}", MediaTypes.Json, statusCode: StatusCodes.Status200OK);
    }

    private static async Task<IResult> DismissJob(
        string jobId,
        HttpContext context,
        ILogger<OgcProcessesEndpointsLog> logger,
        IEnumerable<IJobCancellationNotifier> cancellationNotifiers,
        IUniversalProgressStore progressStore,
        [FromServices] IExecutionJobStore? jobStore = null,
        [FromServices] IJobQueue? jobQueue = null,
        [FromServices] IEnumerable<IBatchComputeBackend>? backends = null)
    {
        EnrichActivity("DismissJob");

        var gate = context.RequestServices.GetRequiredService<OperatorApprovalGate>();

        var authDecision = gate.CheckAuthorization(context.User, new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Job,
            Operation = OperatorOperation.Execute
        });
        if (!authDecision.IsAllowed)
        {
            OgcProcessesLog.AuthorizationDenied(logger, OperatorResourceType.Job.ToString(), OperatorOperation.Execute.ToString());
            return ProcessEndpoints.FormatOgcAuthError(authDecision);
        }

        OgcProcessesLog.JobDismissRequested(logger, jobId);

        if (jobStore == null)
        {
            OgcProcessesLog.JobStoreUnavailable(logger);
            return JobStoreUnavailableResult();
        }

        var job = await jobStore.GetAsync(jobId, context.RequestAborted).ConfigureAwait(false);
        if (job == null || job.Spec.Kind != ExecutionJobKind.Geoprocessing)
        {
            OgcProcessesLog.JobNotFound(logger, jobId);
            return JobNotFoundResult(jobId);
        }

        authDecision = gate.CheckAuthorization(context.User, new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Job,
            ResourceId = job.OperationId,
            Operation = OperatorOperation.Execute,
            IsDestructive = true
        });
        if (!authDecision.IsAllowed)
        {
            OgcProcessesLog.AuthorizationDenied(logger, OperatorResourceType.Job.ToString(), OperatorOperation.Execute.ToString());
            return ProcessEndpoints.FormatOgcAuthError(authDecision);
        }

        // Already dismissed — reconcile side effects and return current status
        if (job.Status == ExecutionJobStatus.Cancelled)
        {
            if (jobQueue != null)
            {
                try
                {
                    await jobQueue.RemoveAsync(jobId, context.RequestAborted).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    OgcProcessesLog.QueueRemovalFailed(logger, jobId, ex);
                }
            }

            var staleProgress = await progressStore.GetProgressAsync<GeoprocessingProgress>(
                jobId, context.RequestAborted).ConfigureAwait(false);
            if (staleProgress != null && staleProgress.Status != OperationStatus.Cancelled)
            {
                var reconciledProgress = staleProgress.WithCancellation(DateTimeOffset.UtcNow, "Dismissed via OGC API");
                await progressStore.SetProgressAsync(
                    jobId, reconciledProgress, TimeSpan.FromDays(7), context.RequestAborted).ConfigureAwait(false);
            }

            var baseUrl2 = BaseUrlResolver.GetBaseUrl(context);
            return OgcProcessesResults.Dismissed(job, ProcessEndpoints.CanonicalProcessId, baseUrl2);
        }

        // Cannot dismiss terminal jobs (succeeded/failed)
        if (OgcProcessesConversionHelpers.IsTerminal(job.Status))
        {
            await ExecutionJobSubmissionHelper.BridgeTerminalSubmissionProgressAsync(
                progressStore, job, TimeSpan.FromDays(7), cancellationToken: context.RequestAborted).ConfigureAwait(false);
            OgcProcessesLog.DismissRejectedTerminal(logger, jobId, OgcProcessesConversionHelpers.ToOgcStatus(job.Status));
            return Results.Json(
                new OgcProcessError
                {
                    Type = "about:blank",
                    Title = "Cannot dismiss completed job",
                    Status = StatusCodes.Status409Conflict,
                    Detail = $"Job '{jobId}' is in terminal state '{OgcProcessesConversionHelpers.ToOgcStatus(job.Status)}' and cannot be dismissed."
                },
                OgcProcessesJsonContext.Default.OgcProcessError,
                MediaTypes.Json,
                StatusCodes.Status409Conflict);
        }

        // Dismissing a running job is a destructive action — require approval.
        // Evaluated after state checks so not-found, idempotent, and terminal paths
        // remain reachable regardless of approval policy, matching CancelJobAsync.
        var approval = gate.CheckApproval(context.User, new OperatorAuthorizationRequest
        {
            ResourceType = OperatorResourceType.Job,
            Operation = OperatorOperation.Execute,
            IsDestructive = true
        });
        if (approval.IsRequired)
        {
            OgcProcessesLog.DismissRejectedApprovalRequired(logger, jobId, approval.PolicyRef ?? "unknown");
            return ProcessEndpoints.FormatOgcApprovalError(approval);
        }

        // Attempt cancellation via the canonical notifier
        var workerOwnsTerminalState = cancellationNotifiers.CancelAny(jobId);

        if (!workerOwnsTerminalState)
        {
            // Re-read to catch concurrent terminal transitions (e.g. reconciler
            // revoked the stale CTS after requeue or terminal failure).
            var latest = await jobStore.GetAsync(jobId, context.RequestAborted).ConfigureAwait(false);
            if (latest == null)
            {
                OgcProcessesLog.JobNotFound(logger, jobId);
                return JobNotFoundResult(jobId);
            }

            if (!OgcProcessesConversionHelpers.IsTerminal(latest.Status)
                && !string.Equals(latest.Spec.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal)
                && backends != null)
            {
                var backend = backends.Resolve(latest.Spec.Backend, latest.Spec.TargetKind);
                if (backend != null)
                {
                    var capabilities = await backend.GetCapabilitiesAsync(context.RequestAborted).ConfigureAwait(false);
                    if (capabilities.SupportsCancellation)
                    {
                        if (latest.Status == ExecutionJobStatus.Queued && !ExecutionJobCancellationHelper.HasSubmittedProviderMarker(latest))
                        {
                            var preResult = await ExecutionJobCancellationHelper.TryCancelPreSubmissionAsync(
                                jobStore, latest, context.RequestAborted).ConfigureAwait(false);

                            switch (preResult.Outcome)
                            {
                                case PreSubmissionCancelOutcome.TerminalConflict:
                                    await ExecutionJobSubmissionHelper.BridgeTerminalSubmissionProgressAsync(
                                        progressStore, preResult.Job!, TimeSpan.FromDays(7), cancellationToken: context.RequestAborted).ConfigureAwait(false);
                                    OgcProcessesLog.DismissRejectedTerminal(logger, jobId, OgcProcessesConversionHelpers.ToOgcStatus(preResult.Job!.Status));
                                    return Results.Json(
                                        new OgcProcessError
                                        {
                                            Type = "about:blank",
                                            Title = "Cannot dismiss completed job",
                                            Status = StatusCodes.Status409Conflict,
                                            Detail = $"Job '{jobId}' reached terminal state '{OgcProcessesConversionHelpers.ToOgcStatus(preResult.Job!.Status)}' before dismiss could be applied."
                                        },
                                        OgcProcessesJsonContext.Default.OgcProcessError,
                                        MediaTypes.Json,
                                        StatusCodes.Status409Conflict);
                                case PreSubmissionCancelOutcome.Missing:
                                    OgcProcessesLog.JobNotFound(logger, jobId);
                                    return JobNotFoundResult(jobId);
                                case PreSubmissionCancelOutcome.Unconfirmed:
                                    return Results.Json(
                                        new OgcProcessError
                                        {
                                            Type = "about:blank",
                                            Title = "Dismiss could not be confirmed",
                                            Status = StatusCodes.Status409Conflict,
                                            Detail = $"Job '{jobId}' pre-submission dismiss could not be confirmed."
                                        },
                                        OgcProcessesJsonContext.Default.OgcProcessError,
                                        MediaTypes.Json,
                                        StatusCodes.Status409Conflict);
                            }

                            await ExecutionJobSubmissionHelper.BridgeTerminalSubmissionProgressAsync(
                                progressStore, preResult.Job!, TimeSpan.FromDays(7), cancellationToken: context.RequestAborted).ConfigureAwait(false);
                            job = preResult.Job!;
                            var baseUrlLocal = BaseUrlResolver.GetBaseUrl(context);
                            return OgcProcessesResults.Dismissed(job, ProcessEndpoints.CanonicalProcessId, baseUrlLocal);
                        }

                        // Stamp CancellationRequestedAt before the remote cancel so a
                        // concurrent dismiss that races ahead and sees the provider job
                        // already gone maps NotFound → Cancelled rather than Failed.
                        var stampResult = await ExecutionJobCancellationHelper.TryStampRemoteCancelRequestedAtAsync(
                            jobStore, latest, cancellationToken: context.RequestAborted).ConfigureAwait(false);
                        switch (stampResult.Outcome)
                        {
                            case RemoteCancelStampOutcome.Missing:
                                OgcProcessesLog.JobNotFound(logger, jobId);
                                return JobNotFoundResult(jobId);
                            case RemoteCancelStampOutcome.TerminalConflict:
                                await ExecutionJobSubmissionHelper.BridgeTerminalSubmissionProgressAsync(
                                    progressStore, stampResult.Job!, TimeSpan.FromDays(7), cancellationToken: context.RequestAborted).ConfigureAwait(false);
                                OgcProcessesLog.DismissRejectedTerminal(logger, jobId, OgcProcessesConversionHelpers.ToOgcStatus(stampResult.Job!.Status));
                                return Results.Json(
                                    new OgcProcessError
                                    {
                                        Type = "about:blank",
                                        Title = "Cannot dismiss completed job",
                                        Status = StatusCodes.Status409Conflict,
                                        Detail = $"Job '{jobId}' reached terminal state '{OgcProcessesConversionHelpers.ToOgcStatus(stampResult.Job!.Status)}' before dismiss could be applied."
                                    },
                                    OgcProcessesJsonContext.Default.OgcProcessError,
                                    MediaTypes.Json,
                                    StatusCodes.Status409Conflict);
                            case RemoteCancelStampOutcome.Unconfirmed:
                                return Results.Json(
                                    new OgcProcessError
                                    {
                                        Type = "about:blank",
                                        Title = "Dismiss could not be confirmed",
                                        Status = StatusCodes.Status409Conflict,
                                        Detail = $"Job '{jobId}' dismiss could not be confirmed after retries."
                                    },
                                    OgcProcessesJsonContext.Default.OgcProcessError,
                                    MediaTypes.Json,
                                    StatusCodes.Status409Conflict);
                        }

                        var stampedJob = stampResult.Job!;
                        var observation = await backend.CancelAsync(stampedJob, context.RequestAborted).ConfigureAwait(false);
                        var applyResult = await ExecutionJobCancellationHelper.TryApplyBackendCancelAsync(
                            jobStore, stampedJob, observation, context.RequestAborted).ConfigureAwait(false);

                        switch (applyResult.Outcome)
                        {
                            case BackendCancelApplyOutcome.Missing:
                                OgcProcessesLog.JobNotFound(logger, jobId);
                                return JobNotFoundResult(jobId);
                            case BackendCancelApplyOutcome.TerminalConflict:
                                await ExecutionJobSubmissionHelper.BridgeTerminalSubmissionProgressAsync(
                                    progressStore, applyResult.Job!, TimeSpan.FromDays(7), cancellationToken: context.RequestAborted).ConfigureAwait(false);
                                OgcProcessesLog.DismissRejectedTerminal(logger, jobId, OgcProcessesConversionHelpers.ToOgcStatus(applyResult.Job!.Status));
                                return Results.Json(
                                    new OgcProcessError
                                    {
                                        Type = "about:blank",
                                        Title = "Cannot dismiss completed job",
                                        Status = StatusCodes.Status409Conflict,
                                        Detail = $"Job '{jobId}' reached terminal state '{OgcProcessesConversionHelpers.ToOgcStatus(applyResult.Job!.Status)}' before dismiss could be applied."
                                    },
                                    OgcProcessesJsonContext.Default.OgcProcessError,
                                    MediaTypes.Json,
                                    StatusCodes.Status409Conflict);
                            case BackendCancelApplyOutcome.Unconfirmed:
                                return Results.Json(
                                    new OgcProcessError
                                    {
                                        Type = "about:blank",
                                        Title = "Dismiss could not be confirmed",
                                        Status = StatusCodes.Status409Conflict,
                                        Detail = $"Job '{jobId}' dismiss could not be confirmed after retries."
                                    },
                                    OgcProcessesJsonContext.Default.OgcProcessError,
                                    MediaTypes.Json,
                                    StatusCodes.Status409Conflict);
                        }

                        var cancelled = applyResult.Job!;

                        await ExecutionJobSubmissionHelper.BridgeExecutionJobProgressAsync(
                            progressStore, cancelled, TimeSpan.FromDays(7), cancellationToken: context.RequestAborted).ConfigureAwait(false);

                        if (cancelled.Status is ExecutionJobStatus.Succeeded or ExecutionJobStatus.Failed)
                        {
                            OgcProcessesLog.DismissRejectedTerminal(logger, jobId, OgcProcessesConversionHelpers.ToOgcStatus(cancelled.Status));
                            return Results.Json(
                                new OgcProcessError
                                {
                                    Type = "about:blank",
                                    Title = "Cannot dismiss completed job",
                                    Status = StatusCodes.Status409Conflict,
                                    Detail = $"Job '{jobId}' reached terminal state '{OgcProcessesConversionHelpers.ToOgcStatus(cancelled.Status)}' before dismiss could be applied."
                                },
                                OgcProcessesJsonContext.Default.OgcProcessError,
                                MediaTypes.Json,
                                StatusCodes.Status409Conflict);
                        }

                        job = cancelled;
                        var baseUrlBackend = BaseUrlResolver.GetBaseUrl(context);
                        return OgcProcessesResults.Dismissed(job, ProcessEndpoints.CanonicalProcessId, baseUrlBackend);
                    }
                }
            }

            if (OgcProcessesConversionHelpers.IsTerminal(latest.Status))
            {
                if (latest.Status is ExecutionJobStatus.Succeeded or ExecutionJobStatus.Failed)
                {
                    await ExecutionJobSubmissionHelper.BridgeTerminalSubmissionProgressAsync(
                        progressStore, latest, TimeSpan.FromDays(7), cancellationToken: context.RequestAborted).ConfigureAwait(false);
                    OgcProcessesLog.DismissRejectedTerminal(logger, jobId, OgcProcessesConversionHelpers.ToOgcStatus(latest.Status));
                    return Results.Json(
                        new OgcProcessError
                        {
                            Type = "about:blank",
                            Title = "Cannot dismiss completed job",
                            Status = StatusCodes.Status409Conflict,
                            Detail = $"Job '{jobId}' is in terminal state '{OgcProcessesConversionHelpers.ToOgcStatus(latest.Status)}' and cannot be dismissed."
                        },
                        OgcProcessesJsonContext.Default.OgcProcessError,
                        MediaTypes.Json,
                        StatusCodes.Status409Conflict);
                }

                if (jobQueue != null)
                {
                    try
                    {
                        await jobQueue.RemoveAsync(jobId, context.RequestAborted).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        OgcProcessesLog.QueueRemovalFailed(logger, jobId, ex);
                    }
                }

                var staleProgress = await progressStore.GetProgressAsync<GeoprocessingProgress>(
                    jobId, context.RequestAborted).ConfigureAwait(false);
                if (staleProgress != null && staleProgress.Status != OperationStatus.Cancelled)
                {
                    var reconciledProgress = staleProgress.WithCancellation(DateTimeOffset.UtcNow, "Dismissed via OGC API");
                    await progressStore.SetProgressAsync(
                        jobId, reconciledProgress, TimeSpan.FromDays(7), context.RequestAborted).ConfigureAwait(false);
                }

                job = latest;
            }
            else if (!string.Equals(latest.Spec.Backend, LocalBatchComputeBackend.BackendId, StringComparison.Ordinal))
            {
                return Results.Json(
                    new OgcProcessError
                    {
                        Type = "about:blank",
                        Title = "Cancellation not supported",
                        Status = StatusCodes.Status409Conflict,
                        Detail = $"Job '{jobId}' runs on backend '{latest.Spec.Backend}' which does not support dismissal."
                    },
                    OgcProcessesJsonContext.Default.OgcProcessError,
                    MediaTypes.Json,
                    StatusCodes.Status409Conflict);
            }
            else
            {
                var cancelOutcome = await ExecutionJobCancellationHelper.TryApplyAsync(
                    jobStore,
                    jobId,
                    latest,
                    "Dismissed",
                    cancellationToken: context.RequestAborted).ConfigureAwait(false);

                switch (cancelOutcome.State)
                {
                    case ExecutionJobCancellationState.Cancelled:
                        if (jobQueue != null)
                        {
                            try
                            {
                                await jobQueue.RemoveAsync(jobId, context.RequestAborted).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                OgcProcessesLog.QueueRemovalFailed(logger, jobId, ex);
                            }
                        }

                        var progress = await progressStore.GetProgressAsync<GeoprocessingProgress>(
                            jobId, context.RequestAborted).ConfigureAwait(false);
                        if (progress != null)
                        {
                            var cancelledProgress = progress.WithCancellation(DateTimeOffset.UtcNow, "Dismissed via OGC API");
                            await progressStore.SetProgressAsync(
                                jobId, cancelledProgress, TimeSpan.FromDays(7), context.RequestAborted).ConfigureAwait(false);
                        }

                        job = cancelOutcome.Job ?? latest;
                        break;
                    case ExecutionJobCancellationState.CancellationRequested:
                        job = cancelOutcome.Job ?? latest;
                        break;
                    case ExecutionJobCancellationState.TerminalConflict:
                        var terminalStatus = cancelOutcome.Job != null
                            ? OgcProcessesConversionHelpers.ToOgcStatus(cancelOutcome.Job.Status)
                            : "unknown";
                        if (cancelOutcome.Job != null)
                        {
                            await ExecutionJobSubmissionHelper.BridgeTerminalSubmissionProgressAsync(
                                progressStore, cancelOutcome.Job, TimeSpan.FromDays(7), cancellationToken: context.RequestAborted).ConfigureAwait(false);
                        }
                        OgcProcessesLog.DismissRejectedTerminal(logger, jobId, terminalStatus);
                        return Results.Json(
                            new OgcProcessError
                            {
                                Type = "about:blank",
                                Title = "Cannot dismiss completed job",
                                Status = StatusCodes.Status409Conflict,
                                Detail = $"Job '{jobId}' reached terminal state '{terminalStatus}' before dismiss could be applied."
                            },
                            OgcProcessesJsonContext.Default.OgcProcessError,
                            MediaTypes.Json,
                            StatusCodes.Status409Conflict);
                    case ExecutionJobCancellationState.Missing:
                        return JobNotFoundResult(jobId);
                    case ExecutionJobCancellationState.Unconfirmed:
                        return Results.Json(
                            new OgcProcessError
                            {
                                Type = "about:blank",
                                Title = "Dismiss could not be confirmed",
                                Status = StatusCodes.Status409Conflict,
                                Detail = $"Job '{jobId}' dismiss could not be confirmed after retries."
                            },
                            OgcProcessesJsonContext.Default.OgcProcessError,
                            MediaTypes.Json,
                            StatusCodes.Status409Conflict);
                    default:
                        throw new InvalidOperationException($"Unexpected durable cancellation outcome '{cancelOutcome.State}'.");
                }
            }
        }
        else
        {
            // Worker will handle terminal state; re-read to get latest
            job = await jobStore.GetAsync(jobId, context.RequestAborted).ConfigureAwait(false) ?? job;
        }

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        return BuildDismissOutcomeResult(baseUrl, jobId, job);
    }

    private static IResult JobStoreUnavailableResult() => OgcProcessesResults.StoreUnavailable();

    private static IResult JobNotFoundResult(string jobId) => OgcProcessesResults.NoSuchJob(jobId);

    private static IResult BuildDismissOutcomeResult(
        string baseUrl,
        string jobId,
        ExecutionJobRecord job)
        => job.Status == ExecutionJobStatus.Cancelled
            ? OgcProcessesResults.Dismissed(job, ProcessEndpoints.CanonicalProcessId, baseUrl)
            : Results.Json(
                OgcProcessesConversionHelpers.ToOgcStatusInfo(
                    job,
                    ProcessEndpoints.CanonicalProcessId,
                    baseUrl),
                OgcProcessesJsonContext.Default.OgcStatusInfo,
                MediaTypes.Json);

    private static void EnrichActivity(string operation)
    {
        var activity = Activity.Current;
        if (activity == null) return;
        activity.SetTag(HonuaTelemetry.Tags.Protocol, "OGC-API-Processes");
        activity.SetTag(HonuaTelemetry.Tags.Operation, operation);
    }

}

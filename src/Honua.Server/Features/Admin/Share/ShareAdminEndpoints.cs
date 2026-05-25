// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Share.Abstractions;
using Honua.Core.Features.Share.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Honua.Server.Features.Infrastructure.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Admin.Share;

internal static class ShareAdminEndpoints
{
    internal const int DefaultPageSize = 50;
    internal const int MaxPageSize = 200;

    private const int DefaultTrafficHours = 24;
    private const int DefaultBucketMinutes = 60;
    private const int MaxTrafficBuckets = 2_000;

    internal sealed class ShareAdminEndpointsLog;

    public static void MapShareAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/share")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Share")
            .WithDescription("Console Share export definitions, export run history, and Share traffic projections")
            .RequireAdminAuthorization();

        group.MapGet("/exports", HandleListDefinitions)
            .WithName("ListShareExportDefinitions")
            .WithSummary("List scheduled Share export definitions")
            .Produces<ShareExportDefinitionPageResponse>();

        group.MapPost("/exports", HandleCreateDefinition)
            .WithName("CreateShareExportDefinition")
            .WithSummary("Create a scheduled Share export definition")
            .Produces<ShareExportDefinitionResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest);

        group.MapGet("/exports/{exportId}", HandleGetDefinition)
            .WithName("GetShareExportDefinition")
            .WithSummary("Get a scheduled Share export definition")
            .Produces<ShareExportDefinitionResponse>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapPut("/exports/{exportId}", HandleUpdateDefinition)
            .WithName("UpdateShareExportDefinition")
            .WithSummary("Replace a scheduled Share export definition")
            .Produces<ShareExportDefinitionResponse>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/exports/{exportId}", HandleDeleteDefinition)
            .WithName("DeleteShareExportDefinition")
            .WithSummary("Delete a scheduled Share export definition and its run history")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/exports/{exportId}/trigger", HandleTrigger)
            .WithName("TriggerShareExport")
            .WithSummary("Manually trigger a Share export run")
            .Produces<ShareExportRunResponse>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/exports/{exportId}/pause", HandlePause)
            .WithName("PauseShareExport")
            .WithSummary("Pause a scheduled Share export definition")
            .Produces<ShareExportDefinitionResponse>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/exports/{exportId}/resume", HandleResume)
            .WithName("ResumeShareExport")
            .WithSummary("Resume a scheduled Share export definition")
            .Produces<ShareExportDefinitionResponse>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/exports/{exportId}/runs", HandleListRuns)
            .WithName("ListShareExportRuns")
            .WithSummary("List Share export run history")
            .Produces<ShareExportRunPageResponse>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/exports/{exportId}/runs/{runId}", HandleGetRun)
            .WithName("GetShareExportRun")
            .WithSummary("Get Share export run detail")
            .Produces<ShareExportRunResponse>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/traffic", HandleAggregateTrafficSummary)
            .WithName("GetAggregateShareTraffic")
            .WithSummary("Get aggregate Share traffic summary")
            .Produces<ShareTrafficSummaryResponse>();

        group.MapGet("/traffic/series", HandleAggregateTrafficSeries)
            .WithName("GetAggregateShareTrafficSeries")
            .WithSummary("Get aggregate Share traffic time series")
            .Produces<ShareTrafficSeriesResponse>();

        var serviceShare = endpoints.MapGroup("/api/v{version:apiVersion}/admin/services/{serviceName}/layers/{layerId:int}/share")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Share")
            .RequireAdminAuthorization();

        serviceShare.MapGet("/traffic", HandleItemTrafficSummary)
            .WithName("GetItemShareTraffic")
            .WithSummary("Get per-item Share traffic summary")
            .Produces<ShareTrafficSummaryResponse>();

        serviceShare.MapGet("/traffic/series", HandleItemTrafficSeries)
            .WithName("GetItemShareTrafficSeries")
            .WithSummary("Get per-item Share traffic time series")
            .Produces<ShareTrafficSeriesResponse>();
    }

    private static async Task<IResult> HandleListDefinitions(
        HttpContext context,
        [FromServices] IShareExportStore store,
        [FromServices] ILogger<ShareAdminEndpointsLog> logger,
        [FromQuery] string? serviceName = null,
        [FromQuery] string? resourceId = null,
        [FromQuery] int? layerId = null,
        [FromQuery] string? destinationType = null,
        [FromQuery] string? scheduleState = null,
        [FromQuery] string? cursor = null,
        [FromQuery] int? limit = null)
    {
        if (!TryValidateCursor(cursor, out var cursorError))
        {
            return BadRequest(context, cursorError);
        }

        if (!TryParseOptionalEnum(destinationType, out ShareExportDestinationType? destination, out var destinationError))
        {
            return BadRequest(context, destinationError);
        }

        if (!TryParseOptionalEnum(scheduleState, out ShareExportScheduleState? state, out var stateError))
        {
            return BadRequest(context, stateError);
        }

        try
        {
            var page = await store.ListDefinitionsAsync(
                    new ShareExportDefinitionQuery
                    {
                        ServiceName = NullIfWhiteSpace(serviceName),
                        ResourceId = NullIfWhiteSpace(resourceId),
                        LayerId = layerId,
                        DestinationType = destination,
                        ScheduleState = state,
                        Cursor = cursor,
                        Limit = ClampLimit(limit)
                    },
                    context.RequestAborted)
                .ConfigureAwait(false);

            return Results.Json(
                new ShareExportDefinitionPageResponse
                {
                    Items = page.Items.Select(MapDefinition).ToArray(),
                    NextCursor = page.NextCursor
                },
                ShareAdminJsonContext.Default.ShareExportDefinitionPageResponse);
        }
        catch (ShareExportStoreUnavailableException ex)
        {
            return Unavailable(context, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShareAdminLog.EndpointFailed(logger, "share.exports.list", ex);
            return ServerError(context, "Share export listing failed.");
        }
    }

    private static async Task<IResult> HandleCreateDefinition(
        HttpContext context,
        ShareExportDefinitionRequest request,
        [FromServices] IShareExportStore store,
        [FromServices] IShareExportDestinationResolver destinationResolver,
        [FromServices] TimeProvider timeProvider,
        [FromServices] ILogger<ShareAdminEndpointsLog> logger)
    {
        var now = timeProvider.GetUtcNow();
        if (!TryBuildDefinition(
                context,
                request,
                exportId: Guid.NewGuid().ToString("N"),
                createdAt: now,
                updatedAt: now,
                lastRunAt: null,
                nextRunAt: null,
                destinationResolver,
                out var definition,
                out var validationProblem))
        {
            return validationProblem;
        }

        try
        {
            var stored = await store.CreateDefinitionAsync(definition, context.RequestAborted).ConfigureAwait(false);
            context.Response.Headers.Location = $"/api/v1/admin/share/exports/{stored.ExportId}";
            ShareAdminLog.ExportDefinitionCreated(
                logger,
                stored.ExportId,
                stored.ServiceName,
                stored.LayerId,
                stored.DestinationStatus);
            return Results.Json(
                MapDefinition(stored),
                ShareAdminJsonContext.Default.ShareExportDefinitionResponse,
                statusCode: StatusCodes.Status201Created);
        }
        catch (ShareExportStoreConflictException ex)
        {
            return Conflict(context, ex.Message);
        }
        catch (ShareExportStoreUnavailableException ex)
        {
            return Unavailable(context, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShareAdminLog.EndpointFailed(logger, "share.exports.create", ex);
            return ServerError(context, "Share export creation failed.");
        }
    }

    private static async Task<IResult> HandleGetDefinition(
        HttpContext context,
        string exportId,
        [FromServices] IShareExportStore store,
        [FromServices] ILogger<ShareAdminEndpointsLog> logger)
    {
        try
        {
            var definition = await store.GetDefinitionAsync(exportId, context.RequestAborted).ConfigureAwait(false);
            return definition is null
                ? NotFound(context, "Share export definition was not found.")
                : Results.Json(MapDefinition(definition), ShareAdminJsonContext.Default.ShareExportDefinitionResponse);
        }
        catch (ShareExportStoreUnavailableException ex)
        {
            return Unavailable(context, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShareAdminLog.EndpointFailed(logger, "share.exports.get", ex);
            return ServerError(context, "Share export retrieval failed.");
        }
    }

    private static async Task<IResult> HandleUpdateDefinition(
        HttpContext context,
        string exportId,
        ShareExportDefinitionRequest request,
        [FromServices] IShareExportStore store,
        [FromServices] IShareExportDestinationResolver destinationResolver,
        [FromServices] TimeProvider timeProvider,
        [FromServices] ILogger<ShareAdminEndpointsLog> logger)
    {
        try
        {
            var existing = await store.GetDefinitionAsync(exportId, context.RequestAborted).ConfigureAwait(false);
            if (existing is null)
            {
                return NotFound(context, "Share export definition was not found.");
            }

            if (!TryBuildDefinition(
                    context,
                    request,
                    exportId,
                    existing.CreatedAt,
                    timeProvider.GetUtcNow(),
                    existing.LastRunAt,
                    existing.NextRunAt,
                    destinationResolver,
                    out var definition,
                    out var validationProblem))
            {
                return validationProblem;
            }

            var stored = await store.UpdateDefinitionAsync(definition, context.RequestAborted).ConfigureAwait(false);
            return stored is null
                ? NotFound(context, "Share export definition was not found.")
                : Results.Json(MapDefinition(stored), ShareAdminJsonContext.Default.ShareExportDefinitionResponse);
        }
        catch (ShareExportStoreUnavailableException ex)
        {
            return Unavailable(context, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShareAdminLog.EndpointFailed(logger, "share.exports.update", ex);
            return ServerError(context, "Share export update failed.");
        }
    }

    private static async Task<IResult> HandleDeleteDefinition(
        HttpContext context,
        string exportId,
        [FromServices] IShareExportStore store,
        [FromServices] ILogger<ShareAdminEndpointsLog> logger)
    {
        try
        {
            var deleted = await store.DeleteDefinitionAsync(exportId, context.RequestAborted).ConfigureAwait(false);
            return deleted ? Results.NoContent() : NotFound(context, "Share export definition was not found.");
        }
        catch (ShareExportStoreUnavailableException ex)
        {
            return Unavailable(context, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShareAdminLog.EndpointFailed(logger, "share.exports.delete", ex);
            return ServerError(context, "Share export deletion failed.");
        }
    }

    private static Task<IResult> HandlePause(
        HttpContext context,
        string exportId,
        [FromServices] IShareExportStore store,
        [FromServices] TimeProvider timeProvider,
        [FromServices] ILogger<ShareAdminEndpointsLog> logger)
        => UpdateScheduleStateAsync(
            context,
            exportId,
            ShareExportScheduleState.Paused,
            store,
            timeProvider,
            logger,
            "share.exports.pause");

    private static Task<IResult> HandleResume(
        HttpContext context,
        string exportId,
        [FromServices] IShareExportStore store,
        [FromServices] TimeProvider timeProvider,
        [FromServices] ILogger<ShareAdminEndpointsLog> logger)
        => UpdateScheduleStateAsync(
            context,
            exportId,
            ShareExportScheduleState.Active,
            store,
            timeProvider,
            logger,
            "share.exports.resume");

    private static async Task<IResult> HandleTrigger(
        HttpContext context,
        string exportId,
        [FromServices] IShareExportStore store,
        [FromServices] IShareExportDestinationResolver destinationResolver,
        [FromServices] TimeProvider timeProvider,
        [FromServices] ILogger<ShareAdminEndpointsLog> logger)
    {
        // The durable job runner is optional: only a Supported trigger needs it, and the
        // definition read plus unsupported/not-configured paths do not. Resolve both pieces from
        // the request scope instead of as injected handler parameters so this handler stays within
        // the endpoint dependency-injection limit (DependencyInjectionLimitsTests).
        var jobStore = context.RequestServices.GetService<IExecutionJobStore>();
        var jobQueue = context.RequestServices.GetService<IJobQueue>();

        try
        {
            var definition = await store.GetDefinitionAsync(exportId, context.RequestAborted).ConfigureAwait(false);
            if (definition is null)
            {
                return NotFound(context, "Share export definition was not found.");
            }

            var now = timeProvider.GetUtcNow();
            var currentStatus = destinationResolver.Resolve(definition.DestinationType);
            if (currentStatus != definition.DestinationStatus)
            {
                definition = definition with
                {
                    DestinationStatus = currentStatus,
                    UpdatedAt = now
                };
                await store.UpdateDefinitionAsync(definition, context.RequestAborted).ConfigureAwait(false);
            }

            ShareAdminTelemetry.RecordExportTriggered(
                definition.DestinationType.ToString(),
                currentStatus.ToString());

            using var activity = HonuaTelemetry.ActivitySource.StartActivity(HonuaTelemetry.Activities.ShareExportTrigger);
            activity?.SetTag(HonuaTelemetry.Tags.ShareExportId, definition.ExportId);
            activity?.SetTag(HonuaTelemetry.Tags.ShareDestinationType, definition.DestinationType.ToString());
            activity?.SetTag(HonuaTelemetry.Tags.ServiceId, definition.ServiceName);
            activity?.SetTag(HonuaTelemetry.Tags.LayerId, definition.LayerId);

            if (currentStatus != ShareExportDestinationStatus.Supported)
            {
                var failedRun = BuildRun(
                    definition,
                    now,
                    ShareExportRunStatus.Failed,
                    jobRunId: null,
                    lastError: currentStatus == ShareExportDestinationStatus.NotConfigured
                        ? "share-export-destination-not-configured"
                        : "share-export-destination-unsupported");
                await store.AppendRunAsync(failedRun, context.RequestAborted).ConfigureAwait(false);
                ShareAdminLog.ExportDestinationUnavailable(logger, exportId, currentStatus);
                activity?.SetTag(HonuaTelemetry.Tags.ShareRunId, failedRun.RunId);
                activity?.SetStatus(ActivityStatusCode.Error, failedRun.LastError);
                activity?.SetTag(HonuaTelemetry.Tags.Error, true);
                activity?.SetTag(HonuaTelemetry.Tags.ErrorMessage, failedRun.LastError);
                return UnprocessableDestination(context, currentStatus);
            }

            // A Supported destination runs on the local batch backend, which is dispatched by
            // enqueuing onto the durable job queue for a worker to claim. Both the durable job
            // store and the queue must be present, otherwise the job would be created but never
            // dispatched (stuck Queued forever). Bail before creating any record so no orphan job
            // is left behind.
            if (jobStore is null || jobQueue is null)
            {
                return Unavailable(context, "Share export job runner is not configured.");
            }

            var run = BuildRun(definition, now, ShareExportRunStatus.Queued, jobRunId: CreateJobRunId(), lastError: null);
            var job = BuildExecutionJob(context, definition, run, now);
            var created = await jobStore.TryCreateAsync(job, cancellationToken: context.RequestAborted).ConfigureAwait(false);
            if (!created)
            {
                return Unavailable(context, "Share export job runner could not create a job record.");
            }

            try
            {
                // Enqueue after the durable job record exists (queue contract requires the job to
                // be persisted first) so a worker can claim and execute it.
                await jobQueue.EnqueueAsync(job.OperationId, OperationPriority.Normal, context.RequestAborted)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Dispatch failed: roll the created job back to a terminal Failed state and record a
                // Failed run so a returned run never lingers as a Queued job no worker will run.
                await ExecutionJobSubmissionHelper.TryRollbackCreatedJobAsync(
                        jobStore,
                        job.OperationId,
                        failureMessage: "Share export dispatch failed.",
                        cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);

                var failedRun = run with
                {
                    Status = ShareExportRunStatus.Failed,
                    CompletedAt = now,
                    LastError = "share-export-dispatch-failed"
                };
                await store.AppendRunAsync(failedRun, context.RequestAborted).ConfigureAwait(false);

                ShareAdminLog.ExportDispatchFailed(logger, failedRun.RunId, exportId, job.OperationId, ex);
                activity?.SetTag(HonuaTelemetry.Tags.ShareRunId, failedRun.RunId);
                activity?.SetTag(HonuaTelemetry.Tags.JobId, job.OperationId);
                activity?.SetStatus(ActivityStatusCode.Error, failedRun.LastError);
                activity?.SetTag(HonuaTelemetry.Tags.Error, true);
                activity?.SetTag(HonuaTelemetry.Tags.ErrorMessage, failedRun.LastError);
                return Unavailable(context, "Share export job could not be dispatched.");
            }

            run = await store.AppendRunAsync(run, context.RequestAborted).ConfigureAwait(false);
            ShareAdminLog.ExportRunTriggered(logger, run.RunId, exportId, run.JobRunId);
            activity?.SetTag(HonuaTelemetry.Tags.ShareRunId, run.RunId);
            activity?.SetTag(HonuaTelemetry.Tags.JobId, run.JobRunId);
            HonuaTelemetry.SetSuccess(activity);
            return Results.Json(
                MapRun(run),
                ShareAdminJsonContext.Default.ShareExportRunResponse,
                statusCode: StatusCodes.Status202Accepted);
        }
        catch (ShareExportStoreUnavailableException ex)
        {
            return Unavailable(context, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShareAdminLog.EndpointFailed(logger, "share.exports.trigger", ex);
            return ServerError(context, "Share export trigger failed.");
        }
    }

    private static async Task<IResult> HandleListRuns(
        HttpContext context,
        string exportId,
        [FromServices] IShareExportStore store,
        [FromServices] ILogger<ShareAdminEndpointsLog> logger,
        [FromQuery] string? cursor = null,
        [FromQuery] int? limit = null)
    {
        if (!TryValidateCursor(cursor, out var cursorError))
        {
            return BadRequest(context, cursorError);
        }

        try
        {
            var definition = await store.GetDefinitionAsync(exportId, context.RequestAborted).ConfigureAwait(false);
            if (definition is null)
            {
                return NotFound(context, "Share export definition was not found.");
            }

            var page = await store.ListRunsAsync(
                    exportId,
                    cursor,
                    ClampLimit(limit),
                    context.RequestAborted)
                .ConfigureAwait(false);

            return Results.Json(
                new ShareExportRunPageResponse
                {
                    Items = page.Items.Select(MapRun).ToArray(),
                    NextCursor = page.NextCursor
                },
                ShareAdminJsonContext.Default.ShareExportRunPageResponse);
        }
        catch (ShareExportStoreUnavailableException ex)
        {
            return Unavailable(context, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShareAdminLog.EndpointFailed(logger, "share.exports.runs.list", ex);
            return ServerError(context, "Share export run listing failed.");
        }
    }

    private static async Task<IResult> HandleGetRun(
        HttpContext context,
        string exportId,
        string runId,
        [FromServices] IShareExportStore store,
        [FromServices] ILogger<ShareAdminEndpointsLog> logger)
    {
        try
        {
            var run = await store.GetRunAsync(exportId, runId, context.RequestAborted).ConfigureAwait(false);
            return run is null
                ? NotFound(context, "Share export run was not found.")
                : Results.Json(MapRun(run), ShareAdminJsonContext.Default.ShareExportRunResponse);
        }
        catch (ShareExportStoreUnavailableException ex)
        {
            return Unavailable(context, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShareAdminLog.EndpointFailed(logger, "share.exports.runs.get", ex);
            return ServerError(context, "Share export run retrieval failed.");
        }
    }

    private static Task<IResult> HandleAggregateTrafficSummary(
        HttpContext context,
        [FromServices] IShareTrafficStore store,
        [FromServices] TimeProvider timeProvider,
        [FromServices] ILogger<ShareAdminEndpointsLog> logger,
        [FromQuery] string? periodStart = null,
        [FromQuery] string? periodEnd = null)
        => HandleTrafficSummaryAsync(context, store, timeProvider, logger, itemRef: null, periodStart, periodEnd);

    private static Task<IResult> HandleItemTrafficSummary(
        HttpContext context,
        string serviceName,
        int layerId,
        [FromServices] IShareTrafficStore store,
        [FromServices] TimeProvider timeProvider,
        [FromServices] ILogger<ShareAdminEndpointsLog> logger,
        [FromQuery] string? resourceId = null,
        [FromQuery] string? periodStart = null,
        [FromQuery] string? periodEnd = null)
        => HandleTrafficSummaryAsync(
            context,
            store,
            timeProvider,
            logger,
            new ShareItemRef { ResourceId = NullIfWhiteSpace(resourceId), ServiceName = serviceName, LayerId = layerId },
            periodStart,
            periodEnd);

    private static Task<IResult> HandleAggregateTrafficSeries(
        HttpContext context,
        [FromServices] IShareTrafficStore store,
        [FromServices] TimeProvider timeProvider,
        [FromServices] ILogger<ShareAdminEndpointsLog> logger,
        [FromQuery] string? periodStart = null,
        [FromQuery] string? periodEnd = null,
        [FromQuery] int? bucketMinutes = null)
        => HandleTrafficSeriesAsync(context, store, timeProvider, logger, itemRef: null, periodStart, periodEnd, bucketMinutes);

    private static Task<IResult> HandleItemTrafficSeries(
        HttpContext context,
        string serviceName,
        int layerId,
        [FromServices] IShareTrafficStore store,
        [FromServices] TimeProvider timeProvider,
        [FromServices] ILogger<ShareAdminEndpointsLog> logger,
        [FromQuery] string? resourceId = null,
        [FromQuery] string? periodStart = null,
        [FromQuery] string? periodEnd = null,
        [FromQuery] int? bucketMinutes = null)
        => HandleTrafficSeriesAsync(
            context,
            store,
            timeProvider,
            logger,
            new ShareItemRef { ResourceId = NullIfWhiteSpace(resourceId), ServiceName = serviceName, LayerId = layerId },
            periodStart,
            periodEnd,
            bucketMinutes);

    private static async Task<IResult> HandleTrafficSummaryAsync(
        HttpContext context,
        IShareTrafficStore store,
        TimeProvider timeProvider,
        ILogger logger,
        ShareItemRef? itemRef,
        string? periodStart,
        string? periodEnd)
    {
        if (!TryBuildTrafficQuery(context, timeProvider, itemRef, periodStart, periodEnd, bucketMinutes: null, out var query, out var problem))
        {
            return problem;
        }

        try
        {
            var summary = await store.GetSummaryAsync(query, context.RequestAborted).ConfigureAwait(false);
            return Results.Json(
                MapSummary(summary),
                ShareAdminJsonContext.Default.ShareTrafficSummaryResponse);
        }
        catch (ShareTrafficStoreUnavailableException ex)
        {
            return Unavailable(context, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShareAdminLog.EndpointFailed(logger, "share.traffic.summary", ex);
            return ServerError(context, "Share traffic summary retrieval failed.");
        }
    }

    private static async Task<IResult> HandleTrafficSeriesAsync(
        HttpContext context,
        IShareTrafficStore store,
        TimeProvider timeProvider,
        ILogger logger,
        ShareItemRef? itemRef,
        string? periodStart,
        string? periodEnd,
        int? bucketMinutes)
    {
        if (!TryBuildTrafficQuery(context, timeProvider, itemRef, periodStart, periodEnd, bucketMinutes, out var query, out var problem))
        {
            return problem;
        }

        try
        {
            var series = await store.GetSeriesAsync(query, context.RequestAborted).ConfigureAwait(false);
            return Results.Json(
                MapSeries(series),
                ShareAdminJsonContext.Default.ShareTrafficSeriesResponse);
        }
        catch (ShareTrafficStoreUnavailableException ex)
        {
            return Unavailable(context, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShareAdminLog.EndpointFailed(logger, "share.traffic.series", ex);
            return ServerError(context, "Share traffic series retrieval failed.");
        }
    }

    private static async Task<IResult> UpdateScheduleStateAsync(
        HttpContext context,
        string exportId,
        ShareExportScheduleState scheduleState,
        IShareExportStore store,
        TimeProvider timeProvider,
        ILogger logger,
        string operation)
    {
        try
        {
            var definition = await store.GetDefinitionAsync(exportId, context.RequestAborted).ConfigureAwait(false);
            if (definition is null)
            {
                return NotFound(context, "Share export definition was not found.");
            }

            var updated = definition with
            {
                ScheduleState = scheduleState,
                UpdatedAt = timeProvider.GetUtcNow()
            };
            var stored = await store.UpdateDefinitionAsync(updated, context.RequestAborted).ConfigureAwait(false);
            return stored is null
                ? NotFound(context, "Share export definition was not found.")
                : Results.Json(MapDefinition(stored), ShareAdminJsonContext.Default.ShareExportDefinitionResponse);
        }
        catch (ShareExportStoreUnavailableException ex)
        {
            return Unavailable(context, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShareAdminLog.EndpointFailed(logger, operation, ex);
            return ServerError(context, "Share export schedule-state update failed.");
        }
    }

    private static bool TryBuildDefinition(
        HttpContext context,
        ShareExportDefinitionRequest request,
        string exportId,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? lastRunAt,
        DateTimeOffset? nextRunAt,
        IShareExportDestinationResolver destinationResolver,
        out ShareExportDefinition definition,
        out IResult problem)
    {
        definition = null!;
        problem = Results.Empty;

        if (string.IsNullOrWhiteSpace(request.ServiceName))
        {
            problem = BadRequest(context, "serviceName is required.");
            return false;
        }

        if (request.LayerId < 0)
        {
            problem = BadRequest(context, "layerId must be zero or greater.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.Format))
        {
            problem = BadRequest(context, "format is required.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.Schedule))
        {
            problem = BadRequest(context, "schedule is required.");
            return false;
        }

        if (!TryParseRequiredEnum(request.DestinationType, out ShareExportDestinationType destinationType, out var destinationError))
        {
            problem = BadRequest(context, destinationError);
            return false;
        }

        if (!TryParseOptionalEnum(request.ScheduleState, out ShareExportScheduleState? scheduleState, out var stateError))
        {
            problem = BadRequest(context, stateError);
            return false;
        }

        if (!TryNormalizeDestinationConfig(request.DestinationConfig, out var config, out var configError))
        {
            problem = BadRequest(context, configError);
            return false;
        }

        definition = new ShareExportDefinition
        {
            ExportId = exportId,
            ResourceId = NullIfWhiteSpace(request.ResourceId),
            ServiceName = request.ServiceName.Trim(),
            LayerId = request.LayerId,
            DisplayName = NullIfWhiteSpace(request.DisplayName),
            DestinationType = destinationType,
            DestinationStatus = destinationResolver.Resolve(destinationType),
            DestinationConfig = config,
            Format = request.Format.Trim(),
            Schedule = request.Schedule.Trim(),
            ScheduleState = scheduleState ?? ShareExportScheduleState.Active,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            LastRunAt = lastRunAt,
            NextRunAt = nextRunAt
        };

        return true;
    }

    private static ShareExportRun BuildRun(
        ShareExportDefinition definition,
        DateTimeOffset now,
        ShareExportRunStatus status,
        string? jobRunId,
        string? lastError)
        => new()
        {
            RunId = Guid.NewGuid().ToString("N"),
            ExportId = definition.ExportId,
            TriggerKind = ShareExportTriggerKind.Manual,
            Status = status,
            JobRunId = jobRunId,
            TriggeredAt = now,
            StartedAt = null,
            CompletedAt = status is ShareExportRunStatus.Failed or ShareExportRunStatus.Cancelled or ShareExportRunStatus.Succeeded
                ? now
                : null,
            TargetSummary = BuildTargetSummary(definition),
            ResultArtifacts = Array.Empty<string>(),
            LastError = lastError
        };

    private static ExecutionJobRecord BuildExecutionJob(
        HttpContext context,
        ShareExportDefinition definition,
        ShareExportRun run,
        DateTimeOffset now)
    {
        var resourceRefs = BuildResourceRefs(definition);
        return new ExecutionJobRecord
        {
            OperationId = run.JobRunId!,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Queued",
            Audit = new OperationAuditInfo
            {
                RequestedBy = context.User.Identity?.Name,
                CorrelationId = context.TraceIdentifier,
                IdempotencyKey = run.RunId
            },
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.ShareExport,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = LocalBatchComputeBackend.BackendId,
                WorkloadName = $"share-export:{definition.ExportId}",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ExecutionJobParameterKeys.DefinitionId] = definition.ExportId,
                    [ExecutionJobParameterKeys.ResourceRefs] = string.Join(
                        ExecutionJobParameterKeys.MetadataListSeparator,
                        resourceRefs),
                    [ExecutionJobParameterKeys.ShareExportId] = definition.ExportId,
                    [ExecutionJobParameterKeys.ShareRunId] = run.RunId,
                    [ExecutionJobParameterKeys.ShareDestinationType] = definition.DestinationType.ToString(),
                    [ExecutionJobParameterKeys.ShareFormat] = definition.Format
                }
            }
        };
    }

    private static string CreateJobRunId() => $"share-export-{Guid.NewGuid():N}";

    private static string[] BuildResourceRefs(ShareExportDefinition definition)
    {
        var refs = new List<string>(3)
        {
            $"service/{definition.ServiceName}",
            $"layer/{definition.ServiceName}/{definition.LayerId.ToString(CultureInfo.InvariantCulture)}"
        };
        if (!string.IsNullOrWhiteSpace(definition.ResourceId))
        {
            refs.Add($"share/{definition.ResourceId}");
        }

        return refs.ToArray();
    }

    private static string BuildTargetSummary(ShareExportDefinition definition)
        => definition.DestinationType switch
        {
            ShareExportDestinationType.S3 => TryGetConfig(definition, "bucket", out var bucket)
                ? $"S3 bucket {bucket}"
                : "S3 destination",
            ShareExportDestinationType.Sftp => TryGetConfig(definition, "host", out var host)
                ? $"SFTP host {host}"
                : "SFTP destination",
            ShareExportDestinationType.Webhook => TryGetConfig(definition, "url", out var url)
                ? $"Webhook {url}"
                : "Webhook destination",
            ShareExportDestinationType.AuditSnapshot => "Audit snapshot export",
            _ => definition.DestinationType.ToString()
        };

    private static bool TryGetConfig(ShareExportDefinition definition, string key, out string value)
    {
        foreach (var pair in definition.DestinationConfig)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool TryBuildTrafficQuery(
        HttpContext context,
        TimeProvider timeProvider,
        ShareItemRef? itemRef,
        string? periodStart,
        string? periodEnd,
        int? bucketMinutes,
        out ShareTrafficQuery query,
        out IResult problem)
    {
        query = null!;
        problem = Results.Empty;

        var end = string.IsNullOrWhiteSpace(periodEnd)
            ? timeProvider.GetUtcNow()
            : default;
        if (!string.IsNullOrWhiteSpace(periodEnd)
            && !DateTimeOffset.TryParse(periodEnd, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out end))
        {
            problem = BadRequest(context, "periodEnd must be an ISO-8601 date/time.");
            return false;
        }

        var start = string.IsNullOrWhiteSpace(periodStart)
            ? end.AddHours(-DefaultTrafficHours)
            : default;
        if (!string.IsNullOrWhiteSpace(periodStart)
            && !DateTimeOffset.TryParse(periodStart, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out start))
        {
            problem = BadRequest(context, "periodStart must be an ISO-8601 date/time.");
            return false;
        }

        if (start >= end)
        {
            problem = BadRequest(context, "periodStart must be before periodEnd.");
            return false;
        }

        var effectiveBucketMinutes = bucketMinutes ?? DefaultBucketMinutes;
        if (effectiveBucketMinutes <= 0)
        {
            problem = BadRequest(context, "bucketMinutes must be greater than zero.");
            return false;
        }

        var bucketDuration = TimeSpan.FromMinutes(effectiveBucketMinutes);

        // The series stores emit one bucket per step while cursor < periodEnd, i.e. a ceiling
        // count of ceil(range / bucketDuration). Guard against that exact emitted count (not the
        // floor) so a range of MaxTrafficBuckets buckets plus a partial tick cannot slip through
        // as one bucket over the documented cap. Ceiling division avoids the overflow a
        // MaxTrafficBuckets * bucketTicks multiplication would risk for very large bucket widths.
        var rangeTicks = (end - start).Ticks;
        var bucketTicks = bucketDuration.Ticks;
        var bucketCount = rangeTicks / bucketTicks;
        if (rangeTicks % bucketTicks != 0)
        {
            bucketCount++;
        }

        if (bucketCount > MaxTrafficBuckets)
        {
            problem = BadRequest(context, $"Traffic series cannot exceed {MaxTrafficBuckets.ToString(CultureInfo.InvariantCulture)} buckets.");
            return false;
        }

        query = new ShareTrafficQuery
        {
            ItemRef = itemRef,
            PeriodStart = start.ToUniversalTime(),
            PeriodEnd = end.ToUniversalTime(),
            BucketDuration = bucketDuration
        };
        return true;
    }

    private static ShareExportDefinitionResponse MapDefinition(ShareExportDefinition definition)
        => new()
        {
            ExportId = definition.ExportId,
            ResourceId = definition.ResourceId,
            ServiceName = definition.ServiceName,
            LayerId = definition.LayerId,
            DisplayName = definition.DisplayName,
            DestinationType = definition.DestinationType.ToString(),
            DestinationStatus = definition.DestinationStatus.ToString(),
            DestinationConfig = SanitizeConfigForRead(definition.DestinationConfig),
            Format = definition.Format,
            Schedule = definition.Schedule,
            ScheduleState = definition.ScheduleState.ToString(),
            CreatedAt = definition.CreatedAt,
            UpdatedAt = definition.UpdatedAt,
            LastRunAt = definition.LastRunAt,
            NextRunAt = definition.NextRunAt
        };

    private static ShareExportRunResponse MapRun(ShareExportRun run)
        => new()
        {
            RunId = run.RunId,
            ExportId = run.ExportId,
            TriggerKind = run.TriggerKind.ToString(),
            Status = run.Status.ToString(),
            JobRunId = run.JobRunId,
            TriggeredAt = run.TriggeredAt,
            StartedAt = run.StartedAt,
            CompletedAt = run.CompletedAt,
            TargetSummary = run.TargetSummary,
            ResultArtifacts = run.ResultArtifacts.ToArray(),
            LastError = run.LastError
        };

    private static ShareTrafficSummaryResponse MapSummary(ShareTrafficSummary summary)
        => new()
        {
            ItemRef = MapItemRef(summary.ItemRef),
            PeriodStart = summary.PeriodStart,
            PeriodEnd = summary.PeriodEnd,
            ByInteractionType = MapCounts(summary.ByInteractionType),
            TotalRequests = summary.TotalRequests
        };

    private static ShareTrafficSeriesResponse MapSeries(ShareTrafficSeries series)
        => new()
        {
            ItemRef = MapItemRef(series.ItemRef),
            PeriodStart = series.PeriodStart,
            PeriodEnd = series.PeriodEnd,
            BucketDuration = series.BucketDuration,
            Buckets = series.Buckets.Select(bucket => new ShareTrafficBucketResponse
            {
                BucketStart = bucket.BucketStart,
                ByInteractionType = MapCounts(bucket.ByInteractionType),
                Total = bucket.Total
            }).ToArray()
        };

    private static ShareItemRefResponse? MapItemRef(ShareItemRef? itemRef)
        => itemRef is null
            ? null
            : new ShareItemRefResponse
            {
                ResourceId = itemRef.ResourceId,
                ServiceName = itemRef.ServiceName,
                LayerId = itemRef.LayerId
            };

    private static ShareTrafficCountsResponse MapCounts(ShareTrafficCounts counts)
        => new()
        {
            Public = counts.Public,
            PublicLink = counts.PublicLink,
            Embed = counts.Embed,
            OpenData = counts.OpenData,
            Dcat = counts.Dcat,
            Stac = counts.Stac,
            Export = counts.Export
        };

    private static bool TryNormalizeDestinationConfig(
        IReadOnlyDictionary<string, string>? input,
        out Dictionary<string, string> config,
        out string error)
    {
        config = new Dictionary<string, string>(StringComparer.Ordinal);
        error = string.Empty;

        if (input is null)
        {
            return true;
        }

        foreach (var pair in input)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                error = "destinationConfig keys cannot be empty.";
                return false;
            }

            var key = pair.Key.Trim();
            var value = pair.Value?.Trim();
            if (value is null)
            {
                error = $"destinationConfig.{key} cannot be null.";
                return false;
            }

            if (IsSecretMaterialKey(key) && !IsSecretReferenceKey(key))
            {
                error = $"destinationConfig.{key} appears to contain raw secret material; use a secretRef or credentialRef instead.";
                return false;
            }

            config[key] = value;
        }

        return true;
    }

    private static Dictionary<string, string> SanitizeConfigForRead(IReadOnlyDictionary<string, string> config)
    {
        var sanitized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in config)
        {
            sanitized[pair.Key] = IsSecretMaterialKey(pair.Key) && !IsSecretReferenceKey(pair.Key)
                ? "redacted"
                : pair.Value;
        }

        return sanitized;
    }

    private static bool IsSecretReferenceKey(string key)
    {
        var normalized = NormalizeConfigKey(key);
        return normalized.EndsWith("ref", StringComparison.Ordinal)
            || normalized.EndsWith("reference", StringComparison.Ordinal);
    }

    private static bool IsSecretMaterialKey(string key)
    {
        var normalized = NormalizeConfigKey(key);
        return normalized.Contains("password", StringComparison.Ordinal)
            || normalized.Contains("secret", StringComparison.Ordinal)
            || normalized.Contains("token", StringComparison.Ordinal)
            || normalized.Contains("privatekey", StringComparison.Ordinal)
            || normalized.Contains("accesskey", StringComparison.Ordinal)
            || normalized.Contains("apikey", StringComparison.Ordinal);
    }

    private static string NormalizeConfigKey(string key)
    {
        var normalized = new char[key.Length];
        var length = 0;
        foreach (var ch in key)
        {
            if (ch is '-' or '_' or '.' or ' ')
            {
                continue;
            }

            normalized[length] = char.ToLowerInvariant(ch);
            length++;
        }

        return new string(normalized, 0, length);
    }

    private static bool TryParseRequiredEnum<TEnum>(string? value, out TEnum parsed, out string error)
        where TEnum : struct, Enum
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = $"{typeof(TEnum).Name} value is required.";
            return false;
        }

        return TryParseEnum(value, out parsed, out error);
    }

    private static bool TryParseOptionalEnum<TEnum>(string? value, out TEnum? parsed, out string error)
        where TEnum : struct, Enum
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = string.Empty;
            return true;
        }

        if (!TryParseEnum(value, out TEnum enumValue, out error))
        {
            return false;
        }

        parsed = enumValue;
        return true;
    }

    private static bool TryParseEnum<TEnum>(string value, out TEnum parsed, out string error)
        where TEnum : struct, Enum
    {
        parsed = default;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            || !Enum.TryParse(value.Trim(), ignoreCase: true, out parsed)
            || !Enum.IsDefined(parsed))
        {
            error = $"{value} is not a valid {typeof(TEnum).Name}.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryValidateCursor(string? cursor, out string error)
    {
        if (!string.IsNullOrWhiteSpace(cursor) && !ShareCursor.TryDecode(cursor, out _, out _))
        {
            error = "cursor is invalid.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static int ClampLimit(int? limit)
        => Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IResult BadRequest(HttpContext context, string detail)
        => ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, "Bad Request", detail);

    private static IResult NotFound(HttpContext context, string detail)
        => ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, "Not Found", detail);

    private static IResult Conflict(HttpContext context, string detail)
        => ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status409Conflict, "Conflict", detail);

    private static IResult Unavailable(HttpContext context, string detail)
        => ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status503ServiceUnavailable, "Service Unavailable", detail);

    private static IResult ServerError(HttpContext context, string detail)
        => ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status500InternalServerError, "Internal Server Error", detail);

    private static IResult UnprocessableDestination(HttpContext context, ShareExportDestinationStatus status)
        => status == ShareExportDestinationStatus.NotConfigured
            ? ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "share-export-destination-not-configured",
                "The Share export destination is known but is not configured for this environment.")
            : ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "share-export-destination-unsupported",
                "The Share export destination has no registered worker in this build.");
}

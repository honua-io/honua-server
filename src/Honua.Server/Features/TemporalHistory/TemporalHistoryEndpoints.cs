// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.TemporalHistory.Abstractions;
using Honua.Core.Features.TemporalHistory.Domain;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.TemporalHistory;

/// <summary>
/// Minimal-API endpoints for the temporal data-history surface: capability discovery, as-of reads,
/// checkpoints, diffs, per-feature timelines, rollback planning, and job-backed rollback execution.
/// Each operation enforces its own permission through <see cref="TemporalAccessResolver"/>.
/// </summary>
internal static partial class TemporalHistoryEndpoints
{
    private const string BasePath = "/api/v{version:apiVersion}/layers/{layerId:int}/history";

    /// <summary>
    /// Maps the temporal-history endpoints.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The endpoint route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapTemporalHistoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(BasePath)
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Temporal History")
            .AllowAnonymous();

        _ = group.MapGet("/capabilities", HandleCapabilitiesAsync)
            .WithName("GetTemporalCapabilities")
            .WithSummary("Discover the temporal-history capabilities of a layer.");

        _ = group.MapGet("/checkpoints", HandleCheckpointsAsync)
            .WithName("ListTemporalCheckpoints")
            .WithSummary("List named/derived temporal checkpoints for a layer.");

        _ = group.MapGet("/items", HandleAsOfAsync)
            .WithName("QueryTemporalAsOf")
            .WithSummary("Query a layer as of a timestamp or checkpoint.");

        _ = group.MapGet("/diff", HandleDiffAsync)
            .WithName("GetTemporalDiff")
            .WithSummary("Diff a layer between two checkpoints.");

        _ = group.MapGet("/items/{featureId}/timeline", HandleTimelineAsync)
            .WithName("GetTemporalTimeline")
            .WithSummary("Read a single feature's revision timeline.");

        _ = group.MapGet("/rollback-plan", HandleRollbackPlanAsync)
            .WithName("GetTemporalRollbackPlan")
            .WithSummary("Generate a rollback plan to a target checkpoint.");

        _ = group.MapPost("/rollback", HandleRollbackAsync)
            .WithName("ExecuteTemporalRollback")
            .WithSummary("Execute an approved rollback through the job runner.");

        return endpoints;
    }

    private static async Task<IResult> HandleCapabilitiesAsync(
        int layerId,
        HttpContext context,
        [FromServices] ILayerCatalog catalog,
        [FromServices] ITemporalHistorySource source)
    {
        var resolution = await ResolveLayerAsync(context, catalog, layerId, TemporalOperation.CurrentRead).ConfigureAwait(false);
        if (resolution.Error is not null)
        {
            return resolution.Error;
        }

        try
        {
            var capabilities = await source.GetCapabilitiesAsync(resolution.Layer!, context.RequestAborted).ConfigureAwait(false);
            return capabilities is null
                ? NotTemporal(context)
                : Results.Json(capabilities, TemporalHistoryApiJsonContext.Default.TemporalSourceCapabilityInfo);
        }
        catch (TemporalHistoryException ex)
        {
            return BadRequest(context, ex.Message);
        }
        catch (Exception ex)
        {
            return ServerError(context, "temporal-history.capabilities", ex);
        }
    }

    private static async Task<IResult> HandleCheckpointsAsync(
        int layerId,
        int? limit,
        HttpContext context,
        [FromServices] ILayerCatalog catalog,
        [FromServices] ITemporalHistorySource source)
    {
        var resolution = await ResolveLayerAsync(context, catalog, layerId, TemporalOperation.HistoryRead).ConfigureAwait(false);
        if (resolution.Error is not null)
        {
            return resolution.Error;
        }

        try
        {
            var checkpoints = await source.ListCheckpointsAsync(
                resolution.Layer!, limit ?? TemporalPageRequest.DefaultLimit, context.RequestAborted).ConfigureAwait(false);
            return Results.Json(
                new TemporalCheckpointListResponse { LayerId = layerId, Checkpoints = checkpoints },
                TemporalHistoryApiJsonContext.Default.TemporalCheckpointListResponse);
        }
        catch (TemporalHistoryException ex)
        {
            return BadRequest(context, ex.Message);
        }
        catch (Exception ex)
        {
            return ServerError(context, "temporal-history.checkpoints", ex);
        }
    }

    private static async Task<IResult> HandleAsOfAsync(
        int layerId,
        string? at,
        int? limit,
        string? cursor,
        HttpContext context,
        [FromServices] ILayerCatalog catalog,
        [FromServices] ITemporalHistorySource source)
    {
        var resolution = await ResolveLayerAsync(context, catalog, layerId, TemporalOperation.HistoryRead).ConfigureAwait(false);
        if (resolution.Error is not null)
        {
            return resolution.Error;
        }

        if (!TryParseCursor(at, out var atCursor))
        {
            return BadRequest(context, "A valid 'at' cursor or timestamp is required.");
        }

        try
        {
            var capabilities = await source.GetCapabilitiesAsync(resolution.Layer!, context.RequestAborted).ConfigureAwait(false);
            if (capabilities is null)
            {
                return NotTemporal(context);
            }

            if (!capabilities.SupportsAsOf)
            {
                return BadRequest(context, "As-of queries are not available for this layer.");
            }

            var snapshot = await source.QueryAsOfAsync(
                resolution.Layer!, atCursor, BuildPage(limit, cursor), context.RequestAborted).ConfigureAwait(false);
            return Results.Json(snapshot, TemporalHistoryApiJsonContext.Default.TemporalSnapshot);
        }
        catch (TemporalHistoryException ex)
        {
            return BadRequest(context, ex.Message);
        }
        catch (Exception ex)
        {
            return ServerError(context, "temporal-history.asof", ex);
        }
    }

    private static async Task<IResult> HandleDiffAsync(
        int layerId,
        string? from,
        string? to,
        int? limit,
        string? cursor,
        HttpContext context,
        [FromServices] ILayerCatalog catalog,
        [FromServices] ITemporalHistorySource source)
    {
        var resolution = await ResolveLayerAsync(context, catalog, layerId, TemporalOperation.DiffRead).ConfigureAwait(false);
        if (resolution.Error is not null)
        {
            return resolution.Error;
        }

        if (!TryParseCursor(from, out var fromCursor) || !TryParseCursor(to, out var toCursor))
        {
            return BadRequest(context, "Valid 'from' and 'to' cursors are required.");
        }

        try
        {
            var capabilities = await source.GetCapabilitiesAsync(resolution.Layer!, context.RequestAborted).ConfigureAwait(false);
            if (capabilities is null)
            {
                return NotTemporal(context);
            }

            if (!capabilities.SupportsDiff)
            {
                return BadRequest(context, "Diffs are not available for this layer.");
            }

            var diff = await source.DiffAsync(
                resolution.Layer!, fromCursor, toCursor, BuildPage(limit, cursor), context.RequestAborted).ConfigureAwait(false);
            return Results.Json(diff, TemporalHistoryApiJsonContext.Default.TemporalDiff);
        }
        catch (TemporalHistoryException ex)
        {
            return BadRequest(context, ex.Message);
        }
        catch (Exception ex)
        {
            return ServerError(context, "temporal-history.diff", ex);
        }
    }

    private static async Task<IResult> HandleTimelineAsync(
        int layerId,
        string featureId,
        int? limit,
        string? cursor,
        HttpContext context,
        [FromServices] ILayerCatalog catalog,
        [FromServices] ITemporalHistorySource source)
    {
        var resolution = await ResolveLayerAsync(context, catalog, layerId, TemporalOperation.TimelineRead).ConfigureAwait(false);
        if (resolution.Error is not null)
        {
            return resolution.Error;
        }

        try
        {
            var timeline = await source.GetTimelineAsync(
                resolution.Layer!, featureId, BuildPage(limit, cursor), context.RequestAborted).ConfigureAwait(false);
            return Results.Json(timeline, TemporalHistoryApiJsonContext.Default.TemporalTimeline);
        }
        catch (TemporalHistoryException ex)
        {
            return BadRequest(context, ex.Message);
        }
        catch (Exception ex)
        {
            return ServerError(context, "temporal-history.timeline", ex);
        }
    }

    private static async Task<IResult> HandleRollbackPlanAsync(
        int layerId,
        string? to,
        HttpContext context,
        [FromServices] ILayerCatalog catalog,
        [FromServices] ITemporalHistorySource source)
    {
        var resolution = await ResolveLayerAsync(context, catalog, layerId, TemporalOperation.RollbackPlan).ConfigureAwait(false);
        if (resolution.Error is not null)
        {
            return resolution.Error;
        }

        if (!TryParseCursor(to, out var toCursor))
        {
            return BadRequest(context, "A valid 'to' cursor or timestamp is required.");
        }

        try
        {
            var plan = await source.PlanRollbackAsync(resolution.Layer!, toCursor, context.RequestAborted).ConfigureAwait(false);
            return Results.Json(plan, TemporalHistoryApiJsonContext.Default.TemporalRollbackPlan);
        }
        catch (TemporalHistoryException ex)
        {
            return BadRequest(context, ex.Message);
        }
        catch (Exception ex)
        {
            return ServerError(context, "temporal-history.rollback-plan", ex);
        }
    }

    private static async Task<IResult> HandleRollbackAsync(
        int layerId,
        TemporalRollbackRequestBody request,
        HttpContext context,
        [FromServices] ILayerCatalog catalog,
        [FromServices] ITemporalHistorySource source)
    {
        var resolution = await ResolveLayerAsync(context, catalog, layerId, TemporalOperation.RollbackExecute).ConfigureAwait(false);
        if (resolution.Error is not null)
        {
            return resolution.Error;
        }

        if (request is null || !TryParseCursor(request.To, out var toCursor))
        {
            return BadRequest(context, "A valid 'to' cursor or timestamp is required.");
        }

        var jobStore = context.RequestServices.GetService<IExecutionJobStore>();
        var jobQueue = context.RequestServices.GetService<IJobQueue>();
        if (jobStore is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context, StatusCodes.Status503ServiceUnavailable, "Rollback execution is not available in this deployment.");
        }

        try
        {
            var capabilities = await source.GetCapabilitiesAsync(resolution.Layer!, context.RequestAborted).ConfigureAwait(false);
            if (capabilities is null)
            {
                return NotTemporal(context);
            }

            if (!capabilities.SupportsRollbackExecution)
            {
                return BadRequest(context, "Rollback execution is not available for this layer.");
            }

            var plan = await source.PlanRollbackAsync(resolution.Layer!, toCursor, context.RequestAborted).ConfigureAwait(false);
            if (plan.Mode == TemporalRollbackMode.Blocked)
            {
                return ProblemDetailsHelpers.CreateAdminProblem(
                    context, StatusCodes.Status409Conflict, "Rollback is blocked for the requested target.");
            }

            if (plan.Mode is TemporalRollbackMode.ScriptRequired or TemporalRollbackMode.Manual)
            {
                return ProblemDetailsHelpers.CreateAdminProblem(
                    context,
                    StatusCodes.Status409Conflict,
                    "Rollback requires operator-managed execution for the requested target.");
            }

            if (!request.Approved)
            {
                return BadRequest(context, "Rollback requires explicit approval.");
            }

            var jobId = $"tmprb_{Guid.NewGuid():N}";
            var now = DateTimeOffset.UtcNow;
            var record = new ExecutionJobRecord
            {
                OperationId = jobId,
                Status = ExecutionJobStatus.Queued,
                Priority = OperationPriority.Normal,
                CreatedAt = now,
                UpdatedAt = now,
                CurrentPhase = "Queued",
                Audit = new OperationAuditInfo
                {
                    RequestedBy = context.User.Identity?.Name,
                    Reason = request.Reason,
                    CorrelationId = context.TraceIdentifier
                },
                Spec = new ExecutionJobSpec
                {
                    TargetKind = BatchComputeTargetKind.KubernetesJob,
                    Backend = LocalBatchComputeBackend.BackendId,
                    Kind = ExecutionJobKind.TemporalRollback,
                    WorkloadName = $"temporal-rollback:layer-{layerId}",
                    Parameters = new Dictionary<string, string>
                    {
                        [ExecutionJobParameterKeys.TemporalRollbackLayerId] = layerId.ToString(CultureInfo.InvariantCulture),
                        [ExecutionJobParameterKeys.TemporalRollbackToCursor] = toCursor.ToString(),
                        [ExecutionJobParameterKeys.TemporalRollbackReason] = request.Reason ?? string.Empty
                    }
                }
            };

            var created = await jobStore.TryCreateAsync(record, cancellationToken: context.RequestAborted).ConfigureAwait(false);
            if (created && jobQueue is not null)
            {
                await jobQueue.EnqueueAsync(jobId, OperationPriority.Normal, context.RequestAborted).ConfigureAwait(false);
            }

            return Results.Json(
                new TemporalRollbackAcceptedResponse
                {
                    LayerId = layerId,
                    JobId = jobId,
                    To = toCursor.ToString(),
                    Status = ExecutionJobStatus.Queued.ToString(),
                    Mode = plan.Mode.ToString(),
                    AffectedCount = plan.AffectedCount
                },
                TemporalHistoryApiJsonContext.Default.TemporalRollbackAcceptedResponse,
                statusCode: StatusCodes.Status202Accepted);
        }
        catch (TemporalHistoryException ex)
        {
            return BadRequest(context, ex.Message);
        }
        catch (Exception ex)
        {
            return ServerError(context, "temporal-history.rollback", ex);
        }
    }

    private static async Task<LayerResolution> ResolveLayerAsync(
        HttpContext context,
        ILayerCatalog catalog,
        int layerId,
        TemporalOperation operation)
    {
        var layer = await catalog.GetLayerAsync(layerId, context.RequestAborted).ConfigureAwait(false);
        if (layer is null)
        {
            return new LayerResolution(
                null,
                ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, "The requested layer was not found."));
        }

        if (layer.Metadata?.TemporalSource is null)
        {
            return new LayerResolution(null, NotTemporal(context));
        }

        var denied = TemporalAccessResolver.Require(context, layer, operation);
        return denied is not null
            ? new LayerResolution(null, denied)
            : new LayerResolution(layer, null);
    }

    private static bool TryParseCursor(string? raw, out TemporalCursor cursor)
    {
        if (TemporalCursor.TryParse(raw, out cursor))
        {
            return true;
        }

        if (DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
        {
            cursor = TemporalCursor.AtTimestamp(timestamp);
            return true;
        }

        cursor = default!;
        return false;
    }

    private static TemporalPageRequest BuildPage(int? limit, string? cursor)
        => new TemporalPageRequest
        {
            Limit = limit ?? TemporalPageRequest.DefaultLimit,
            Cursor = string.IsNullOrWhiteSpace(cursor) ? null : cursor
        }.Normalize();

    private static IResult NotTemporal(HttpContext context)
        => ProblemDetailsHelpers.CreateAdminProblem(
            context, StatusCodes.Status404NotFound, "Temporal history is not available for this layer.");

    private static IResult BadRequest(HttpContext context, string detail)
        => ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, detail);

    private static IResult ServerError(HttpContext context, string operation, Exception exception)
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Honua.TemporalHistory");
        TemporalHistoryEndpointsLog.EndpointFailed(logger, operation, exception);
        return ProblemDetailsHelpers.CreateAdminProblem(
            context, StatusCodes.Status500InternalServerError, "An internal error occurred while processing the temporal-history request.");
    }

    private readonly record struct LayerResolution(LayerDefinition? Layer, IResult? Error);
}

/// <summary>
/// Source-generated structured logging for temporal-history endpoint failures.
/// </summary>
internal static partial class TemporalHistoryEndpointsLog
{
    [LoggerMessage(EventId = 7110, Level = LogLevel.Error, Message = "Temporal history endpoint '{Operation}' failed.")]
    public static partial void EndpointFailed(ILogger logger, string operation, Exception exception);
}

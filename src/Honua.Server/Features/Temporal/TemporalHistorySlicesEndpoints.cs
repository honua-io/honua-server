// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Temporal.Abstractions;
using Honua.Core.Features.Temporal.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Temporal;

/// <summary>
/// Admin endpoint group for slices 2-5 of the temporal data history API (honua-server#1166): diff,
/// feature timeline, attribution (surfaced in diff/timeline), and governed rollback planning/execution.
/// These are thin adapters — they parse, authorize, delegate to <see cref="ITemporalHistoryService"/>, and
/// format results — and never reimplement query/edit/job logic.
/// </summary>
/// <remarks>
/// Each surface enforces a distinct permission: diff reads use the diff-read policy, timeline reads reuse
/// the history-read policy, rollback planning uses the history-read policy (read-only analysis), and
/// rollback execution uses the dedicated rollback-execution policy.
/// </remarks>
internal static partial class TemporalHistorySlicesEndpoints
{
    /// <summary>Log category for temporal history slice 2-5 endpoints.</summary>
    internal sealed class TemporalHistorySlicesEndpointsLog;

    /// <summary>Maps the slice 2-5 temporal history admin endpoints.</summary>
    public static IEndpointRouteBuilder MapTemporalHistorySliceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        const string basePath = "/api/v{version:apiVersion}/temporal/services/{serviceId}/layers/{layerId:int}";

        // Diff: distinct diff-read permission (slice 2).
        var diffGroup = endpoints.MapGroup(basePath)
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Temporal")
            .RequireTemporalDiffRead();

        _ = diffGroup.MapGet("/diff", HandleDiffAsync)
            .WithName("GetTemporalDiff")
            .WithSummary("Diff a layer between two checkpoints with classified, pageable feature changes (slice 2 of #1166).");

        // Timeline + rollback planning: history-read permission (read-only analysis, slices 3 and 5).
        var readGroup = endpoints.MapGroup(basePath)
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Temporal")
            .RequireTemporalHistoryRead();

        _ = readGroup.MapGet("/features/{featureId:long}/timeline", HandleTimelineAsync)
            .WithName("GetTemporalFeatureTimeline")
            .WithSummary("Read a feature's revision timeline with attribution, subject to masking (slice 3 of #1166).");

        _ = readGroup.MapPost("/rollback/plan", HandleRollbackPlanAsync)
            .WithName("PlanTemporalRollback")
            .WithSummary("Plan a governed rollback to a target checkpoint (slice 5 of #1166).");

        // Rollback execution: distinct rollback-execution permission (slice 5).
        var rollbackGroup = endpoints.MapGroup(basePath)
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Temporal")
            .RequireTemporalRollbackExecute();

        _ = rollbackGroup.MapPost("/rollback", HandleRollbackExecuteAsync)
            .WithName("ExecuteTemporalRollback")
            .WithSummary("Execute an approved rollback as a forward corrective job through the job runner (slice 5 of #1166).");

        return endpoints;
    }

    private static async Task<IResult> HandleDiffAsync(
        string serviceId,
        int layerId,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromServices] ITemporalHistoryService service,
        HttpContext context)
    {
        try
        {
            if (!TryParseCheckpoint(from, required: true, context, "from", out var fromCheckpoint, out var badFrom))
            {
                return badFrom;
            }

            TemporalCheckpoint? toCheckpoint = null;
            if (!string.IsNullOrWhiteSpace(to))
            {
                if (!TryParseCheckpoint(to, required: true, context, "to", out var parsedTo, out var badTo))
                {
                    return badTo;
                }

                toCheckpoint = parsedTo;
            }

            var limit = ReadIntQuery(context, "limit");
            var cursor = context.Request.Query["cursor"].FirstOrDefault();

            var request = new TemporalDiffRequest(fromCheckpoint!, toCheckpoint, limit, cursor);
            var result = await service.DiffAsync(serviceId, layerId, request, context.RequestAborted).ConfigureAwait(false);
            return Results.Json(ToResponse(result), TemporalHistorySlicesJsonContext.Default.TemporalDiffResponse);
        }
        catch (Exception ex) when (TemporalProblemMapping.TryMap(ex, context, out var problem))
        {
            return problem;
        }
        catch (Exception ex)
        {
            LogEndpointFailed(context, "temporal.diff", ex);
            return CreateGenericProblem(context);
        }
    }

    private static async Task<IResult> HandleTimelineAsync(
        string serviceId,
        int layerId,
        long featureId,
        [FromServices] ITemporalHistoryService service,
        HttpContext context)
    {
        try
        {
            var limit = ReadIntQuery(context, "limit");
            var cursor = context.Request.Query["cursor"].FirstOrDefault();

            var request = new TemporalTimelineRequest(featureId, limit, cursor);
            var result = await service.GetTimelineAsync(serviceId, layerId, request, context.RequestAborted).ConfigureAwait(false);
            return Results.Json(ToResponse(result), TemporalHistorySlicesJsonContext.Default.TemporalTimelineResponse);
        }
        catch (Exception ex) when (TemporalProblemMapping.TryMap(ex, context, out var problem))
        {
            return problem;
        }
        catch (Exception ex)
        {
            LogEndpointFailed(context, "temporal.timeline", ex);
            return CreateGenericProblem(context);
        }
    }

    private static async Task<IResult> HandleRollbackPlanAsync(
        string serviceId,
        int layerId,
        [FromBody] TemporalRollbackPlanRequestBody? body,
        [FromServices] ITemporalHistoryService service,
        HttpContext context)
    {
        try
        {
            if (!TryBuildCheckpointFromBody(body?.Checkpoint, context, out var checkpoint, out var bad))
            {
                return bad;
            }

            var request = new TemporalRollbackPlanRequest(checkpoint!);
            var plan = await service.PlanRollbackAsync(serviceId, layerId, request, context.RequestAborted).ConfigureAwait(false);
            return Results.Json(ToResponse(plan), TemporalHistorySlicesJsonContext.Default.TemporalRollbackPlanResponse);
        }
        catch (Exception ex) when (TemporalProblemMapping.TryMap(ex, context, out var problem))
        {
            return problem;
        }
        catch (Exception ex)
        {
            LogEndpointFailed(context, "temporal.rollback.plan", ex);
            return CreateGenericProblem(context);
        }
    }

    private static async Task<IResult> HandleRollbackExecuteAsync(
        string serviceId,
        int layerId,
        [FromBody] TemporalRollbackExecuteRequestBody? body,
        [FromServices] ITemporalHistoryService service,
        HttpContext context)
    {
        try
        {
            if (!TryBuildCheckpointFromBody(body?.Checkpoint, context, out var checkpoint, out var bad))
            {
                return bad;
            }

            var request = new TemporalRollbackExecuteRequest(
                TargetCheckpoint: checkpoint!,
                Approved: body?.Approved ?? false,
                Reason: body?.Reason);

            var handle = await service.ExecuteRollbackAsync(serviceId, layerId, request, context.RequestAborted).ConfigureAwait(false);
            return Results.Json(ToResponse(handle), TemporalHistorySlicesJsonContext.Default.TemporalRollbackJobResponse, statusCode: StatusCodes.Status202Accepted);
        }
        catch (Exception ex) when (TemporalProblemMapping.TryMap(ex, context, out var problem))
        {
            return problem;
        }
        catch (Exception ex)
        {
            LogEndpointFailed(context, "temporal.rollback", ex);
            return CreateGenericProblem(context);
        }
    }

    private static bool TryParseCheckpoint(
        string? raw,
        bool required,
        HttpContext context,
        string parameterName,
        out TemporalCheckpoint? checkpoint,
        out IResult problem)
    {
        checkpoint = null;
        problem = Results.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            if (!required)
            {
                return true;
            }

            problem = ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                $"Query parameter '{parameterName}' is required.");
            return false;
        }

        // Checkpoint syntax: "<kind>:<value>" (for example "timestamp:2026-05-01T00:00:00Z",
        // "job:abc", "named:release-12"). A bare integer is shorthand for a generation checkpoint.
        var separatorIndex = raw.IndexOf(':');
        if (separatorIndex < 0)
        {
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var generation))
            {
                checkpoint = TemporalCheckpoint.ForGeneration(generation);
                return true;
            }

            problem = ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                $"Query parameter '{parameterName}' must be a generation number or '<kind>:<value>'.");
            return false;
        }

        var kindToken = raw[..separatorIndex];
        var value = raw[(separatorIndex + 1)..];
        if (!TryParseCheckpointKind(kindToken, out var kind))
        {
            problem = ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                $"Query parameter '{parameterName}' has an unrecognized checkpoint kind '{kindToken}'.");
            return false;
        }

        checkpoint = kind == TemporalCheckpointKind.Generation
            && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var gen)
                ? TemporalCheckpoint.ForGeneration(gen)
                : new TemporalCheckpoint(kind, value);
        return true;
    }

    private static bool TryBuildCheckpointFromBody(
        TemporalCheckpointBody? body,
        HttpContext context,
        out TemporalCheckpoint? checkpoint,
        out IResult problem)
    {
        checkpoint = null;
        problem = Results.Empty;

        if (body is null || string.IsNullOrWhiteSpace(body.Kind))
        {
            problem = ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                "A target checkpoint with a 'kind' is required.");
            return false;
        }

        if (!TryParseCheckpointKind(body.Kind, out var kind))
        {
            problem = ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                $"Unrecognized checkpoint kind '{body.Kind}'.");
            return false;
        }

        if (kind == TemporalCheckpointKind.Generation)
        {
            if (body.Generation is { } generation)
            {
                checkpoint = TemporalCheckpoint.ForGeneration(generation);
                return true;
            }

            if (!string.IsNullOrWhiteSpace(body.Value)
                && long.TryParse(body.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var gen))
            {
                checkpoint = TemporalCheckpoint.ForGeneration(gen);
                return true;
            }

            problem = ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                "A generation checkpoint requires a numeric 'generation' or 'value'.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(body.Value))
        {
            problem = ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
                $"A {kind} checkpoint requires a 'value'.");
            return false;
        }

        checkpoint = new TemporalCheckpoint(kind, body.Value);
        return true;
    }

    private static bool TryParseCheckpointKind(string token, out TemporalCheckpointKind kind)
        => Enum.TryParse(token, ignoreCase: true, out kind) && Enum.IsDefined(kind);

    private static int? ReadIntQuery(HttpContext context, string name)
    {
        var raw = context.Request.Query[name].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static IResult CreateGenericProblem(HttpContext context)
        => ProblemDetailsHelpers.CreateAdminProblem(
            context,
            StatusCodes.Status500InternalServerError,
            ProblemDetailsHelpers.GetTitle(StatusCodes.Status500InternalServerError),
            "An internal error occurred while processing the temporal history request.");

    private static void LogEndpointFailed(HttpContext context, string operation, Exception exception)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<TemporalHistorySlicesEndpointsLog>>();
        Log.EndpointFailed(logger, operation, exception);
    }

    private static partial class Log
    {
        [LoggerMessage(12121, LogLevel.Error, "Temporal history endpoint {Operation} failed")]
        public static partial void EndpointFailed(
            ILogger logger,
            string operation,
            Exception exception);
    }
}

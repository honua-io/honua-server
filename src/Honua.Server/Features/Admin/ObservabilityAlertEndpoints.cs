// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Monitoring;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Console Operate alert endpoints (#1168) — query, get, acknowledge, suppress, resolve.
/// </summary>
internal static class ObservabilityAlertEndpoints
{
    private const int MaxNoteLength = 1024;

    public static void MapObservabilityAlertEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Read-only ops-reader authorization (A12): the GET alert reads additionally admit an ops:read
        // credential, while the mutating POSTs (acknowledge/suppress/resolve) still require full admin
        // write — the ops-read policy is method-aware.
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/observability/alerts")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Observability", "Alerts")
            .RequireOpsReadAuthorization();

        group.MapGet("", HandleList)
            .WithDisplayName("List Observability Alerts")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapGet("/{eventId:long}", HandleGet)
            .WithDisplayName("Get Observability Alert")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("/{eventId:long}/acknowledge", HandleAcknowledge)
            .WithDisplayName("Acknowledge Observability Alert")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapPost("/{eventId:long}/suppress", HandleSuppress)
            .WithDisplayName("Suppress Observability Alert")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapPost("/{eventId:long}/resolve", HandleResolve)
            .WithDisplayName("Resolve Observability Alert")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));
    }

    private static async Task<IResult> HandleList(
        HttpRequest request,
        [FromServices] IAlertEventQuery query,
        CancellationToken cancellationToken)
    {
        if (!TryParseAlertFilter(request.Query, out var filter, out var error))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest, ProblemDetailsHelpers.GetTitle(400), error);
        }

        var page = await query.ListAsync(filter, cancellationToken).ConfigureAwait(false);
        var response = new ObservabilityAlertEventPageResponse
        {
            Items = page.Items.Select(ObservabilityAlertEventResponseMapper.Map).ToArray(),
            NextCursor = page.NextCursor,
            EvidencePosture = McpOpsObservabilityReader.BuildEventPosture(
                EvidencePostureVocabulary.SourceIds.AlertEvents, "alert-event-store", filter.From, filter.To,
                page.Items.Select(item => item.OccurredAt), page.NextCursor is not null, partial: false),
        };

        return Results.Json(response, ObservabilityJsonContext.Default.ObservabilityAlertEventPageResponse);
    }

    private static async Task<IResult> HandleGet(
        long eventId,
        [FromServices] IAlertEventQuery query,
        CancellationToken cancellationToken)
    {
        var summary = await query.GetAsync(eventId, cancellationToken).ConfigureAwait(false);
        if (summary is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status404NotFound, ProblemDetailsHelpers.GetTitle(404), $"Alert event '{eventId}' was not found.");
        }

        return Results.Json(
            ObservabilityAlertEventResponseMapper.Map(summary),
            ObservabilityJsonContext.Default.ObservabilityAlertEventResponse);
    }

    private static Task<IResult> HandleAcknowledge(
        long eventId, ObservabilityAlertAcknowledgeRequest? body,
        [FromServices] IAlertLifecycleMutationStore mutations, [FromServices] IAlertEventQuery query,
        HttpContext context, CancellationToken cancellationToken)
        => PerformLifecycleAsync(eventId, body?.Note, null, "alert.acknowledge", mutations, query, context, cancellationToken);

    private static Task<IResult> HandleSuppress(
        long eventId, ObservabilityAlertSuppressRequest? body,
        [FromServices] IAlertLifecycleMutationStore mutations, [FromServices] IAlertEventQuery query,
        HttpContext context, CancellationToken cancellationToken)
        => body is null ? Task.FromResult(BadRequest("A request body with 'suppressUntil' is required."))
            : PerformLifecycleAsync(eventId, body.Note, body.SuppressUntil, "alert.suppress", mutations, query, context, cancellationToken);

    private static Task<IResult> HandleResolve(
        long eventId, ObservabilityAlertResolveRequest? body,
        [FromServices] IAlertLifecycleMutationStore mutations, [FromServices] IAlertEventQuery query,
        HttpContext context, CancellationToken cancellationToken)
        => PerformLifecycleAsync(eventId, body?.Note, null, "alert.resolve", mutations, query, context, cancellationToken);

    private static async Task<IResult> PerformLifecycleAsync(
        long eventId, string? note, DateTimeOffset? suppressUntil, string action,
        IAlertLifecycleMutationStore mutations, IAlertEventQuery query, HttpContext context,
        CancellationToken cancellationToken)
    {
        if (note is { Length: > MaxNoteLength })
        {
            return BadRequest($"'note' must not exceed {MaxNoteLength} characters.");
        }

        var correlation = context.TraceIdentifier;
        if (string.IsNullOrWhiteSpace(correlation) || correlation.Length > 64)
        {
            return BadRequest("The correlation ID must contain between 1 and 64 characters.");
        }

        var auditEvent = new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventType = AuditEventType.AdminAction,
            Actor = ResolveActor(context),
            ActorType = AuditActorType.UserId,
            ResourceType = "alert_event",
            ResourceId = eventId.ToString(CultureInfo.InvariantCulture),
            Action = action,
            Outcome = AuditOutcome.Success,
            CorrelationId = correlation
        };
        AlertEventLifecycle? lifecycle;
        try
        {
            lifecycle = await mutations.MutateAsync(eventId, note, suppressUntil, auditEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (AlertLifecycleRetryConflictException exception)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(StatusCodes.Status409Conflict,
                ProblemDetailsHelpers.GetTitle(409), exception.Message);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(exception.Message);
        }

        if (lifecycle is null) { return NotFound(eventId); }
        var refreshed = await query.GetAsync(eventId, cancellationToken).ConfigureAwait(false);
        if (refreshed is null) { return NotFound(eventId); }
        return Results.Json(ObservabilityAlertEventResponseMapper.Map(refreshed),
            ObservabilityJsonContext.Default.ObservabilityAlertEventResponse);
    }

    internal static bool TryParseAlertFilter(IQueryCollection query, out AlertEventFilter filter, out string error)
    {
        filter = new AlertEventFilter();
        error = string.Empty;

        if (!QueryFilterParsers.TryParseDateTimeOffset(query, "from", out var from, out var parseError) ||
            !QueryFilterParsers.TryParseDateTimeOffset(query, "to", out var to, out parseError))
        {
            error = parseError;
            return false;
        }

        if (!QueryFilterParsers.TryParseInt(query, "layerId", out var layerId, out parseError) ||
            !QueryFilterParsers.TryParseLong(query, "objectId", out var objectId, out parseError) ||
            !QueryFilterParsers.TryParseLong(query, "ruleId", out var ruleId, out parseError) ||
            !QueryFilterParsers.TryParseInt(query, "pageSize", out var pageSize, out parseError))
        {
            error = parseError;
            return false;
        }

        if (!QueryFilterParsers.TryParseEnumList<AlertSeverity>(query, "severity", out var severities, out parseError) ||
            !QueryFilterParsers.TryParseEnumList<AlertIncidentStatus>(query, "incidentStatus", out var incidentStatuses, out parseError) ||
            !QueryFilterParsers.TryParseEnumList<AlertLifecycleStatus>(query, "lifecycleStatus", out var lifecycleStatuses, out parseError))
        {
            error = parseError;
            return false;
        }

        filter = filter with
        {
            From = from,
            To = to,
            ServiceId = QueryFilterParsers.GetString(query, "serviceId"),
            LayerId = layerId,
            ObjectId = objectId,
            RuleId = ruleId,
            Severities = severities,
            IncidentStatuses = incidentStatuses,
            LifecycleStatuses = lifecycleStatuses,
            PageSize = pageSize ?? 50,
            Cursor = QueryFilterParsers.GetString(query, "cursor")
        };

        return true;
    }

    private static string ResolveActor(HttpContext context)
    {
        return context.User?.Identity?.Name ?? AuditEvent.AnonymousActor;
    }

    private static IResult BadRequest(string detail)
        => ProblemDetailsHelpers.CreateAdminProblem(StatusCodes.Status400BadRequest,
            ProblemDetailsHelpers.GetTitle(400), detail);

    private static IResult NotFound(long eventId)
        => ProblemDetailsHelpers.CreateAdminProblem(StatusCodes.Status404NotFound,
            ProblemDetailsHelpers.GetTitle(404), $"Alert event '{eventId}' was not found.");
}

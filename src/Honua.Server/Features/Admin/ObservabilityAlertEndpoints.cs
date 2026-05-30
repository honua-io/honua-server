// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
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
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/observability/alerts")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Observability", "Alerts")
            .RequireAdminAuthorization();

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
            Items = page.Items.Select(MapSummary).ToArray(),
            NextCursor = page.NextCursor
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

        return Results.Json(MapSummary(summary), ObservabilityJsonContext.Default.ObservabilityAlertEventResponse);
    }

    private static Task<IResult> HandleAcknowledge(
        long eventId,
        ObservabilityAlertAcknowledgeRequest? body,
        [FromServices] IAlertLifecycleStore lifecycleStore,
        [FromServices] IAlertEventQuery query,
        [FromServices] IAuditLog auditLog,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        return PerformLifecycleAsync(
            eventId,
            body?.Note,
            action: "alert.acknowledge",
            query,
            auditLog,
            context,
            (actor, note, timestamp) => lifecycleStore.AcknowledgeAsync(eventId, actor, note, timestamp, cancellationToken),
            cancellationToken);
    }

    private static async Task<IResult> HandleSuppress(
        long eventId,
        ObservabilityAlertSuppressRequest? body,
        [FromServices] IAlertLifecycleStore lifecycleStore,
        [FromServices] IAlertEventQuery query,
        [FromServices] IAuditLog auditLog,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (body is null)
        {
            return BadRequest("A request body with 'suppressUntil' is required.");
        }

        var now = DateTimeOffset.UtcNow;
        if (body.SuppressUntil <= now)
        {
            return BadRequest("'suppressUntil' must be in the future.");
        }

        if (body.Note is { Length: > MaxNoteLength })
        {
            return BadRequest($"'note' must not exceed {MaxNoteLength} characters.");
        }

        var actor = ResolveActor(context);
        var lifecycle = await lifecycleStore.SuppressAsync(eventId, actor, body.SuppressUntil, body.Note, now, cancellationToken).ConfigureAwait(false);
        if (lifecycle is null)
        {
            return NotFound(eventId);
        }

        await RecordAuditAsync(auditLog, context, actor, eventId, "alert.suppress",
            $"{{\"suppressUntil\":\"{body.SuppressUntil.ToString("O", CultureInfo.InvariantCulture)}\"}}",
            cancellationToken).ConfigureAwait(false);

        var refreshed = await query.GetAsync(eventId, cancellationToken).ConfigureAwait(false);
        if (refreshed is null)
        {
            return NotFound(eventId);
        }

        return Results.Json(MapSummary(refreshed), ObservabilityJsonContext.Default.ObservabilityAlertEventResponse);
    }

    private static Task<IResult> HandleResolve(
        long eventId,
        ObservabilityAlertResolveRequest? body,
        [FromServices] IAlertLifecycleStore lifecycleStore,
        [FromServices] IAlertEventQuery query,
        [FromServices] IAuditLog auditLog,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        return PerformLifecycleAsync(
            eventId,
            body?.Note,
            action: "alert.resolve",
            query,
            auditLog,
            context,
            (actor, note, timestamp) => lifecycleStore.ResolveAsync(eventId, actor, note, timestamp, cancellationToken),
            cancellationToken);
    }

    private static async Task<IResult> PerformLifecycleAsync(
        long eventId,
        string? note,
        string action,
        IAlertEventQuery query,
        IAuditLog auditLog,
        HttpContext context,
        Func<string, string?, DateTimeOffset, Task<AlertEventLifecycle?>> mutate,
        CancellationToken cancellationToken)
    {
        if (note is { Length: > MaxNoteLength })
        {
            return BadRequest($"'note' must not exceed {MaxNoteLength} characters.");
        }

        var actor = ResolveActor(context);
        var now = DateTimeOffset.UtcNow;
        var lifecycle = await mutate(actor, note, now).ConfigureAwait(false);
        if (lifecycle is null)
        {
            return NotFound(eventId);
        }

        await RecordAuditAsync(auditLog, context, actor, eventId, action, details: string.Empty, cancellationToken)
            .ConfigureAwait(false);

        var refreshed = await query.GetAsync(eventId, cancellationToken).ConfigureAwait(false);
        if (refreshed is null)
        {
            return NotFound(eventId);
        }

        return Results.Json(MapSummary(refreshed), ObservabilityJsonContext.Default.ObservabilityAlertEventResponse);
    }

    private static Task RecordAuditAsync(
        IAuditLog auditLog,
        HttpContext context,
        string actor,
        long eventId,
        string action,
        string details,
        CancellationToken cancellationToken)
    {
        var auditEvent = new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventType = AuditEventType.AdminAction,
            Actor = actor,
            ActorType = AuditActorType.UserId,
            ResourceType = "alert_event",
            ResourceId = eventId.ToString(CultureInfo.InvariantCulture),
            Action = action,
            Outcome = AuditOutcome.Success,
            CorrelationId = context.TraceIdentifier,
            Details = details
        };

        return auditLog.RecordAsync(auditEvent, cancellationToken);
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

    private static ObservabilityAlertEventResponse MapSummary(AlertEventSummary summary)
    {
        return new ObservabilityAlertEventResponse
        {
            EventId = summary.EventId,
            RuleId = summary.RuleId,
            RuleName = summary.RuleName,
            ZoneId = summary.ZoneId,
            ServiceId = summary.ServiceId,
            LayerId = summary.LayerId,
            ObjectId = summary.ObjectId,
            TriggerType = summary.TriggerType.ToString().ToLowerInvariant(),
            Severity = summary.Severity.ToString().ToLowerInvariant(),
            OccurredAt = summary.OccurredAt,
            IncidentStatus = summary.IncidentStatus.ToString().ToLowerInvariant(),
            IncidentDurationMs = summary.IncidentDurationMs,
            LifecycleStatus = summary.LifecycleStatus.ToString().ToLowerInvariant(),
            AcknowledgedAt = summary.AcknowledgedAt,
            AcknowledgedBy = summary.AcknowledgedBy,
            SuppressedUntil = summary.SuppressedUntil,
            ResolvedAt = summary.ResolvedAt,
            ResolvedBy = summary.ResolvedBy,
            ResourceRef = "alert/" + summary.EventId.ToString(CultureInfo.InvariantCulture)
        };
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

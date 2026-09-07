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

    /// <summary>Optional operator-supplied retry identity for a lifecycle mutation.</summary>
    private const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>Bound on the caller-controlled portion of the retry identity.</summary>
    private const int MaxIdempotencyKeyLength = 128;

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
        long eventId,
        ObservabilityAlertAcknowledgeRequest? body,
        [FromServices] IAlertLifecycleStore lifecycleStore,
        [FromServices] IAlertEventQuery query,
        [FromServices] IAlertAuditCompleter auditCompleter,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        return PerformLifecycleAsync(
            eventId,
            AlertLifecycleAction.Acknowledge,
            body?.Note,
            suppressUntil: null,
            details: string.Empty,
            lifecycleStore,
            query,
            auditCompleter,
            context,
            cancellationToken);
    }

    private static Task<IResult> HandleSuppress(
        long eventId,
        ObservabilityAlertSuppressRequest? body,
        [FromServices] IAlertLifecycleStore lifecycleStore,
        [FromServices] IAlertEventQuery query,
        [FromServices] IAlertAuditCompleter auditCompleter,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (body is null)
        {
            return Task.FromResult(BadRequest("A request body with 'suppressUntil' is required."));
        }

        if (body.SuppressUntil <= DateTimeOffset.UtcNow)
        {
            return Task.FromResult(BadRequest("'suppressUntil' must be in the future."));
        }

        return PerformLifecycleAsync(
            eventId,
            AlertLifecycleAction.Suppress,
            body.Note,
            body.SuppressUntil,
            details: $"{{\"suppressUntil\":\"{body.SuppressUntil.ToString("O", CultureInfo.InvariantCulture)}\"}}",
            lifecycleStore,
            query,
            auditCompleter,
            context,
            cancellationToken);
    }

    private static Task<IResult> HandleResolve(
        long eventId,
        ObservabilityAlertResolveRequest? body,
        [FromServices] IAlertLifecycleStore lifecycleStore,
        [FromServices] IAlertEventQuery query,
        [FromServices] IAlertAuditCompleter auditCompleter,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        return PerformLifecycleAsync(
            eventId,
            AlertLifecycleAction.Resolve,
            body?.Note,
            suppressUntil: null,
            details: string.Empty,
            lifecycleStore,
            query,
            auditCompleter,
            context,
            cancellationToken);
    }

    /// <summary>
    /// Applies one operator lifecycle mutation.
    /// </summary>
    /// <remarks>
    /// The mutation and its domain audit INTENT are committed in a single store
    /// transaction, then the audit record is written and the intent completed. A
    /// fault or a process death anywhere after the commit leaves the mutation with
    /// a pending intent, which <c>AlertAuditOutboxReconciler</c> completes
    /// deterministically on restart — so a lifecycle mutation is never externally
    /// observable without either its domain audit record or a durable
    /// reconciliation record that produces it (#3865).
    /// </remarks>
    private static async Task<IResult> PerformLifecycleAsync(
        long eventId,
        AlertLifecycleAction action,
        string? note,
        DateTimeOffset? suppressUntil,
        string details,
        IAlertLifecycleStore lifecycleStore,
        IAlertEventQuery query,
        IAlertAuditCompleter auditCompleter,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (note is { Length: > MaxNoteLength })
        {
            return BadRequest($"'note' must not exceed {MaxNoteLength} characters.");
        }

        var actor = ResolveActor(context);
        var correlationId = context.TraceIdentifier;
        var command = new AlertLifecycleCommand
        {
            EventId = eventId,
            Action = action,
            Actor = actor,
            Note = note,
            SuppressUntil = suppressUntil,
            OccurredAt = DateTimeOffset.UtcNow,
            CorrelationId = correlationId,
            IdempotencyKey = ResolveIdempotencyKey(context, eventId, action, correlationId),
            Details = details
        };

        var transition = await lifecycleStore.ApplyAsync(command, cancellationToken).ConfigureAwait(false);
        if (transition.Lifecycle is null)
        {
            return NotFound(eventId);
        }

        if (transition.Intent is { } intent)
        {
            await auditCompleter.CompleteAsync(
                    intent,
                    context.Connection.RemoteIpAddress?.ToString(),
                    context.Request.Headers.UserAgent.ToString() is { Length: > 0 } agent ? agent : null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var refreshed = await query.GetAsync(eventId, cancellationToken).ConfigureAwait(false);
        if (refreshed is null)
        {
            return NotFound(eventId);
        }

        return Results.Json(
            ObservabilityAlertEventResponseMapper.Map(refreshed),
            ObservabilityJsonContext.Default.ObservabilityAlertEventResponse);
    }

    /// <summary>
    /// Resolves the operator retry identity: an explicit <c>Idempotency-Key</c>
    /// header when the caller supplies one, otherwise the request's own
    /// correlation identity scoped to the event and action. Retrying with the same
    /// identity yields one logical transition and one domain audit action.
    /// </summary>
    private static string ResolveIdempotencyKey(
        HttpContext context, long eventId, AlertLifecycleAction action, string correlationId)
    {
        var supplied = context.Request.Headers[IdempotencyKeyHeader].ToString();
        var identity = string.IsNullOrWhiteSpace(supplied) ? correlationId : supplied.Trim();
        if (identity.Length > MaxIdempotencyKeyLength)
        {
            identity = identity[..MaxIdempotencyKeyLength];
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{eventId}:{AlertAuditActions.ForLifecycle(action)}:{identity}");
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

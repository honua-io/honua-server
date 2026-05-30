// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Console Operate audit log query endpoint (#1168).
/// </summary>
internal static class ObservabilityAuditEndpoints
{
    public static void MapObservabilityAuditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/observability/audit")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Observability", "Audit")
            .RequireAdminAuthorization();

        group.MapGet("", HandleList)
            .WithDisplayName("List Audit Log Entries")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));
    }

    private static async Task<IResult> HandleList(
        HttpRequest request,
        [FromServices] IAuditLogReader reader,
        CancellationToken cancellationToken)
    {
        if (!TryParseAuditFilter(request.Query, out var filter, out var error))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest, ProblemDetailsHelpers.GetTitle(400), error);
        }

        var page = await reader.ListAsync(filter, cancellationToken).ConfigureAwait(false);
        var response = new ObservabilityAuditPageResponse
        {
            Items = page.Items.Select(MapRecord).ToArray(),
            NextCursor = page.NextCursor
        };

        return Results.Json(response, ObservabilityJsonContext.Default.ObservabilityAuditPageResponse);
    }

    internal static bool TryParseAuditFilter(IQueryCollection query, out AuditLogFilter filter, out string error)
    {
        filter = new AuditLogFilter();
        error = string.Empty;

        if (!QueryFilterParsers.TryParseDateTimeOffset(query, "from", out var from, out var parseError) ||
            !QueryFilterParsers.TryParseDateTimeOffset(query, "to", out var to, out parseError) ||
            !QueryFilterParsers.TryParseInt(query, "pageSize", out var pageSize, out parseError))
        {
            error = parseError;
            return false;
        }

        if (!QueryFilterParsers.TryParseEnumList<AuditActorType>(query, "actorType", out var actorTypes, out parseError) ||
            !QueryFilterParsers.TryParseEnumList<AuditEventType>(query, "eventType", out var eventTypes, out parseError) ||
            !QueryFilterParsers.TryParseEnumList<AuditOutcome>(query, "outcome", out var outcomes, out parseError))
        {
            error = parseError;
            return false;
        }

        filter = filter with
        {
            From = from,
            To = to,
            Actor = QueryFilterParsers.GetString(query, "actor"),
            ActorTypes = actorTypes,
            ResourceType = QueryFilterParsers.GetString(query, "resourceType"),
            ResourceId = QueryFilterParsers.GetString(query, "resourceId"),
            Action = QueryFilterParsers.GetString(query, "action"),
            EventTypes = eventTypes,
            Outcomes = outcomes,
            CorrelationId = QueryFilterParsers.GetString(query, "correlationId"),
            PageSize = pageSize ?? 50,
            Cursor = QueryFilterParsers.GetString(query, "cursor")
        };

        return true;
    }

    private static ObservabilityAuditRecordResponse MapRecord(AuditEventRecord record)
    {
        return new ObservabilityAuditRecordResponse
        {
            AuditId = record.AuditId,
            Timestamp = record.Timestamp,
            EventType = record.EventType.ToString(),
            Actor = record.Actor,
            ActorType = record.ActorType.ToString(),
            ResourceType = record.ResourceType,
            ResourceId = record.ResourceId,
            Action = record.Action,
            Outcome = record.Outcome.ToString(),
            CorrelationId = record.CorrelationId,
            RemoteIp = record.RemoteIp,
            UserAgent = record.UserAgent,
            Details = record.Details
        };
    }
}

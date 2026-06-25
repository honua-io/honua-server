// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Console Operate audit log query endpoint (#1168) plus the SIEM export and
/// tamper-evidence surfaces (#350, #509). All routes are operator-gated via
/// <c>RequireAdminAuthorization</c>.
/// </summary>
internal static class ObservabilityAuditEndpoints
{
    private const string JsonLinesContentType = "application/x-ndjson";
    private const string CefContentType = "text/plain; charset=utf-8";

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

        group.MapGet("/export", HandleExport)
            .WithDisplayName("Export Audit Trail (SIEM)")
            .WithSummary("Stream the audit trail for SIEM ingestion as JSON Lines (default) or CEF, filtered by time range, actor, resource type, or action.")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapGet("/verify", HandleVerify)
            .WithDisplayName("Verify Audit Trail Integrity")
            .WithSummary("Replay the tamper-evident hash chain and report whether the audit trail has been altered.")
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

    private static async Task<IResult> HandleExport(
        HttpContext context,
        [FromServices] IAuditLogExporter exporter,
        [FromServices] IAuditLog auditLog,
        CancellationToken cancellationToken)
    {
        if (!TryParseExportFilter(context.Request.Query, out var filter, out var format, out var error))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status400BadRequest, ProblemDetailsHelpers.GetTitle(400), error);
        }

        var isCef = string.Equals(format, "cef", StringComparison.OrdinalIgnoreCase);
        context.Response.ContentType = isCef ? CefContentType : JsonLinesContentType;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";

        var writer = new StreamWriter(context.Response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await using (writer.ConfigureAwait(false))
        {
            await foreach (var record in exporter.ExportAsync(filter, cancellationToken).ConfigureAwait(false))
            {
                var line = isCef
                    ? AuditCefFormatter.Format(record)
                    : JsonSerializer.Serialize(MapRecord(record), ObservabilityJsonContext.Default.ObservabilityAuditRecordResponse);
                await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        // Reading the audit trail in bulk is itself a security-relevant action.
        await auditLog.RecordAsync(new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventType = AuditEventType.DataExport,
            Actor = context.GetUserIdentity(),
            ActorType = AuditActorType.UserId,
            ResourceType = "audit-log",
            ResourceId = isCef ? "cef" : "jsonl",
            Action = "audit.export",
            Outcome = AuditOutcome.Success,
            CorrelationId = context.TraceIdentifier,
            Details = $"{{\"format\":\"{(isCef ? "cef" : "jsonl")}\"}}",
        }, CancellationToken.None).ConfigureAwait(false);

        return Results.Empty;
    }

    private static async Task<IResult> HandleVerify(
        [FromServices] IAuditLogIntegrityVerifier verifier,
        CancellationToken cancellationToken)
    {
        var report = await verifier.VerifyAsync(cancellationToken).ConfigureAwait(false);
        var response = new ObservabilityAuditIntegrityResponse
        {
            Verified = report.Verified,
            RowsChecked = report.RowsChecked,
            UnhashedRows = report.UnhashedRows,
            FirstBrokenAuditId = report.FirstBrokenAuditId,
            FailureReason = report.FailureReason,
        };

        return Results.Json(response, ObservabilityJsonContext.Default.ObservabilityAuditIntegrityResponse);
    }

    internal static bool TryParseExportFilter(
        IQueryCollection query,
        out AuditExportFilter filter,
        out string format,
        out string error)
    {
        filter = new AuditExportFilter();
        format = "jsonl";
        error = string.Empty;

        if (!QueryFilterParsers.TryParseDateTimeOffset(query, "from", out var from, out var parseError) ||
            !QueryFilterParsers.TryParseDateTimeOffset(query, "to", out var to, out parseError) ||
            !QueryFilterParsers.TryParseInt(query, "limit", out var limit, out parseError))
        {
            error = parseError;
            return false;
        }

        var requestedFormat = QueryFilterParsers.GetString(query, "format");
        if (!string.IsNullOrWhiteSpace(requestedFormat))
        {
            if (!string.Equals(requestedFormat, "jsonl", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(requestedFormat, "cef", StringComparison.OrdinalIgnoreCase))
            {
                error = "format must be 'jsonl' or 'cef'.";
                return false;
            }

            format = requestedFormat;
        }

        filter = filter with
        {
            From = from,
            To = to,
            Actor = QueryFilterParsers.GetString(query, "actor"),
            ResourceType = QueryFilterParsers.GetString(query, "resourceType"),
            Action = QueryFilterParsers.GetString(query, "action"),
            Limit = limit,
        };

        return true;
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

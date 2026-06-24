// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.FieldWorkflows.Export;
using Honua.Core.Features.FieldWorkflows.Review;
using Honua.Infrastructure.Models;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.FieldWorkflows.Export;

/// <summary>
/// Back-office field export package service (#1160). Adapts admin requests into the
/// shared <see cref="IFieldExportStore"/>, serializes the selected record set with
/// <see cref="FieldExportSerializer"/>, audits every export, and maps failures
/// through shared problem helpers. Authorization is enforced at the route group via
/// <c>RequireAdminAuthorization</c>.
/// </summary>
internal sealed class FieldExportService
{
    private const int MaxListLimit = 500;

    private readonly IFieldExportStore _store;
    private readonly IAuditLog _auditLog;
    private readonly ILogger<FieldExportService> _logger;

    public FieldExportService(
        IFieldExportStore store,
        IAuditLog auditLog,
        ILogger<FieldExportService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IResult> CreateAsync(HttpContext context)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("FieldExport.Create");
        activity?.SetTag("honua.protocol", "field-workflows");
        activity?.SetTag("honua.operation", "create-export");

        var request = await ReadAsync(context, FieldExportJsonContext.Default.FieldExportRequest).ConfigureAwait(false)
            ?? new FieldExportRequest();

        var format = string.IsNullOrWhiteSpace(request.Format) ? FieldExportFormat.GeoJson : request.Format.Trim().ToLowerInvariant();
        if (!FieldExportFormat.IsValid(format))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                $"format must be one of: {string.Join(", ", FieldExportFormat.All)}.");
        }

        var reviewStatus = Trimmed(request.ReviewStatus);
        if (reviewStatus is not null && !FieldReviewStatus.IsValid(reviewStatus))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status400BadRequest,
                $"reviewStatus must be one of: {string.Join(", ", FieldReviewStatus.All)}.");
        }

        var normalized = new FieldExportRequest
        {
            Format = format,
            FormId = Trimmed(request.FormId),
            ServiceId = Trimmed(request.ServiceId),
            LayerId = request.LayerId,
            ReviewStatus = reviewStatus,
            AssignedTo = Trimmed(request.AssignedTo),
            SyncStatus = Trimmed(request.SyncStatus),
            HasConflict = request.HasConflict,
            SubmittedFrom = request.SubmittedFrom,
            SubmittedTo = request.SubmittedTo
        };

        var actor = ResolveActor(context);
        var package = await _store.CreateExportAsync(normalized, actor, context.RequestAborted).ConfigureAwait(false);
        var payload = FieldExportSerializer.Serialize(format, package.Items);

        activity?.SetTag("honua.result_count", package.Items.Length);
        activity?.SetTag("honua.export_format", format);

        await RecordAuditAsync(context, "field.export.create", package.Record.ExportId, AuditOutcome.Success,
            $"{{\"format\":{JsonString(format)},\"recordCount\":{package.Record.RecordCount.ToString(CultureInfo.InvariantCulture)}}}").ConfigureAwait(false);
        FieldExportLog.Created(_logger, package.Record.ExportId, format, package.Record.RecordCount);

        ApplyNoStore(context.Response);
        context.Response.Headers["X-Honua-Export-Id"] = package.Record.ExportId.ToString();
        context.Response.Headers["X-Honua-Export-Count"] = package.Record.RecordCount.ToString(CultureInfo.InvariantCulture);
        var filename = $"field-export-{package.Record.ExportId}.{FieldExportFormat.Extension(format)}";
        context.Response.Headers.ContentDisposition = $"attachment; filename=\"{filename}\"";
        return Results.Bytes(payload, FieldExportFormat.ContentType(format));
    }

    public async Task<IResult> ListAsync(HttpContext context)
    {
        var limit = TryParseInt(context.Request.Query["limit"]) is int l ? Math.Clamp(l, 1, MaxListLimit) : 50;
        var offset = TryParseInt(context.Request.Query["offset"]) is int o ? Math.Max(0, o) : 0;

        var result = await _store.ListExportsAsync(limit, offset, context.RequestAborted).ConfigureAwait(false);
        ApplyNoStore(context.Response);
        return Results.Json(result, FieldExportJsonContext.Default.FieldExportRecordListResult);
    }

    private static async Task<T?> ReadAsync<T>(HttpContext context, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (context.Request.ContentLength is 0 || context.Request.ContentLength is null)
        {
            return null;
        }

        try
        {
            return await JsonSerializer.DeserializeAsync(context.Request.Body, typeInfo, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int? TryParseInt(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static void ApplyNoStore(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
    }

    private static string ResolveActor(HttpContext context)
        => context.User.FindFirstValue(ClaimTypes.NameIdentifier)
           ?? context.User.Identity?.Name
           ?? AuditEvent.AnonymousActor;

    private static string JsonString(string? value)
        => value is null ? "null" : "\"" + JsonEncodedText.Encode(value) + "\"";

    private Task RecordAuditAsync(
        HttpContext context,
        string action,
        Guid exportId,
        AuditOutcome outcome,
        string details)
        => _auditLog.RecordAsync(new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventType = AuditEventType.AdminAction,
            Actor = ResolveActor(context),
            ActorType = context.User.Identity?.IsAuthenticated == true ? AuditActorType.UserId : AuditActorType.Anonymous,
            ResourceType = "field_export",
            ResourceId = exportId.ToString(),
            Action = action,
            Outcome = outcome,
            CorrelationId = context.TraceIdentifier,
            RemoteIp = context.Connection.RemoteIpAddress?.ToString(),
            UserAgent = context.Request.Headers.UserAgent.ToString(),
            Details = details
        }, context.RequestAborted);
}

internal static partial class FieldExportLog
{
    [LoggerMessage(EventId = 118510, Level = LogLevel.Information, Message = "Generated field export {ExportId} ({Format}) covering {RecordCount} records.")]
    public static partial void Created(ILogger logger, Guid exportId, string format, long recordCount);
}

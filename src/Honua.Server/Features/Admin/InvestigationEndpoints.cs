// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Console Operate investigation endpoints (#1168).
/// </summary>
internal static class InvestigationEndpoints
{
    private const int MaxTitleLength = 256;
    private const int MaxSummaryLength = 4096;
    private const int MaxNoteLength = 1024;

    public static void MapInvestigationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/investigations")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Observability", "Investigations")
            .RequireAdminAuthorization();

        group.MapGet("", HandleList)
            .WithDisplayName("List Investigations")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("", HandleCreate)
            .WithDisplayName("Create Investigation")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapGet("/{investigationId}", HandleGet)
            .WithDisplayName("Get Investigation")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPatch("/{investigationId}", HandleUpdate)
            .WithDisplayName("Update Investigation")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Patch }));

        group.MapPost("/{investigationId}/pins", HandleAddPin)
            .WithDisplayName("Pin Event To Investigation")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapDelete("/{investigationId}/pins/{pinId:long}", HandleRemovePin)
            .WithDisplayName("Remove Pin From Investigation")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Delete }));

        group.MapPost("/{investigationId}/links", HandleAddLink)
            .WithDisplayName("Link Resource To Investigation")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapDelete("/{investigationId}/links/{linkId:long}", HandleRemoveLink)
            .WithDisplayName("Remove Link From Investigation")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Delete }));
    }

    private static async Task<IResult> HandleList(
        HttpRequest request,
        [FromServices] IInvestigationStore store,
        CancellationToken cancellationToken)
    {
        if (!TryParseInvestigationFilter(request.Query, out var filter, out var error))
        {
            return BadRequest(error);
        }

        var page = await store.ListAsync(filter, cancellationToken).ConfigureAwait(false);
        var response = new InvestigationPageResponse
        {
            Items = page.Items.Select(MapSummary).ToArray(),
            NextCursor = page.NextCursor
        };
        return Results.Json(response, InvestigationJsonContext.Default.InvestigationPageResponse);
    }

    private static async Task<IResult> HandleCreate(
        CreateInvestigationRequest? body,
        [FromServices] IInvestigationStore store,
        [FromServices] IAuditLog auditLog,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Title))
        {
            return BadRequest("'title' is required.");
        }

        if (body.Title.Length > MaxTitleLength)
        {
            return BadRequest($"'title' must not exceed {MaxTitleLength} characters.");
        }

        if (body.Summary is { Length: > MaxSummaryLength })
        {
            return BadRequest($"'summary' must not exceed {MaxSummaryLength} characters.");
        }

        var actor = ResolveActor(context);
        var record = await store.CreateAsync(body.Title.Trim(), actor, body.Summary, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        await RecordAuditAsync(auditLog, context, actor, record.InvestigationId, "investigation.create", string.Empty, cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(MapRecord(record), InvestigationJsonContext.Default.InvestigationResponse,
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> HandleGet(
        string investigationId,
        [FromServices] IInvestigationStore store,
        CancellationToken cancellationToken)
    {
        var record = await store.GetAsync(investigationId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound(investigationId);
        }

        return Results.Json(MapRecord(record), InvestigationJsonContext.Default.InvestigationResponse);
    }

    private static async Task<IResult> HandleUpdate(
        string investigationId,
        UpdateInvestigationRequest? body,
        [FromServices] IInvestigationStore store,
        [FromServices] IAuditLog auditLog,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (body is null)
        {
            return BadRequest("A JSON body is required.");
        }

        if (body.Title is { Length: > MaxTitleLength })
        {
            return BadRequest($"'title' must not exceed {MaxTitleLength} characters.");
        }

        if (body.Summary is { Length: > MaxSummaryLength })
        {
            return BadRequest($"'summary' must not exceed {MaxSummaryLength} characters.");
        }

        InvestigationStatus? status = null;
        if (!string.IsNullOrWhiteSpace(body.Status))
        {
            if (!Enum.TryParse<InvestigationStatus>(body.Status, ignoreCase: true, out var parsed))
            {
                return BadRequest("'status' must be 'open' or 'closed'.");
            }

            status = parsed;
        }

        var actor = ResolveActor(context);
        var record = await store.UpdateAsync(investigationId, body.Title?.Trim(), body.Summary, status, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
        {
            return NotFound(investigationId);
        }

        await RecordAuditAsync(auditLog, context, actor, investigationId, "investigation.update", string.Empty, cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(MapRecord(record), InvestigationJsonContext.Default.InvestigationResponse);
    }

    private static async Task<IResult> HandleAddPin(
        string investigationId,
        AddInvestigationPinRequest? body,
        [FromServices] IInvestigationStore store,
        [FromServices] IAuditLog auditLog,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.EventRef) || string.IsNullOrWhiteSpace(body.EventKind))
        {
            return BadRequest("'eventRef' and 'eventKind' are required.");
        }

        if (!Enum.TryParse<OperateEventKind>(body.EventKind, ignoreCase: true, out var kind))
        {
            return BadRequest($"'eventKind' contains unsupported value '{body.EventKind}'.");
        }

        if (body.Note is { Length: > MaxNoteLength })
        {
            return BadRequest($"'note' must not exceed {MaxNoteLength} characters.");
        }

        var actor = ResolveActor(context);
        var now = DateTimeOffset.UtcNow;
        var record = await store.AddPinAsync(investigationId, body.EventRef.Trim(), kind, body.OccurredAt, body.Note, actor, now, cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
        {
            return NotFound(investigationId);
        }

        await RecordAuditAsync(auditLog, context, actor, investigationId, "investigation.pin.add", $"{{\"eventRef\":\"{body.EventRef.Trim()}\"}}", cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(MapRecord(record), InvestigationJsonContext.Default.InvestigationResponse);
    }

    private static async Task<IResult> HandleRemovePin(
        string investigationId,
        long pinId,
        [FromServices] IInvestigationStore store,
        [FromServices] IAuditLog auditLog,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var actor = ResolveActor(context);
        var record = await store.RemovePinAsync(investigationId, pinId, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
        {
            return NotFound(investigationId);
        }

        await RecordAuditAsync(auditLog, context, actor, investigationId, "investigation.pin.remove",
            $"{{\"pinId\":{pinId.ToString(CultureInfo.InvariantCulture)}}}",
            cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(MapRecord(record), InvestigationJsonContext.Default.InvestigationResponse);
    }

    private static async Task<IResult> HandleAddLink(
        string investigationId,
        AddInvestigationLinkRequest? body,
        [FromServices] IInvestigationStore store,
        [FromServices] IAuditLog auditLog,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.ResourceId) || string.IsNullOrWhiteSpace(body.ResourceKind))
        {
            return BadRequest("'resourceId' and 'resourceKind' are required.");
        }

        if (!Enum.TryParse<InvestigationResourceKind>(body.ResourceKind, ignoreCase: true, out var kind))
        {
            return BadRequest($"'resourceKind' must be one of alert, job, release, changeSet.");
        }

        if (body.Note is { Length: > MaxNoteLength })
        {
            return BadRequest($"'note' must not exceed {MaxNoteLength} characters.");
        }

        var actor = ResolveActor(context);
        var record = await store.AddLinkAsync(investigationId, kind, body.ResourceId.Trim(), body.Note, actor, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
        {
            return NotFound(investigationId);
        }

        await RecordAuditAsync(auditLog, context, actor, investigationId, "investigation.link.add",
            $"{{\"resourceKind\":\"{kind.ToString().ToLowerInvariant()}\",\"resourceId\":\"{body.ResourceId.Trim()}\"}}",
            cancellationToken).ConfigureAwait(false);

        return Results.Json(MapRecord(record), InvestigationJsonContext.Default.InvestigationResponse);
    }

    private static async Task<IResult> HandleRemoveLink(
        string investigationId,
        long linkId,
        [FromServices] IInvestigationStore store,
        [FromServices] IAuditLog auditLog,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var actor = ResolveActor(context);
        var record = await store.RemoveLinkAsync(investigationId, linkId, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
        {
            return NotFound(investigationId);
        }

        await RecordAuditAsync(auditLog, context, actor, investigationId, "investigation.link.remove",
            $"{{\"linkId\":{linkId.ToString(CultureInfo.InvariantCulture)}}}",
            cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(MapRecord(record), InvestigationJsonContext.Default.InvestigationResponse);
    }

    internal static bool TryParseInvestigationFilter(IQueryCollection query, out InvestigationFilter filter, out string error)
    {
        filter = new InvestigationFilter();
        error = string.Empty;

        if (!QueryFilterParsers.TryParseInt(query, "pageSize", out var pageSize, out var parseError))
        {
            error = parseError;
            return false;
        }

        InvestigationStatus? status = null;
        if (query.TryGetValue("status", out var rawStatus) && !string.IsNullOrWhiteSpace(rawStatus))
        {
            if (!Enum.TryParse<InvestigationStatus>(rawStatus!, ignoreCase: true, out var parsedStatus))
            {
                error = "'status' must be 'open' or 'closed'.";
                return false;
            }

            status = parsedStatus;
        }

        filter = filter with
        {
            Status = status,
            CreatedBy = QueryFilterParsers.GetString(query, "createdBy"),
            TitleContains = QueryFilterParsers.GetString(query, "titleContains"),
            PageSize = pageSize ?? 50,
            Cursor = QueryFilterParsers.GetString(query, "cursor")
        };

        return true;
    }

    private static InvestigationResponse MapRecord(InvestigationRecord record)
    {
        return new InvestigationResponse
        {
            InvestigationId = record.InvestigationId,
            Title = record.Title,
            Status = record.Status.ToString().ToLowerInvariant(),
            CreatedAt = record.CreatedAt,
            CreatedBy = record.CreatedBy,
            UpdatedAt = record.UpdatedAt,
            Summary = record.Summary,
            Pins = record.Pins.Select(MapPin).ToArray(),
            Links = record.Links.Select(MapLink).ToArray()
        };
    }

    private static InvestigationSummaryResponse MapSummary(InvestigationSummary summary)
    {
        return new InvestigationSummaryResponse
        {
            InvestigationId = summary.InvestigationId,
            Title = summary.Title,
            Status = summary.Status.ToString().ToLowerInvariant(),
            CreatedAt = summary.CreatedAt,
            CreatedBy = summary.CreatedBy,
            UpdatedAt = summary.UpdatedAt,
            Summary = summary.Summary,
            PinCount = summary.PinCount,
            LinkCount = summary.LinkCount
        };
    }

    private static InvestigationPinResponse MapPin(InvestigationPin pin)
    {
        return new InvestigationPinResponse
        {
            PinId = pin.PinId,
            EventRef = pin.EventRef,
            EventKind = pin.EventKind.ToString().ToLowerInvariant(),
            OccurredAt = pin.OccurredAt,
            Note = pin.Note,
            CreatedAt = pin.CreatedAt,
            CreatedBy = pin.CreatedBy
        };
    }

    private static InvestigationLinkResponse MapLink(InvestigationLink link)
    {
        return new InvestigationLinkResponse
        {
            LinkId = link.LinkId,
            ResourceKind = link.ResourceKind.ToString().ToLowerInvariant(),
            ResourceId = link.ResourceId,
            Note = link.Note,
            CreatedAt = link.CreatedAt,
            CreatedBy = link.CreatedBy
        };
    }

    private static string ResolveActor(HttpContext context)
        => context.User?.Identity?.Name ?? AuditEvent.AnonymousActor;

    private static Task RecordAuditAsync(
        IAuditLog auditLog,
        HttpContext context,
        string actor,
        string investigationId,
        string action,
        string details,
        CancellationToken cancellationToken)
    {
        var evt = new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventType = AuditEventType.AdminAction,
            Actor = actor,
            ActorType = AuditActorType.UserId,
            ResourceType = "investigation",
            ResourceId = investigationId,
            Action = action,
            Outcome = AuditOutcome.Success,
            CorrelationId = context.TraceIdentifier,
            Details = details
        };

        return auditLog.RecordAsync(evt, cancellationToken);
    }

    private static IResult BadRequest(string detail)
        => ProblemDetailsHelpers.CreateAdminProblem(StatusCodes.Status400BadRequest,
            ProblemDetailsHelpers.GetTitle(400), detail);

    private static IResult NotFound(string id)
        => ProblemDetailsHelpers.CreateAdminProblem(StatusCodes.Status404NotFound,
            ProblemDetailsHelpers.GetTitle(404), $"Investigation '{id}' was not found.");
}

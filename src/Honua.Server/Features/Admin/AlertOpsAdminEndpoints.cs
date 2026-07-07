// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Admin endpoints for the alert dispatch self-healing actuators (#2561): dead-letter
/// redrive and per-channel pause/resume. These operate the delivery outbox directly and
/// mirror the ops actions exposed through the operation gateway; each is idempotent and
/// audited (the audit event surfaces on the Operate timeline).
/// </summary>
internal static class AlertOpsAdminEndpoints
{
    private const int MaxRedriveBatchLimit = 5000;
    private const int DefaultRedriveBatchLimit = 500;

    public static void MapAlertOpsAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/alerts")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Alerts")
            .RequireAdminAuthorization();

        group.MapPost("/dispatch/redrive", HandleRedriveDeadLetters)
            .WithDisplayName("Redrive Alert Dead-Letters")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapGet("/channels", HandleListChannelStates)
            .WithDisplayName("List Alert Channel States")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get }));

        group.MapPost("/channels/{channel}/pause", HandlePauseChannel)
            .WithDisplayName("Pause Alert Channel")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));

        group.MapPost("/channels/{channel}/resume", HandleResumeChannel)
            .WithDisplayName("Resume Alert Channel")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }));
    }

    private static async Task<IResult> HandleRedriveDeadLetters(
        int? maxCount,
        [FromServices] IAlertDispatchStore store,
        [FromServices] IAuditLog auditLog,
        [FromServices] TimeProvider timeProvider,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var limit = maxCount is > 0 ? Math.Min(maxCount.Value, MaxRedriveBatchLimit) : DefaultRedriveBatchLimit;
        var now = timeProvider.GetUtcNow();
        var redriven = await store.RedriveDeadLettersAsync(now, limit, cancellationToken).ConfigureAwait(false);

        await RecordAuditAsync(
            auditLog,
            context,
            action: "alerts.redrive_dead_letters",
            resourceType: "alert-dispatch",
            resourceId: "dead-letters",
            details: new AlertOpsAuditDetails { Redriven = redriven },
            now,
            cancellationToken).ConfigureAwait(false);

        var response = new AlertDispatchRedriveResponse { Redriven = redriven };
        return Results.Json(
            ApiResponse<AlertDispatchRedriveResponse>.CreateSuccess(response, $"Redrove {redriven.ToString(CultureInfo.InvariantCulture)} dead-lettered dispatch row(s)."),
            AlertOpsAdminJsonContext.Default.ApiResponseAlertDispatchRedriveResponse);
    }

    private static async Task<IResult> HandleListChannelStates(
        [FromServices] IAlertDispatchStore store,
        CancellationToken cancellationToken)
    {
        var states = await store.GetChannelPauseStatesAsync(cancellationToken).ConfigureAwait(false);
        var payload = states
            .OrderBy(static pair => pair.Key.ToExternalName(), StringComparer.Ordinal)
            .Select(static pair => new AlertChannelStateResponse { Channel = pair.Key.ToExternalName(), Paused = pair.Value })
            .ToArray();

        return Results.Json(
            ApiResponse<AlertChannelStateResponse[]>.CreateSuccess(payload),
            AlertOpsAdminJsonContext.Default.ApiResponseAlertChannelStateResponseArray);
    }

    private static Task<IResult> HandlePauseChannel(
        string channel,
        [FromServices] IAlertDispatchStore store,
        [FromServices] IAuditLog auditLog,
        [FromServices] TimeProvider timeProvider,
        HttpContext context,
        CancellationToken cancellationToken)
        => SetChannelPausedAsync(channel, paused: true, store, auditLog, timeProvider, context, cancellationToken);

    private static Task<IResult> HandleResumeChannel(
        string channel,
        [FromServices] IAlertDispatchStore store,
        [FromServices] IAuditLog auditLog,
        [FromServices] TimeProvider timeProvider,
        HttpContext context,
        CancellationToken cancellationToken)
        => SetChannelPausedAsync(channel, paused: false, store, auditLog, timeProvider, context, cancellationToken);

    private static async Task<IResult> SetChannelPausedAsync(
        string channel,
        bool paused,
        IAlertDispatchStore store,
        IAuditLog auditLog,
        TimeProvider timeProvider,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!AlertChannelNames.TryParse(channel, out var channelType))
        {
            return BadRequest($"Unsupported alert channel '{channel}'.");
        }

        var now = timeProvider.GetUtcNow();
        await store.SetChannelPausedAsync(channelType, paused, now, cancellationToken).ConfigureAwait(false);

        await RecordAuditAsync(
            auditLog,
            context,
            action: paused ? "alerts.pause_channel" : "alerts.resume_channel",
            resourceType: "alert-channel",
            resourceId: channelType.ToExternalName(),
            details: new AlertOpsAuditDetails { Channel = channelType.ToExternalName(), Paused = paused },
            now,
            cancellationToken).ConfigureAwait(false);

        var response = new AlertChannelStateResponse { Channel = channelType.ToExternalName(), Paused = paused };
        return Results.Json(
            ApiResponse<AlertChannelStateResponse>.CreateSuccess(response, paused ? "Channel paused." : "Channel resumed."),
            AlertOpsAdminJsonContext.Default.ApiResponseAlertChannelStateResponse);
    }

    private static Task RecordAuditAsync(
        IAuditLog auditLog,
        HttpContext context,
        string action,
        string resourceType,
        string resourceId,
        AlertOpsAuditDetails details,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var actor = context.User?.Identity?.Name ?? AuditEvent.AnonymousActor;
        var auditEvent = new AuditEvent
        {
            Timestamp = now,
            EventType = AuditEventType.AdminAction,
            Actor = actor,
            ActorType = string.Equals(actor, AuditEvent.AnonymousActor, StringComparison.Ordinal)
                ? AuditActorType.Anonymous
                : AuditActorType.UserId,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Action = action,
            Outcome = AuditOutcome.Success,
            CorrelationId = context.TraceIdentifier,
            Details = JsonSerializer.Serialize(details, AlertOpsAdminJsonContext.Default.AlertOpsAuditDetails),
        };

        return auditLog.RecordAsync(auditEvent, cancellationToken);
    }

    private static IResult BadRequest(string message)
        => Results.Json(
            ApiResponse<object>.Failure(message),
            AlertOpsAdminJsonContext.Default.ApiResponseObject,
            statusCode: StatusCodes.Status400BadRequest);
}

/// <summary>Result of a dead-letter redrive.</summary>
public sealed record AlertDispatchRedriveResponse
{
    /// <summary>Number of dead-lettered dispatch rows re-enqueued.</summary>
    [JsonPropertyName("redriven")]
    public required int Redriven { get; init; }
}

/// <summary>Pause state of an alert delivery channel.</summary>
public sealed record AlertChannelStateResponse
{
    /// <summary>Canonical channel name.</summary>
    [JsonPropertyName("channel")]
    public required string Channel { get; init; }

    /// <summary>Whether delivery on the channel is paused.</summary>
    [JsonPropertyName("paused")]
    public required bool Paused { get; init; }
}

/// <summary>Structured audit detail for alert-ops admin actions.</summary>
internal sealed record AlertOpsAuditDetails
{
    /// <summary>Channel name for pause/resume actions.</summary>
    [JsonPropertyName("channel")]
    public string? Channel { get; init; }

    /// <summary>Pause flag for pause/resume actions.</summary>
    [JsonPropertyName("paused")]
    public bool? Paused { get; init; }

    /// <summary>Redriven row count for the redrive action.</summary>
    [JsonPropertyName("redriven")]
    public int? Redriven { get; init; }
}

/// <summary>Source-generated JSON context for the alert-ops admin endpoints.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ApiResponse<AlertDispatchRedriveResponse>))]
[JsonSerializable(typeof(ApiResponse<AlertChannelStateResponse>))]
[JsonSerializable(typeof(ApiResponse<AlertChannelStateResponse[]>))]
[JsonSerializable(typeof(AlertOpsAuditDetails))]
[JsonSerializable(typeof(ApiResponse<object>))]
internal sealed partial class AlertOpsAdminJsonContext : JsonSerializerContext;

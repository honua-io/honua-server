// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Ai.StudioAiProxy.Abstractions;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.RateLimiting;

namespace Honua.Server.Features.Studio.Ai;

/// <summary>
/// Studio AI proxy endpoints (honua-server#3000): a provider-neutral, server-mediated chat surface
/// so Studio clients never see model credentials. Admin-authorized in MVP — the same posture as the
/// Studio package lifecycle surface (<c>WorkflowPackageEndpoints</c>) — pending a dedicated
/// per-session Studio-user authorization scope.
/// </summary>
internal static class StudioAiProxyEndpoints
{
    public static void MapStudioAiProxyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/v{version:apiVersion}/studio/ai")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Studio", "AI")
            .RequireAdminAuthorization();

        group.MapGet("/capabilities", HandleGetCapabilities)
            .WithName("GetStudioAiCapabilities")
            .WithSummary("List the configured Studio AI proxy providers and their capabilities")
            .Produces<ApiResponse<StudioAiCapabilitiesResponse>>();

        group.MapPost("/chat", HandleChat)
            .WithName("StudioAiChat")
            .WithSummary("Proxy a streaming chat turn to a configured AI provider (SSE)")
            // Chat calls fan out to an upstream model provider and can be comparatively expensive;
            // 30/min keeps a single session from exhausting a shared provider budget while staying
            // well above realistic interactive usage. Mirrors the AdminAuthEndpoints precedent of an
            // explicit per-endpoint RateLimitAttribute for a sensitive/expensive surface.
            .WithMetadata(new RateLimitAttribute(30))
            .Accepts<StudioAiChatHttpRequest>("application/json")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .Produces(StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> HandleGetCapabilities(
        HttpContext context,
        IStudioAiProxyService service,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);
        var capabilities = await service.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        return Results.Json(
            ApiResponse<StudioAiCapabilitiesResponse>.CreateSuccess(capabilities),
            StudioAiProxyEndpointsJsonContext.Default.ApiResponseStudioAiCapabilitiesResponse);
    }

    private static async Task<IResult> HandleChat(
        HttpContext context,
        StudioAiChatHttpRequest httpRequest,
        IStudioAiProxyService service,
        IAuditLog auditLog,
        CancellationToken cancellationToken)
    {
        SetNoStore(context);

        var (domainRequest, mappingError) = StudioAiChatRequestMapper.ToDomain(httpRequest);
        if (mappingError is not null || domainRequest is null)
        {
            return BadRequest(context, mappingError ?? "Invalid request.");
        }

        var validationError = service.ValidateRequest(domainRequest);
        if (validationError is not null)
        {
            return BadRequest(context, validationError);
        }

        // Past this point the request names a real, configured provider, so it is safe to commit the
        // SSE response headers: nothing beyond here can still downgrade this into a JSON 4xx (SSE has
        // no way to change the status code after the first byte is written).
        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers.Expires = "0";
        response.Headers["X-Accel-Buffering"] = "no";

        var summary = new StudioAiProxyCallSummary();
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            await foreach (var evt in service
                .StreamChatAsync(domainRequest, summary, context.RequestAborted)
                .ConfigureAwait(false))
            {
                await WriteSseEventAsync(response, EventName(evt.Type), evt, context.RequestAborted).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Cancellation via request abort (REQ-001): the client disconnected or the browser tab
            // navigated away mid-stream. Not a provider failure — audited as a cancellation, not an
            // error.
            summary.ErrorMessage ??= "Client disconnected.";
            summary.StopReason ??= StudioAiStopReason.Cancelled;
        }
        catch (IOException)
        {
            summary.ErrorMessage ??= "Client disconnected.";
            summary.StopReason ??= StudioAiStopReason.Cancelled;
        }
        finally
        {
            // Always audit exactly once per call, even on client disconnect — use CancellationToken.None
            // so an aborted request's audit write is not itself cancelled.
            await RecordAuditAsync(auditLog, context, summary, startedAt, CancellationToken.None).ConfigureAwait(false);
        }

        return Results.Empty;
    }

    private static string EventName(StudioAiChatEventType type) => type switch
    {
        StudioAiChatEventType.MessageStart => "message_start",
        StudioAiChatEventType.TextDelta => "text_delta",
        StudioAiChatEventType.ToolCallStart => "tool_call_start",
        StudioAiChatEventType.ToolCallDelta => "tool_call_delta",
        StudioAiChatEventType.ToolCallStop => "tool_call_stop",
        StudioAiChatEventType.MessageStop => "message_stop",
        StudioAiChatEventType.Error => "error",
        _ => "message"
    };

    private static async Task WriteSseEventAsync(
        HttpResponse response,
        string eventName,
        StudioAiChatEvent value,
        CancellationToken cancellationToken)
    {
        await response.WriteAsync("event: ", cancellationToken).ConfigureAwait(false);
        await response.WriteAsync(eventName, cancellationToken).ConfigureAwait(false);
        await response.WriteAsync("\n", cancellationToken).ConfigureAwait(false);
        await response.WriteAsync("data: ", cancellationToken).ConfigureAwait(false);
        await JsonSerializer
            .SerializeAsync(response.Body, value, Honua.Ai.StudioAiProxy.StudioAiProxyJsonContext.Default.StudioAiChatEvent, cancellationToken)
            .ConfigureAwait(false);
        await response.WriteAsync("\n\n", cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Task RecordAuditAsync(
        IAuditLog auditLog,
        HttpContext context,
        StudioAiProxyCallSummary summary,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var actor = context.User?.Identity?.Name ?? AuditEvent.AnonymousActor;
        var details = new StudioAiProxyAuditDetails
        {
            Kind = summary.Kind,
            Model = summary.Model,
            PromptTokens = summary.PromptTokens,
            CompletionTokens = summary.CompletionTokens,
            LatencyMs = summary.LatencyMs,
            StopReason = summary.StopReason?.ToString(),
            ErrorMessage = summary.ErrorMessage
        };

        var auditEvent = new AuditEvent
        {
            Timestamp = startedAt,
            EventType = AuditEventType.AdminAction,
            Actor = actor,
            ActorType = string.Equals(actor, AuditEvent.AnonymousActor, StringComparison.Ordinal)
                ? AuditActorType.Anonymous
                : AuditActorType.UserId,
            ResourceType = "studio_ai_provider",
            ResourceId = string.IsNullOrEmpty(summary.Provider) ? null : summary.Provider,
            Action = "studio_ai.chat",
            Outcome = summary.Succeeded ? AuditOutcome.Success : AuditOutcome.Failure,
            CorrelationId = context.TraceIdentifier,
            Details = JsonSerializer.Serialize(
                details,
                Honua.Ai.StudioAiProxy.StudioAiProxyJsonContext.Default.StudioAiProxyAuditDetails)
        };

        return auditLog.RecordAsync(auditEvent, cancellationToken);
    }

    private static IResult BadRequest(HttpContext context, string detail)
        => ProblemDetailsHelpers.CreateAdminProblem(
            context,
            StatusCodes.Status400BadRequest,
            ProblemDetailsHelpers.GetTitle(StatusCodes.Status400BadRequest),
            detail);

    private static void SetNoStore(HttpContext context)
        => context.Response.Headers.CacheControl = "no-store";
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Infrastructure.Middleware;

/// <summary>
/// Bridges the per-request HTTP context to the audit log feature (#1144).
/// </summary>
/// <remarks>
/// <para>
/// This middleware is deliberately lightweight: it does NOT record an audit
/// event for every request (that is the job of metrics / access logs). Its
/// role is to (a) ensure <see cref="IAuditLog"/> is resolvable from the
/// request scope, and (b) emit a single <c>auth.failure</c> audit event when
/// the pipeline ultimately rejects a request with <c>401 Unauthorized</c> or
/// <c>403 Forbidden</c>. Individual handlers that perform security-relevant
/// actions (delete, export, config change, ...) call
/// <see cref="IAuditLog.RecordAsync"/> directly.
/// </para>
/// <para>
/// The 401/403 audit emit runs <i>after</i> the rest of the pipeline so it
/// observes the final status code, including codes set by downstream
/// authentication / authorization handlers.
/// </para>
/// </remarks>
internal sealed class AuditLogMiddleware(RequestDelegate next)
{
    private const string CorrelationIdHeader = "X-Correlation-ID";

    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        await _next(context).ConfigureAwait(false);

        // Only emit the auth-failure audit event when the response is a 401 or 403.
        // Other failure paths (404, 500, ...) are not security-relevant in the
        // forensic sense and are covered by metrics / logs.
        var status = context.Response.StatusCode;
        if (status != StatusCodes.Status401Unauthorized && status != StatusCodes.Status403Forbidden)
        {
            return;
        }

        var auditLog = context.RequestServices.GetService<IAuditLog>();
        if (auditLog is null)
        {
            return;
        }

        // Resolve the actor in best-effort fashion. If the request authenticated
        // (and was then 403'd) we still want the actor identity; otherwise it
        // is recorded as anonymous.
        var actor = ResolveActor(context, out var actorType);

        var auditEvent = new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventType = AuditEventType.Authentication,
            Actor = actor,
            ActorType = actorType,
            ResourceType = "http",
            ResourceId = context.Request.Path.HasValue ? context.Request.Path.Value : null,
            Action = "auth.failure",
            Outcome = status == StatusCodes.Status403Forbidden
                ? AuditOutcome.Denied
                : AuditOutcome.Failure,
            CorrelationId = ResolveCorrelationId(context),
            RemoteIp = context.Connection.RemoteIpAddress?.ToString(),
            UserAgent = context.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null,
            Details = $"{{\"status\":{status},\"method\":\"{context.Request.Method}\"}}",
        };

        try
        {
            await auditLog.RecordAsync(auditEvent, context.RequestAborted).ConfigureAwait(false);
        }
        catch
        {
            // IAuditLog implementations are expected to swallow their own errors;
            // we add a belt-and-braces catch here so the middleware never throws
            // *after* the response has been written.
        }
    }

    private static string ResolveActor(HttpContext context, out AuditActorType actorType)
    {
        var identity = context.User?.Identity;
        if (identity is { IsAuthenticated: true } && !string.IsNullOrWhiteSpace(identity.Name))
        {
            // For API-key authenticated callers the handler attaches an
            // "api_key_id" claim; prefer that as a stable actor identifier so
            // we never log the raw key name.
            var apiKeyId = context.User?.FindFirst("api_key_id")?.Value;
            if (!string.IsNullOrWhiteSpace(apiKeyId))
            {
                actorType = AuditActorType.ApiKey;
                return apiKeyId;
            }

            actorType = AuditActorType.UserId;
            return identity.Name;
        }

        actorType = AuditActorType.Anonymous;
        return AuditEvent.AnonymousActor;
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        if (context.Response.Headers.TryGetValue(CorrelationIdHeader, out var headerValue) &&
            !string.IsNullOrWhiteSpace(headerValue.ToString()))
        {
            return headerValue.ToString();
        }

        return string.IsNullOrWhiteSpace(context.TraceIdentifier)
            ? Guid.NewGuid().ToString("D")
            : context.TraceIdentifier;
    }
}

/// <summary>
/// Extension methods for registering <see cref="AuditLogMiddleware"/>.
/// </summary>
public static class AuditLogMiddlewareExtensions
{
    /// <summary>
    /// Register the audit-log middleware. Should be added after correlation-id
    /// middleware (so the audit event can stamp the request's correlation id)
    /// and after authentication so it can observe the resolved
    /// <see cref="HttpContext.User"/>.
    /// </summary>
    public static IApplicationBuilder UseHonuaAuditLog(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<AuditLogMiddleware>();
    }
}

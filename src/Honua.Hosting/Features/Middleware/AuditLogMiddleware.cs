// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.AuditLog.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.Middleware;

/// <summary>
/// Centralized, route-metadata-driven audit emitter (#507). Bridges the
/// per-request HTTP context to the audit log feature so that security-relevant
/// operations are recorded without each endpoint making manual audit calls.
/// </summary>
/// <remarks>
/// <para>
/// After the rest of the pipeline runs (so it observes the final status code and
/// resolved principal), the middleware:
/// </para>
/// <list type="bullet">
/// <item><description>
/// Looks up the matched endpoint's route template via
/// <see cref="IAuditActionResolver"/>. When the route is in the audit coverage
/// matrix (admin mutations, login, token issuance, ...) it emits the matching
/// event with the outcome derived from the response status code. This is how
/// "all admin API operations" and authentication events are audited centrally.
/// </description></item>
/// <item><description>
/// Independently emits an <c>auth.failure</c> / permission-denied event whenever
/// the pipeline rejects a request with <c>401</c> or <c>403</c> — even for routes
/// that are not otherwise in the matrix — so authorization failures are always
/// captured.
/// </description></item>
/// </list>
/// <para>
/// Destructive feature writes (delete / bulk edit) are emitted by the shared
/// edit-pipeline decorator rather than here, because protocols like WFS-T and
/// gRPC tunnel the operation through a single endpoint where the route does not
/// reveal it. Keeping that one concern in shared infrastructure ensures every
/// protocol adapter is covered consistently.
/// </para>
/// </remarks>
internal sealed class AuditLogMiddleware(RequestDelegate next, IAuditActionResolver actionResolver)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private readonly IAuditActionResolver _actionResolver = actionResolver ?? throw new ArgumentNullException(nameof(actionResolver));

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        await _next(context).ConfigureAwait(false);

        var auditLog = context.RequestServices.GetService<IAuditLog>();
        if (auditLog is null)
        {
            return;
        }

        var status = context.Response.StatusCode;
        var isAuthFailure = status is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden;

        var descriptor = ResolveDescriptor(context);

        // Nothing to audit: route is not in the matrix and the request did not
        // fail authentication/authorization.
        if (descriptor is null && !isAuthFailure)
        {
            return;
        }

        var auditEvent = descriptor is not null
            ? BuildMatrixEvent(context, descriptor, status, isAuthFailure)
            : BuildAuthFailureEvent(context, status);

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

    private AuditActionDescriptor? ResolveDescriptor(HttpContext context)
    {
        var routePattern = ResolveRoutePattern(context);
        if (routePattern is null)
        {
            return null;
        }

        var descriptor = _actionResolver.Resolve(context.Request.Method, routePattern);
        if (descriptor is null)
        {
            return null;
        }

        // Honour the descriptor's success policy: read-style descriptors only
        // emit on failure to avoid flooding the sink on every successful request.
        if (!descriptor.AuditOnSuccess && IsSuccessStatus(context.Response.StatusCode))
        {
            return null;
        }

        return descriptor;
    }

    private static AuditEvent BuildMatrixEvent(
        HttpContext context,
        AuditActionDescriptor descriptor,
        int status,
        bool isAuthFailure)
    {
        var outcome = isAuthFailure
            ? (status == StatusCodes.Status403Forbidden ? AuditOutcome.Denied : AuditOutcome.Failure)
            : (IsSuccessStatus(status) ? AuditOutcome.Success : AuditOutcome.Failure);

        return new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventType = descriptor.EventType,
            Actor = AuditContextResolver.ResolveActor(context, out var actorType),
            ActorType = actorType,
            ResourceType = descriptor.ResourceType,
            ResourceId = context.Request.Path.HasValue ? context.Request.Path.Value : null,
            Action = descriptor.Action,
            Outcome = outcome,
            CorrelationId = AuditContextResolver.ResolveCorrelationId(context),
            RemoteIp = AuditContextResolver.ResolveRemoteIp(context),
            UserAgent = AuditContextResolver.ResolveUserAgent(context),
            Details = BuildDetails(context, status),
        };
    }

    private static AuditEvent BuildAuthFailureEvent(HttpContext context, int status)
        => new()
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventType = status == StatusCodes.Status403Forbidden
                ? AuditEventType.Authorization
                : AuditEventType.Authentication,
            Actor = AuditContextResolver.ResolveActor(context, out var actorType),
            ActorType = actorType,
            ResourceType = "http",
            ResourceId = context.Request.Path.HasValue ? context.Request.Path.Value : null,
            Action = status == StatusCodes.Status403Forbidden ? "auth.denied" : "auth.failure",
            Outcome = status == StatusCodes.Status403Forbidden
                ? AuditOutcome.Denied
                : AuditOutcome.Failure,
            CorrelationId = AuditContextResolver.ResolveCorrelationId(context),
            RemoteIp = AuditContextResolver.ResolveRemoteIp(context),
            UserAgent = AuditContextResolver.ResolveUserAgent(context),
            Details = BuildDetails(context, status),
        };

    private static string BuildDetails(HttpContext context, int status)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"status\":{status},\"method\":\"{context.Request.Method}\"}}");

    private static bool IsSuccessStatus(int status) => status is >= 200 and < 300;

    private static string? ResolveRoutePattern(HttpContext context)
    {
        if (context.GetEndpoint() is RouteEndpoint routeEndpoint)
        {
            return routeEndpoint.RoutePattern.RawText;
        }

        return null;
    }
}

/// <summary>
/// Extension methods for registering <see cref="AuditLogMiddleware"/>.
/// </summary>
public static class AuditLogMiddlewareExtensions
{
    /// <summary>
    /// Register the audit-log middleware. Should be added after correlation-id
    /// middleware (so the audit event can stamp the request's correlation id),
    /// after routing (so the matched endpoint's route template is available), and
    /// after authentication / authorization so it can observe the resolved
    /// <see cref="HttpContext.User"/> and the final status code.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The application builder for chaining.</returns>
    public static IApplicationBuilder UseHonuaAuditLog(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<AuditLogMiddleware>();
    }
}

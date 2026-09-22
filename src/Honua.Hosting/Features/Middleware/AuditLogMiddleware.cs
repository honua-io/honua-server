// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Infrastructure.Models;
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
/// that are not otherwise in the matrix — unless a domain-specific endpoint seam
/// marked that it already recorded the final authorization denial.
/// </description></item>
/// <item><description>
/// Emits a failure event with the shared exception mapper's status when an audited operation throws, then rethrows so the
/// global exception handler still shapes the response. Without this the audit trail would
/// show nothing at all for the one class of admin mutation most worth recording.
/// </description></item>
/// </list>
/// <para>
/// Policy denials produced by the authorization middleware short-circuit *above* this
/// middleware and therefore never reach it; those are recorded at the authorization result
/// handler seam (<c>HonuaAuthorizationMiddlewareResultHandler</c>) using the same shared
/// event factory, so a denial is audited exactly once wherever it is decided.
/// </para>
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

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (Exception pipelineException) when (ShouldAuditFailure(context, pipelineException))
        {
            // The operation failed after it was admitted. Record the failure before the
            // exception leaves this middleware — the global exception handler runs further up
            // and maps the response the audit layer would otherwise never see.
            // The record is written with an independent token so a torn-down request still
            // leaves the row behind.
            var faultStatus = ExceptionMapper.Map(pipelineException).StatusCode;
            var faultDescriptor = ResolveDescriptor(context, faultStatus);
            if (faultDescriptor is not null)
            {
                await TryRecordAsync(
                    context,
                    HttpAuditEventFactory.CreateDescriptorEvent(
                        context,
                        faultDescriptor,
                        faultStatus,
                        DateTimeOffset.UtcNow),
                    CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        var status = context.Response.StatusCode;

        // Domain-specific authorization seams can emit a richer, stable denial event (resource,
        // operation, and code) before returning 403. Do not duplicate that decision with a
        // second generic auth.denied/matrix event.
        if (status == StatusCodes.Status403Forbidden &&
            AuditContextResolver.IsAuthorizationFailureAudited(context))
        {
            return;
        }

        var descriptor = ResolveDescriptor(context, status);

        // Nothing to audit: route is not in the matrix and the request did not
        // fail authentication/authorization.
        if (descriptor is null && !HttpAuditEventFactory.IsAuthRejection(status))
        {
            return;
        }

        var auditEvent = descriptor is not null
            ? HttpAuditEventFactory.CreateDescriptorEvent(context, descriptor, status, DateTimeOffset.UtcNow)
            : HttpAuditEventFactory.CreateAuthOutcomeEvent(context, status, DateTimeOffset.UtcNow, includeLineage: true);

        await TryRecordAsync(context, auditEvent, context.RequestAborted).ConfigureAwait(false);
    }

    // Only faults on a route the coverage matrix classifies are recorded: an unclassified
    // route's exception is already covered by logging/telemetry, and auditing every one of
    // them would let unauthenticated traffic drive audit volume.
    private bool ShouldAuditFailure(HttpContext context, Exception exception)
    {
        if (exception is OutOfMemoryException)
        {
            return false;
        }

        // A caller that hung up mid-request is not a security-relevant operation failure.
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
        {
            return false;
        }

        return context.RequestServices.GetService<IAuditLog>() is not null &&
            ResolveDescriptor(context, ExceptionMapper.Map(exception).StatusCode) is not null;
    }

    private static async Task TryRecordAsync(HttpContext context, AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        var auditLog = context.RequestServices.GetService<IAuditLog>();
        if (auditLog is null)
        {
            return;
        }

        try
        {
            await auditLog.RecordAsync(auditEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
        {
            // IAuditLog implementations are expected to swallow their own errors;
            // we add a belt-and-braces catch here so the middleware never throws
            // *after* the response has been written, and never replaces the
            // in-flight exception on the fault path.
        }
    }

    private AuditActionDescriptor? ResolveDescriptor(HttpContext context, int effectiveStatus)
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
        if (!descriptor.AuditOnSuccess && HttpAuditEventFactory.IsSuccessStatus(effectiveStatus))
        {
            return null;
        }

        return descriptor;
    }

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

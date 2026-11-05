// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.Middleware;

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

public static class AuditLogMiddlewareExtensions
{
    public static IApplicationBuilder UseHonuaAuditLog(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<AuditLogMiddleware>();
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Nodes;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.Middleware;

/// <summary>
/// Shared factory that turns an <see cref="HttpContext"/> plus an effective status code into
/// the canonical <see cref="AuditEvent"/> shapes used by the HTTP surface.
/// </summary>
/// <remarks>
/// <para>
/// Two emit sites need identical rows: <see cref="AuditLogMiddleware"/>, which observes the
/// request after the rest of the pipeline ran, and the authorization result handler, which
/// observes policy denials that short-circuit *before* the middleware is reached. Keeping the
/// projection here means a denial recorded from either site carries the same actor, resource,
/// correlation, and detail fields, so forensic queries do not have to know which seam wrote the
/// row.
/// </para>
/// </remarks>
internal static class HttpAuditEventFactory
{
    /// <summary>Resource family recorded for a transport-level authentication/authorization outcome.</summary>
    public const string HttpResourceType = "http";

    /// <summary>Action recorded when a request is rejected without an authenticated caller.</summary>
    public const string AuthFailureAction = "auth.failure";

    /// <summary>Action recorded when an authenticated caller is refused by policy.</summary>
    public const string AuthDeniedAction = "auth.denied";

    /// <summary>Whether the status code denotes a successful response.</summary>
    /// <param name="status">HTTP status code.</param>
    /// <returns><see langword="true"/> for 2xx.</returns>
    public static bool IsSuccessStatus(int status) => status is >= 200 and < 300;

    /// <summary>Whether the status code denotes an authentication/authorization rejection.</summary>
    /// <param name="status">HTTP status code.</param>
    /// <returns><see langword="true"/> for 401 and 403.</returns>
    public static bool IsAuthRejection(int status)
        => status is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden;

    /// <summary>
    /// Build the generic authentication-failure / permission-denied event for a request that was
    /// rejected with <c>401</c> or <c>403</c>.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="status">The rejection status code.</param>
    /// <param name="timestamp">The event timestamp.</param>
    /// <param name="code">Optional stable policy code recorded alongside the request details.</param>
    /// <returns>The audit event to record.</returns>
    public static AuditEvent CreateAuthOutcomeEvent(
        HttpContext context,
        int status,
        DateTimeOffset timestamp,
        string? code = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        var forbidden = status == StatusCodes.Status403Forbidden;
        return new AuditEvent
        {
            Timestamp = timestamp,
            EventType = forbidden ? AuditEventType.Authorization : AuditEventType.Authentication,
            Actor = AuditContextResolver.ResolveActor(context, out var actorType),
            ActorType = actorType,
            ResourceType = HttpResourceType,
            ResourceId = context.Request.Path.HasValue ? context.Request.Path.Value : null,
            Action = forbidden ? AuthDeniedAction : AuthFailureAction,
            Outcome = forbidden ? AuditOutcome.Denied : AuditOutcome.Failure,
            CorrelationId = AuditContextResolver.ResolveCorrelationId(context),
            RemoteIp = AuditContextResolver.ResolveRemoteIp(context),
            UserAgent = AuditContextResolver.ResolveUserAgent(context),
            Details = BuildDetails(context, status, code),
        };
    }

    /// <summary>
    /// Build the audit-coverage-matrix event for a route the resolver classified.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="descriptor">The matched matrix descriptor.</param>
    /// <param name="status">The effective status code for the request.</param>
    /// <param name="timestamp">The event timestamp.</param>
    /// <returns>The audit event to record.</returns>
    public static AuditEvent CreateDescriptorEvent(
        HttpContext context,
        AuditActionDescriptor descriptor,
        int status,
        DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(descriptor);

        var outcome = status switch
        {
            StatusCodes.Status403Forbidden => AuditOutcome.Denied,
            _ when IsSuccessStatus(status) => AuditOutcome.Success,
            _ => AuditOutcome.Failure,
        };

        return new AuditEvent
        {
            Timestamp = timestamp,
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
            Details = BuildDetails(context, status, code: null),
        };
    }

    private static string BuildDetails(HttpContext context, int status, string? code)
    {
        var details = new JsonObject
        {
            ["status"] = status,
            ["method"] = context.Request.Method,
            ["tenantId"] = context.RequestServices?.GetService<ITenantContext>()?.TenantId,
        };
        if (!string.IsNullOrWhiteSpace(code))
        {
            details["code"] = code;
        }

        AddHeader(details, context, "operationInstanceId", "X-Honua-Operation-Instance-Id");
        AddHeader(details, context, "acceptedAuditId", "X-Honua-Audit-Id");
        AddHeader(details, context, "proposalId", "X-Honua-Proposal-Id");
        return details.ToJsonString();
    }

    private static void AddHeader(JsonObject details, HttpContext context, string propertyName, string headerName)
    {
        if (context.Request.Headers.TryGetValue(headerName, out var values) && values.Count > 0)
        {
            details[propertyName] = values[0];
        }
    }
}

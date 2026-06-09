// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Honua.Infrastructure.Middleware;

/// <summary>
/// Shared helpers for deriving audit fields (actor, correlation id, remote ip,
/// user agent) from an <see cref="HttpContext"/>. Centralizes the actor-resolution
/// rules so every audit emit site — middleware and the shared edit-pipeline
/// decorator — records identical, non-secret-leaking values.
/// </summary>
internal static class AuditContextResolver
{
    private const string CorrelationIdHeader = "X-Correlation-ID";

    /// <summary>
    /// Resolve the actor identity and classification from the request principal.
    /// API-key callers are recorded by their stable hashed key id (never the raw
    /// key); authenticated users by user id; everything else as anonymous.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="actorType">The resolved actor classification.</param>
    /// <returns>The actor identifier.</returns>
    public static string ResolveActor(HttpContext context, out AuditActorType actorType)
    {
        ArgumentNullException.ThrowIfNull(context);

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

    /// <summary>
    /// Resolve the correlation id, preferring the response correlation header,
    /// falling back to the trace identifier or a fresh GUID.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>A non-empty correlation id.</returns>
    public static string ResolveCorrelationId(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Response.Headers.TryGetValue(CorrelationIdHeader, out var headerValue) &&
            !string.IsNullOrWhiteSpace(headerValue.ToString()))
        {
            return headerValue.ToString();
        }

        return string.IsNullOrWhiteSpace(context.TraceIdentifier)
            ? Guid.NewGuid().ToString("D")
            : context.TraceIdentifier;
    }

    /// <summary>Resolve the caller's remote IP, or <see langword="null"/>.</summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>The remote IP string or <see langword="null"/>.</returns>
    public static string? ResolveRemoteIp(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Connection.RemoteIpAddress?.ToString();
    }

    /// <summary>Resolve the caller's user agent, or <see langword="null"/>.</summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>The user agent string or <see langword="null"/>.</returns>
    public static string? ResolveUserAgent(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null;
    }
}

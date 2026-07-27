// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
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
    private static readonly object AuthorizationFailureAuditedKey = new();

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

        var principal = context.User;
        var identity = principal.Identity;
        if (identity is { IsAuthenticated: true })
        {
            // For API-key authenticated callers the handler attaches an
            // "api_key_id" claim; prefer that as a stable actor identifier so
            // we never log the raw key name.
            var apiKeyId = principal.FindFirst("api_key_id")?.Value;
            if (!string.IsNullOrWhiteSpace(apiKeyId))
            {
                actorType = AuditActorType.ApiKey;
                return apiKeyId;
            }

            // OIDC/JWT principals are valid with a stable subject claim even when
            // their token carries no display-name claim. Prefer the same stable
            // identifiers used by the Studio authorization service before falling
            // back to Identity.Name.
            var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrWhiteSpace(userId))
            {
                userId = principal.FindFirst("sub")?.Value;
            }
            if (string.IsNullOrWhiteSpace(userId))
            {
                userId = identity.Name;
            }
            if (string.IsNullOrWhiteSpace(userId))
            {
                userId = principal.FindFirst(ClaimTypes.Name)?.Value;
            }
            if (!string.IsNullOrWhiteSpace(userId))
            {
                actorType = AuditActorType.UserId;
                return userId;
            }
        }

        actorType = AuditActorType.Anonymous;
        return AuditEvent.AnonymousActor;
    }

    /// <summary>
    /// Marks a request whose final authorization denial has already been recorded by a
    /// domain-specific endpoint audit seam. The HTTP audit middleware uses this marker to avoid
    /// emitting a second, generic <c>auth.denied</c> event for the same decision.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    public static void MarkAuthorizationFailureAudited(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Items[AuthorizationFailureAuditedKey] = true;
    }

    /// <summary>
    /// Returns whether a domain-specific endpoint audit seam already recorded the request's
    /// authorization denial.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns><see langword="true"/> when the generic denial event must be suppressed.</returns>
    public static bool IsAuthorizationFailureAudited(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(AuthorizationFailureAuditedKey, out var value)
            && value is true;
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

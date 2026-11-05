// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Infrastructure.Security;
using Microsoft.AspNetCore.Http;

namespace Honua.Infrastructure.Middleware;

internal static class AuditContextResolver
{
    private const string CorrelationIdHeader = "X-Correlation-ID";
    private static readonly object AuthorizationFailureAuditedKey = new();

    // An API-key actor is recorded by the stable hashed key id, never the raw key.
    public static string ResolveActor(HttpContext context, out AuditActorType actorType)
    {
        ArgumentNullException.ThrowIfNull(context);

        var principal = context.User;
        var identity = principal.Identity;
        if (identity is { IsAuthenticated: true })
        {
            // Tenant resolution stamps the framework-owned, scheme/issuer-qualified
            // actor on the principal. Prefer it so MCP transport, durable jobs,
            // policy context, and audit records name the same immutable actor.
            var canonicalActor = CanonicalSecurityActor.FindStampedValue(
                principal,
                CanonicalSecurityActor.CanonicalActorClaim);
            if (!string.IsNullOrWhiteSpace(canonicalActor))
            {
                actorType = string.Equals(
                    identity.AuthenticationType,
                    Honua.Infrastructure.Authentication.AuthenticationExtensions.ApiKeyScheme,
                    StringComparison.OrdinalIgnoreCase)
                    ? AuditActorType.ApiKey
                    : AuditActorType.UserId;
                return canonicalActor;
            }

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

    public static void MarkAuthorizationFailureAudited(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Items[AuthorizationFailureAuditedKey] = true;
    }

    public static bool IsAuthorizationFailureAudited(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(AuthorizationFailureAuditedKey, out var value)
            && value is true;
    }

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

    public static string? ResolveRemoteIp(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Connection.RemoteIpAddress?.ToString();
    }

    public static string? ResolveUserAgent(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null;
    }
}

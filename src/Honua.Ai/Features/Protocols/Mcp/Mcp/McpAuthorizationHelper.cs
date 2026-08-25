// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Security;

namespace Honua.Ai.Protocols.Mcp;

/// <summary>
/// Shared authentication helpers for MCP tool and resource handlers.
/// Tools delegate to <see cref="Geoprocessing.IGeoprocessingJobService.EnsureCallerAuthorizedAsync"/>
/// for operator-grant checks; this helper adapts the MCP request context into
/// the same <see cref="ClaimsPrincipal"/> shape the domain service expects.
/// </summary>
internal static class McpAuthorizationHelper
{
    /// <summary>
    /// Resolves the caller's <see cref="ClaimsPrincipal"/> from the HTTP context.
    /// Throws <see cref="Geoprocessing.GeoprocessingAuthorizationException"/> when
    /// the request does not carry an authenticated principal so that authentication
    /// errors flow through the same exception channel as domain auth failures.
    /// </summary>
    public static ClaimsPrincipal EnsurePrincipal(HttpContext context)
    {
        var principal = context.User;
        if (principal.Identity is null || !principal.Identity.IsAuthenticated)
        {
            throw new Geoprocessing.GeoprocessingAuthorizationException(requiresAuthentication: true);
        }

        return principal;
    }

    /// <summary>
    /// Resolves the stable principal key an MCP session is bound to at
    /// <c>initialize</c> and re-checked on every subsequent request (A3 session
    /// binding; honua-server#2537). Returns
    /// <see cref="McpSessionManager.AnonymousPrincipalKey"/> for an unauthenticated
    /// caller — this mirrors the existing endpoint auth posture (the surface allows
    /// anonymous handshake methods; a session established anonymously stays
    /// anonymous) and never invents a new authentication requirement. For an
    /// authenticated caller the key uses the canonical, scheme-qualified actor
    /// identity (including issuer for bearer subjects and immutable ID for API keys).
    /// </summary>
    public static string ResolvePrincipalKey(ClaimsPrincipal? principal)
    {
        return CanonicalSecurityActor.Resolve(principal)?.ActorId
            ?? McpSessionManager.AnonymousPrincipalKey;
    }

    /// <summary>
    /// Resolves the immutable MCP session binding from the framework-authenticated
    /// actor, the effective tenant selected by tenant policy, and the OAuth scope
    /// ceiling. Client-supplied issuer, subject, tenant, and scope values are never
    /// read from request headers or parameters.
    /// </summary>
    public static string ResolveSessionBindingKey(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var actor = CanonicalSecurityActor.Resolve(context.User);
        if (actor is null)
        {
            return McpSessionManager.AnonymousPrincipalKey;
        }

        var tenant = context.RequestServices.GetService<ITenantContext>()?.TenantId
            ?? context.User.FindFirstValue(CanonicalSecurityActor.EffectiveTenantClaim);
        return CanonicalSecurityActor.BuildBindingKey(actor, tenant, context.User);
    }

    /// <summary>
    /// Rejects a bearer-authenticated tool call when tenant policy did not resolve
    /// an effective tenant. Discovery remains available, but no tool implementation
    /// may observe the deployment's default database/schema in this state.
    /// </summary>
    public static void EnsureBearerToolTenant(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!CanonicalSecurityActor.IsBearerPrincipal(context.User))
        {
            return;
        }

        var tenant = context.RequestServices.GetService<ITenantContext>()?.TenantId;
        if (string.IsNullOrWhiteSpace(tenant))
        {
            throw new Geoprocessing.GeoprocessingAuthorizationException(
                requiresAuthentication: false,
                "A validated tenant is required to invoke MCP tools.");
        }
    }
}

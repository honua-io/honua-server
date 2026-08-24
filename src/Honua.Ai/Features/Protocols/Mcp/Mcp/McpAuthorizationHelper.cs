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
    private const string AnonymousPrincipalScheme = "anonymous";

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
    /// authenticated caller the key prefixes the authentication scheme to the
    /// stable subject/name-identifier claim, falling back to the identity name.
    /// </summary>
    public static string ResolvePrincipalKey(ClaimsPrincipal? principal)
    {
        var identity = ResolveAuthenticatedIdentity(principal);
        if (identity is null || !identity.IsAuthenticated)
        {
            return McpSessionManager.AnonymousPrincipalKey;
        }

        var scheme = ResolveScheme(identity);
        var subject = principal!.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(subject))
        {
            return $"{scheme}:sub:{subject}";
        }

        return string.IsNullOrEmpty(identity.Name)
            ? $"{scheme}:authenticated"
            : $"{scheme}:name:{identity.Name}";
    }

    private static ClaimsIdentity? ResolveAuthenticatedIdentity(ClaimsPrincipal? principal) =>
        principal?.Identity?.IsAuthenticated == true
            ? principal.Identity as ClaimsIdentity
            : principal?.Identities.FirstOrDefault(candidate => candidate.IsAuthenticated);

    private static string ResolveScheme(ClaimsIdentity identity) =>
        string.IsNullOrWhiteSpace(identity.AuthenticationType)
            ? AnonymousPrincipalScheme
            : identity.AuthenticationType;

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
}

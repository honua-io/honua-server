// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Operations.Policy;
using Honua.Infrastructure.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Honua.Infrastructure.Authentication;

internal sealed class AgentOriginFeature
{
    internal static readonly AgentOriginFeature Instance = new();

    private AgentOriginFeature()
    {
    }
}

// An agent is any caller on the MCP route, an API key that carries the "agent" grant, or a
// request carrying an attested agent-origin feature. Recording the origin on HttpContext.Features
// ensures replacing context.User during authorization cannot drop the marker.
internal static class AgentPrincipal
{
    public const string AgentGrant = "agent";

    private const string McpRoutePath = "/mcp";

    public static bool IsAgent(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return HasAgentRole(principal) || HasAgentGrant(principal);
    }

    public static bool IsAgent(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return HasAgentOrigin(context) || IsAgent(context.User);
    }

    public static bool HasAgentOrigin(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Features.Get<AgentOriginFeature>() is not null;
    }

    public static void MarkAgentOrigin(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Features.Set(AgentOriginFeature.Instance);
        MarkAgent(context.User);
    }

    public static void ClearAgentOrigin(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Features.Set<AgentOriginFeature>(null);
    }

    // The policy context's roles, with the agent role whenever the principal is an agent, so the
    // operation policy and the approval gate classify the caller the same way.
    public static string[] EffectiveRoles(ClaimsPrincipal principal)
    {
        var roles = principal.FindAll(ClaimTypes.Role).Select(static claim => claim.Value).Distinct(StringComparer.Ordinal);
        return IsAgent(principal) ? [.. roles.Append(AgentApprovalRule.AgentRole).Distinct(StringComparer.OrdinalIgnoreCase)] : [.. roles];
    }

    public static string[] EffectiveRoles(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var roles = EffectiveRoles(context.User);
        return HasAgentOrigin(context)
            ? [.. roles.Append(AgentApprovalRule.AgentRole).Distinct(StringComparer.OrdinalIgnoreCase)]
            : roles;
    }

    public static bool IsAgentRequest(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return HasAgentOrigin(context)
            || context.Request.Path.StartsWithSegments(McpRoutePath, StringComparison.OrdinalIgnoreCase)
            || IsAgent(context.User);
    }

    public static void MarkRequestPrincipal(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!IsAgentRequest(context))
        {
            return;
        }

        MarkAgentOrigin(context);
    }

    // Marks an authenticated principal as an agent; the operation loopback uses it for a hop whose
    // attestation says the original caller was an agent.
    public static void MarkAgent(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity?.IsAuthenticated != true || HasAgentRole(principal))
        {
            return;
        }

        principal.AddIdentity(new ClaimsIdentity([CanonicalSecurityActor.CreateStampedClaim(ClaimTypes.Role, AgentApprovalRule.AgentRole)]));
    }

    public static IApplicationBuilder UseAgentPrincipalMarking(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            MarkRequestPrincipal(context);
            using var writeGuard = AgentFeatureWriteGuard.Begin(() => IsUnapprovedAgentFeatureWrite(context));
            await next(context).ConfigureAwait(false);
        });
    }

    internal static bool IsUnapprovedAgentFeatureWrite(HttpContext context)
        => IsAgent(context) && !AdminApiKeyPermission.IsServerMintedApprovedOperation(context.User);

    private static bool HasAgentRole(ClaimsPrincipal principal)
        => AgentApprovalRule.IsAgent([.. principal.Identities
            .SelectMany(static identity => identity.FindAll(identity.RoleClaimType).Concat(identity.FindAll(ClaimTypes.Role)))
            .Select(static claim => claim.Value)]);

    private static bool HasAgentGrant(ClaimsPrincipal principal)
        => principal.FindAll(AdminApiKeyPermission.PermissionClaimType)
            .Any(static claim => string.Equals(claim.Value?.Trim(), AgentGrant, StringComparison.OrdinalIgnoreCase));
}

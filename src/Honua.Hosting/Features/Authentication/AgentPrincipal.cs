// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Operations.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Honua.Infrastructure.Authentication;

// An agent is any caller on the MCP route, or an API key that carries the "agent" grant. Marking
// the principal with the agent role lets the operator approval gate and the operation policy see
// the same identity on every path, including the in-process MCP dispatch.
internal static class AgentPrincipal
{
    public const string AgentGrant = "agent";

    private const string McpRoutePath = "/mcp";

    public static bool IsAgent(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return HasAgentRole(principal) || HasAgentGrant(principal);
    }

    // The policy context's roles, with the agent role whenever the principal is an agent, so the
    // operation policy and the approval gate classify the caller the same way.
    public static string[] EffectiveRoles(ClaimsPrincipal principal)
    {
        var roles = principal.FindAll(ClaimTypes.Role).Select(static claim => claim.Value).Distinct(StringComparer.Ordinal);
        return IsAgent(principal) ? [.. roles.Append(AgentApprovalRule.AgentRole).Distinct(StringComparer.OrdinalIgnoreCase)] : [.. roles];
    }

    public static bool IsAgentRequest(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Request.Path.StartsWithSegments(McpRoutePath, StringComparison.OrdinalIgnoreCase)
            || IsAgent(context.User);
    }

    public static void MarkRequestPrincipal(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var principal = context.User;
        if (principal.Identity?.IsAuthenticated != true
            || HasAgentRole(principal)
            || !IsAgentRequest(context))
        {
            return;
        }

        principal.AddIdentity(new ClaimsIdentity([new Claim(ClaimTypes.Role, AgentApprovalRule.AgentRole)]));
    }

    public static IApplicationBuilder UseAgentPrincipalMarking(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            MarkRequestPrincipal(context);
            await next(context).ConfigureAwait(false);
        });
    }

    private static bool HasAgentRole(ClaimsPrincipal principal)
        => AgentApprovalRule.IsAgent([.. principal.Identities
            .SelectMany(static identity => identity.FindAll(identity.RoleClaimType).Concat(identity.FindAll(ClaimTypes.Role)))
            .Select(static claim => claim.Value)]);

    private static bool HasAgentGrant(ClaimsPrincipal principal)
        => principal.FindAll(AdminApiKeyPermission.PermissionClaimType)
            .Any(static claim => string.Equals(claim.Value?.Trim(), AgentGrant, StringComparison.OrdinalIgnoreCase));
}

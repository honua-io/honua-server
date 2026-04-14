// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;

namespace Honua.Server.Features.Infrastructure.Authentication;

/// <summary>
/// Evaluates layered service and layer access policies.
/// </summary>
internal sealed class AccessPolicyEvaluator : IAccessPolicyEvaluator
{
    /// <inheritdoc />
    public Task<AccessDecision> EvaluateAsync(ClaimsPrincipal principal, string resource, string action)
        => Task.FromResult(Evaluate(principal, resource, action));

    /// <inheritdoc />
    public AccessDecision Evaluate(ClaimsPrincipal principal, string resource, string action)
    {
        var requiresAuthentication = !string.IsNullOrWhiteSpace(resource) || !string.IsNullOrWhiteSpace(action);
        if (!requiresAuthentication)
        {
            return AccessDecision.Allowed();
        }

        return principal.Identity?.IsAuthenticated == true
            ? AccessDecision.Allowed()
            : AccessDecision.RequiresAuth("Authentication is required.");
    }

    /// <inheritdoc />
    public Task<AccessDecision> EvaluateAsync(
        ClaimsPrincipal principal,
        AccessPolicy? layerPolicy,
        AccessPolicy? servicePolicy,
        object? scope = null)
        => Task.FromResult(Evaluate(principal, layerPolicy, servicePolicy, scope));

    /// <inheritdoc />
    public AccessDecision Evaluate(
        ClaimsPrincipal principal,
        AccessPolicy? layerPolicy,
        AccessPolicy? servicePolicy,
        object? scope = null)
    {
        var requiresWrite = IsWriteScope(scope);

        var layerDecision = EvaluateSinglePolicy(principal, layerPolicy, requiresWrite);
        var serviceDecision = EvaluateSinglePolicy(principal, servicePolicy, requiresWrite);

        if (layerDecision is null && serviceDecision is null)
        {
            return principal.Identity?.IsAuthenticated == true
                ? AccessDecision.Allowed()
                : AccessDecision.RequiresAuth("Authentication is required.");
        }

        if (layerDecision is { IsAllowed: false })
        {
            return layerDecision.Value;
        }

        if (serviceDecision is { IsAllowed: false })
        {
            return serviceDecision.Value;
        }

        return AccessDecision.Allowed();
    }

    private static AccessDecision? EvaluateSinglePolicy(ClaimsPrincipal principal, AccessPolicy? policy, bool requiresWrite)
    {
        if (policy is null)
        {
            return null;
        }

        var allowAnonymous = requiresWrite ? policy.AllowAnonymousWrite : policy.AllowAnonymous;
        if (allowAnonymous)
        {
            return AccessDecision.Allowed();
        }

        if (principal.Identity?.IsAuthenticated != true)
        {
            return AccessDecision.RequiresAuth("Authentication is required.");
        }

        var allowedRoles = requiresWrite
            ? policy.AllowedWriteRoles ?? policy.AllowedRoles
            : policy.AllowedRoles;

        if (allowedRoles is { Length: > 0 } && !IsInAnyRole(principal, allowedRoles))
        {
            return AccessDecision.Forbidden("User does not have the required role.");
        }

        return AccessDecision.Allowed();
    }

    private static bool IsWriteScope(object? scope)
    {
        return scope switch
        {
            null => false,
            int numeric => numeric != 0,
            string text => string.Equals(text, "write", StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(scope.ToString(), "Write", StringComparison.OrdinalIgnoreCase)
        };
    }

    private static bool IsInAnyRole(ClaimsPrincipal principal, string[] allowedRoles)
    {
        foreach (var allowedRole in allowedRoles)
        {
            if (string.IsNullOrWhiteSpace(allowedRole))
            {
                continue;
            }

            var trimmedRole = allowedRole.Trim();
            if (principal.Claims.Any(claim =>
                    claim.Type == ClaimTypes.Role &&
                    string.Equals(claim.Value, trimmedRole, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (principal.IsInRole(trimmedRole))
            {
                return true;
            }
        }

        return false;
    }
}

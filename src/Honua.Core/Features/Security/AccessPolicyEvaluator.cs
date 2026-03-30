// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Security.Abstractions;

namespace Honua.Core.Features.Security;

/// <summary>
/// Evaluates catalog access policies against user claims.
/// </summary>
public sealed class AccessPolicyEvaluator : IAccessPolicyEvaluator
{
    /// <inheritdoc />
    public AccessDecision Evaluate(
        ClaimsPrincipal principal,
        AccessPolicy? layerPolicy,
        AccessPolicy? servicePolicy,
        AccessScope scope = AccessScope.Read)
    {
        var layerDecision = EvaluatePolicy(principal, layerPolicy, scope);
        var serviceDecision = EvaluatePolicy(principal, servicePolicy, scope);

        if (layerDecision is null && serviceDecision is null)
        {
            return principal?.Identity?.IsAuthenticated == true
                ? AccessDecision.Allowed()
                : AccessDecision.RequiresAuth("Authentication is required.");
        }

        if (layerDecision is not null && !layerDecision.IsAllowed)
        {
            return layerDecision;
        }

        if (serviceDecision is not null && !serviceDecision.IsAllowed)
        {
            return serviceDecision;
        }

        return AccessDecision.Allowed();
    }

    private static AccessDecision? EvaluatePolicy(
        ClaimsPrincipal principal,
        AccessPolicy? policy,
        AccessScope scope)
    {
        if (policy is null)
        {
            return null;
        }

        var allowAnonymous = scope == AccessScope.Read
            ? policy.AllowAnonymous
            : policy.AllowAnonymousWrite;

        if (allowAnonymous)
        {
            return AccessDecision.Allowed();
        }

        if (principal?.Identity?.IsAuthenticated != true)
        {
            return AccessDecision.RequiresAuth("Authentication is required.");
        }

        var allowedRoles = scope == AccessScope.Read
            ? policy.AllowedRoles
            : policy.AllowedWriteRoles ?? policy.AllowedRoles;

        if (allowedRoles is { Length: > 0 } && !IsInAnyRole(principal, allowedRoles))
        {
            return AccessDecision.Forbidden("User does not have the required role.");
        }

        return AccessDecision.Allowed();
    }

    private static bool IsInAnyRole(ClaimsPrincipal principal, string[] allowedRoles)
    {
        foreach (var role in allowedRoles)
        {
            if (principal.IsInRole(role))
            {
                return true;
            }
        }

        return false;
    }
}

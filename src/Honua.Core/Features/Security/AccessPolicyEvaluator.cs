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
        var policy = layerPolicy ?? servicePolicy ?? new AccessPolicy { AllowAnonymous = false };

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

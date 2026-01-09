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
        AccessPolicy? servicePolicy)
    {
        var policy = layerPolicy ?? servicePolicy ?? new AccessPolicy { AllowAnonymous = false };

        if (policy.AllowAnonymous)
        {
            return AccessDecision.Allowed();
        }

        if (principal?.Identity?.IsAuthenticated != true)
        {
            return AccessDecision.RequiresAuth("Authentication is required.");
        }

        if (policy.AllowedRoles is { Length: > 0 } && !IsInAnyRole(principal, policy.AllowedRoles))
        {
            return AccessDecision.Forbidden("User does not have the required role.");
        }

        if (policy.AllowedTenants is { Length: > 0 } &&
            !IsInAllowedTenant(principal, policy.TenantClaimType, policy.AllowedTenants))
        {
            return AccessDecision.Forbidden("User does not belong to an allowed tenant.");
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

    private static bool IsInAllowedTenant(
        ClaimsPrincipal principal,
        string? claimType,
        string[] allowedTenants)
    {
        var effectiveClaimType = string.IsNullOrWhiteSpace(claimType) ? "tenant_id" : claimType;
        var tenantId = principal.FindFirst(effectiveClaimType)?.Value;

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return false;
        }

        foreach (var allowed in allowedTenants)
        {
            if (string.Equals(tenantId, allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

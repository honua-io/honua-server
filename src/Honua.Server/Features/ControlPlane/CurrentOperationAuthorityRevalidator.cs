// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;

namespace Honua.ControlPlane;

/// <summary>
/// Re-evaluates the retained proposer through the current durable grant store,
/// OAuth scope policy, exact resource binding, and edition ladder.
/// </summary>
internal sealed class CurrentOperationAuthorityRevalidator(
    IOperatorAuthorizationEvaluator authorization,
    IOperatorScopeAuthorizer scopeAuthorizer,
    IGuardrailLadder ladder) : IOperationAuthorityRevalidator
{
    public async Task<OperationAuthorityRevalidationResult> RevalidateAsync(
        OperationProposal proposal,
        CancellationToken cancellationToken = default)
    {
        var authority = proposal.Authority;
        if (authority?.ResourceType is not { } resourceType ||
            authority.Operation is not { } operation ||
            string.IsNullOrWhiteSpace(authority.ResourceId) ||
            string.IsNullOrWhiteSpace(authority.EffectiveTenant))
        {
            return OperationAuthorityRevalidationResult.Denied(
                "retained tenant, resource, and operation evidence is incomplete");
        }

        if (ladder.Resolve(proposal.Kind).Tier == GuardrailTier.Blocked)
        {
            return OperationAuthorityRevalidationResult.Denied("the current edition blocks this operation");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, authority.Actor),
            new("iss", authority.Issuer),
        };
        claims.AddRange(authority.ScopeCeiling.Select(scope =>
            new Claim(OperatorScopeCatalog.ScopeClaimType, scope)));
        claims.AddRange(authority.PermissionCeiling.Select(permission =>
            new Claim("permission", permission)));
        claims.AddRange(authority.RoleCeiling.Select(role => new Claim(ClaimTypes.Role, role)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            authority.Scheme,
            ClaimTypes.NameIdentifier,
            ClaimTypes.Role));

        var grant = await authorization.EvaluateAsync(
            principal,
            new OperatorAuthorizationRequest
            {
                ResourceType = resourceType,
                ResourceId = authority.ResourceId,
                Operation = operation,
            },
            cancellationToken).ConfigureAwait(false);
        if (!grant.IsAllowed)
        {
            return OperationAuthorityRevalidationResult.Denied(grant.FailureReason ?? "current grant denied");
        }

        var scope = scopeAuthorizer.Evaluate(principal, resourceType, operation);
        return scope.IsAllowed
            ? OperationAuthorityRevalidationResult.Allowed()
            : OperationAuthorityRevalidationResult.Denied(scope.Reason ?? "current OAuth scope denied");
    }
}

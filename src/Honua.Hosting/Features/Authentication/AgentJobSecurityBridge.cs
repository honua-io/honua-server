// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Security;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Credential-aware durable identity primitives. Policy values are never promoted into
/// the principal that authorizes execution. Callers must adopt this bridge explicitly;
/// the legacy synchronous agent capture and restore paths remain closed.
/// </summary>
internal static class AgentJobSecurityBridge
{
    private const int MaxPolicyClaims = 256;
    private const int MaxPolicyClaimTypeBytes = 512;
    private const int MaxPolicyClaimValueBytes = 16384;
    private const int MaxPolicyClaimBytes = 65536;
    private static readonly HashSet<string> SecretClaimTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "access_token", "id_token", "refresh_token", "client_secret", "password"
    };

    internal static async Task<JobSecurityContext> CaptureAsync(
        ClaimsPrincipal principal,
        RbacOptions options,
        IServiceProvider services,
        ITenantContext? tenantContext,
        CancellationToken cancellationToken)
    {
        if (!AgentCredentialAuthority.IsAgent(principal))
        {
            throw new UnauthorizedAccessException("Credential-aware agent capture requires an agent credential.");
        }

        var credential = await AgentCredentialAuthority.CaptureAsync(principal, services, cancellationToken).ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("The agent credential is no longer current.");
        if (credential.Kind == "api-key")
        {
            var expiry = await AgentCredentialAuthority.ExecutionDeadlineAsync(credential, services, cancellationToken).ConfigureAwait(false);
            credential = credential with { TokenExpiresAt = expiry ?? DateTimeOffset.MaxValue };
        }
        var current = await AgentCredentialAuthority.RevalidateAsync(credential, services, cancellationToken).ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("The agent credential changed during durable capture.");
        var workspaceOwner = ResolveWorkspaceOwner(current);
        var actor = CanonicalSecurityActor.Resolve(principal)?.ActorId
            ?? throw new UnauthorizedAccessException("The agent credential has no canonical actor.");
        var tenant = JobSecurityContextCapture.ResolveTenantId(principal, tenantContext);
        if (!AgentCredentialAuthority.MatchesTenant(principal, tenant))
        {
            throw new UnauthorizedAccessException("The agent credential does not match the execution tenant.");
        }

        // These values are only inputs to parameterized RLS predicates. Their issuers,
        // properties and authentication provenance are intentionally not persisted.
        // Current roles come from the credential authority, never this list.
        var roleTypes = new[] { ClaimTypes.Role, RbacOptions.DefaultRoleClaimType, options.EffectiveRoleClaimType }
            .Concat(principal.Identities.Select(identity => identity.RoleClaimType))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        ValidatePolicyClaims(roleTypes.Select(type => new JobSecurityClaim(type, string.Empty)).ToArray());
        var claims = principal.Claims
            .Where(claim => !CanonicalSecurityActor.IsFrameworkOwnedClaim(claim)
                && !SecretClaimTypes.Contains(claim.Type)
                && !roleTypes.Contains(claim.Type, StringComparer.OrdinalIgnoreCase))
            .Select(claim => new JobSecurityClaim(claim.Type, claim.Value))
            .Distinct()
            .ToArray();
        ValidatePolicyClaims(claims);
        return new JobSecurityContext(actor, tenant, claims, options.EffectiveRoleClaimType)
        {
            OwnerActorId = actor,
            WorkspaceOwnerId = workspaceOwner,
            AgentCredential = credential,
            AgentPolicyRoleClaimTypes = roleTypes
        };
    }

    internal static async Task<JobSecurityContext> RevalidateAsync(
        JobSecurityContext context, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (context.ExecutionLease is not null)
        {
            var principal = context.CurrentAgentPrincipal
                ?? Honua.Core.Features.Authorization.JobSecurityScope.Current?.Submitter?.CurrentAgentPrincipal;
            if (principal is null || !JobExecutionLeaseTransientAuthority.IsLeasePrincipal(principal)
                || !await JobExecutionLeaseTransientAuthority.IsCurrentAsync(principal, cancellationToken).ConfigureAwait(false)
                || Honua.Core.Features.Authorization.JobSecurityScope.Current?.Submitter is not { } scoped
                || scoped.ExecutionLease != context.ExecutionLease
                || JobExecutionLeaseIntentFingerprint.ComputeContext(scoped) != JobExecutionLeaseIntentFingerprint.ComputeContext(context))
            {
                throw new UnauthorizedAccessException("The lease requires current authority inside its actual job execution scope.");
            }
            return context with { CurrentAgentPrincipal = principal, CredentialExpiresAt = context.ExecutionLease.ExpiresAt };
        }
        var credential = context.AgentCredential
            ?? throw new UnauthorizedAccessException("The job has no typed agent credential binding.");
        var current = await AgentCredentialAuthority.RevalidateAsync(credential, services, cancellationToken).ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("The job's agent credential is no longer current.");
        var actor = CanonicalSecurityActor.Resolve(current)?.ActorId;
        if (actor is null || !string.Equals(actor, context.OwnerActorId, StringComparison.Ordinal)
            || !string.Equals(actor, context.PrincipalId, StringComparison.Ordinal)
            || !string.Equals(context.WorkspaceOwnerId, ResolveWorkspaceOwner(current), StringComparison.Ordinal)
            || !AgentCredentialAuthority.MatchesTenant(current, context.TenantId))
        {
            throw new UnauthorizedAccessException("The job credential does not match its sealed owner and tenant.");
        }

        var expiresAt = await AgentCredentialAuthority.ExecutionDeadlineAsync(credential, services, cancellationToken).ConfigureAwait(false);
        return context with { CurrentAgentPrincipal = current, CredentialExpiresAt = expiresAt };
    }

    internal static async Task<ClaimsPrincipal> ResolveExecutionPrincipalAsync(
        JobSecurityContext context, IServiceProvider services, CancellationToken cancellationToken)
    {
        var validated = await RevalidateAsync(context, services, cancellationToken).ConfigureAwait(false);
        return validated.CurrentAgentPrincipal!;
    }

    /// <summary>
    /// Builds an unauthenticated principal exclusively for RLS and field-mask evaluation.
    /// Revalidation is repeated for each read; no authorization consumer may use this result.
    /// </summary>
    internal static async Task<ClaimsPrincipal> ResolvePolicyPrincipalAsync(
        JobSecurityContext context, IServiceProvider services, CancellationToken cancellationToken)
    {
        await RevalidateAsync(context, services, cancellationToken).ConfigureAwait(false);
        var roles = context.AgentCredential!.MembershipRoles;
        var roleType = context.RoleClaimType ?? ClaimTypes.Role;
        ValidatePolicyClaims(context.Claims);
        var roleTypes = new[] { ClaimTypes.Role, RbacOptions.DefaultRoleClaimType, roleType }
            .Concat(context.AgentPolicyRoleClaimTypes)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        ValidatePolicyClaims(roleTypes.Select(type => new JobSecurityClaim(type, string.Empty)).ToArray());
        var claims = context.Claims
            .Where(claim => !SecretClaimTypes.Contains(claim.Type)
                && !roleTypes.Contains(claim.Type, StringComparer.OrdinalIgnoreCase))
            .Select(claim => new Claim(claim.Type, claim.Value))
            .Concat(roleTypes.SelectMany(type => roles.Select(role => new Claim(type, role))))
            .ToArray();
        // No framework-owned claim is copied, and AuthenticationType remains null.
        // The actual current principal remains separate on the transient context.
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: null, ClaimTypes.Name, roleType));
    }
    private static string ResolveWorkspaceOwner(ClaimsPrincipal current)
        => current.FindFirstValue(ClaimTypes.NameIdentifier) ?? current.FindFirstValue("sub")
            ?? current.FindFirstValue("api_key_id")
            ?? throw new UnauthorizedAccessException("The current credential has no stable workspace owner.");

    private static void ValidatePolicyClaims(IReadOnlyList<JobSecurityClaim> claims)
    {
        if (claims.Count > MaxPolicyClaims)
        {
            throw new UnauthorizedAccessException("The agent policy input count exceeds the durable limit.");
        }
        var bytes = 0;
        foreach (var claim in claims)
        {
            var typeBytes = Encoding.UTF8.GetByteCount(claim.Type);
            var valueBytes = Encoding.UTF8.GetByteCount(claim.Value);
            if (typeBytes > MaxPolicyClaimTypeBytes || valueBytes > MaxPolicyClaimValueBytes
                || bytes > MaxPolicyClaimBytes - typeBytes - valueBytes)
            {
                throw new UnauthorizedAccessException("The agent policy input size exceeds the durable limit.");
            }
            bytes += typeBytes + valueBytes;
        }
    }
}

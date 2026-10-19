// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using System.Security.Claims;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Infrastructure.Security;

namespace Honua.Infrastructure.Authentication;

/// <summary>Nonserializable authority restricted to one verified job and exact canonical operations.</summary>
internal static class JobExecutionLeaseTransientAuthority
{
    private static readonly ConditionalWeakTable<ClaimsPrincipal, Binding> Principals = new();

    internal static bool IsLeasePrincipal(ClaimsPrincipal principal) => Principals.TryGetValue(principal, out _);

    internal static ClaimsPrincipal Bind(ClaimsPrincipal principal, JobExecutionLeaseProtector protector,
        SealedJobExecutionLeaseBinding verified, ExecutionJobRecord authoritativeJob,
        JobSecurityContext context, TimeProvider clock, Func<CancellationToken, Task<bool>> requireCurrent)
    {
        protector.RequireVerifiedCurrentBinding(verified, authoritativeJob);
        if (authoritativeJob.Audit.SubmitterSecurityContext is not { ExecutionLease: not null } original
            || original.ExecutionLease != context.ExecutionLease
            || JobExecutionLeaseIntentFingerprint.ComputeContext(original) != JobExecutionLeaseIntentFingerprint.ComputeContext(context)
            || CanonicalSecurityActor.Resolve(principal)?.ActorId != context.OwnerActorId)
        {
            throw new UnauthorizedAccessException("The lease principal does not match its original job.");
        }
        // The exposed principal carries identity only, not the original broad credential's
        // ordinary roles, grants or stored-key version. Even an in-process claim copy must
        // not turn a job-specific authority back into a reusable credential.
        var identity = new ClaimsIdentity(principal.Identity!.AuthenticationType);
        var roleTypes = context.AgentPolicyRoleClaimTypes.Append(ClaimTypes.Role).Append(RbacOptions.DefaultRoleClaimType)
            .Append(context.RoleClaimType ?? ClaimTypes.Role).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var claim in principal.Claims.Where(claim => !roleTypes.Contains(claim.Type)
            && (!CanonicalSecurityActor.IsFrameworkAuthorityClaimType(claim.Type)
                || claim.Type is "api_key_id" or "honua:delegated_agent_only")))
        {
            identity.AddClaim(claim.Clone(identity));
        }
        identity.AddClaim(CanonicalSecurityActor.CreateStampedClaim("permission", "agent:workspace"));
        identity.AddClaim(new Claim(ClaimTypes.Role, "job-executor"));
        var attenuated = new ClaimsPrincipal(identity);
        Principals.Add(attenuated, new Binding(verified.JobId, verified.IntentFingerprint,
            JobExecutionLeaseIntentFingerprint.ComputeContext(context), verified.ExpiresAt,
            verified.Scope.Resources.Select(resource => resource with { }).ToArray(),
            verified.Scope.Operations.Select(operation => operation with { ApprovalReasonCodes = operation.ApprovalReasonCodes.ToArray() }).ToArray(),
            clock, original.ExecutionLease, authoritativeJob.ClaimedBy, authoritativeJob.AttemptCount, new ClaimsPrincipal(principal.Identities.Select(source => source.Clone())), requireCurrent));
        return attenuated;
    }

    internal static async Task<bool> IsCurrentAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (!Principals.TryGetValue(principal, out var binding) || !MatchesExecutionScope(binding))
        {
            return false;
        }
        try
        {
            var current = await binding.RequireCurrent(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return current && MatchesExecutionScope(binding);
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Call only after an exact resource/operator fence AND IsCurrentAsync succeeded.
    // The original principal never leaves the canonical authorization implementation.
    internal static ClaimsPrincipal GetAuthorizationPrincipal(ClaimsPrincipal principal)
        => Principals.TryGetValue(principal, out var binding) && MatchesExecutionScope(binding)
            ? binding.AuthorizationPrincipal
            : throw new UnauthorizedAccessException("The execution lease is outside its actual job scope.");

    internal static bool AllowsResource(ClaimsPrincipal principal, string resourceId, AuthorizationOperation operation)
        => Principals.TryGetValue(principal, out var binding) && MatchesExecutionScope(binding)
            && binding.Resources.Any(resource => resource.ResourceType == OperatorResourceType.Catalog
                && resource.ResourceId == resourceId && Satisfies(resource.Operation, operation));

    internal static bool AllowsOperator(ClaimsPrincipal principal, OperatorAuthorizationRequest request)
        => Principals.TryGetValue(principal, out var binding) && MatchesExecutionScope(binding)
            && binding.Operations.Any(operation => operation.ResourceType == request.ResourceType
                && operation.ResourceId == request.ResourceId && operation.Operation == request.Operation
                && operation.WorkspaceVisibility == request.WorkspaceVisibility
                && operation.WorkspaceOwnerId == request.WorkspaceOwnerId
                && operation.WorkspaceScopeId == request.WorkspaceScopeId
                && operation.IsDestructive == request.IsDestructive
                && operation.RequiresExplicitApproval == request.RequiresExplicitApproval
                && operation.ApprovalPolicyRef == request.ApprovalPolicyRef
                && operation.ApprovalReasonCodes.SequenceEqual(request.ApprovalReasonCodes, StringComparer.Ordinal));

    private static bool Satisfies(AuthorizationOperation granted, AuthorizationOperation requested)
        => AuthorizationOperationExtensions.Satisfies(granted.ToWireName(), requested);

    internal static bool MatchesExecutionClaim(ClaimsPrincipal principal, string workerId, int attemptCount)
        => attemptCount > 0 && Principals.TryGetValue(principal, out var binding)
            && binding.WorkerId == workerId && binding.AttemptCount == attemptCount && MatchesExecutionScope(binding);

    private static bool MatchesExecutionScope(Binding binding)
        => binding.ExpiresAt > binding.Clock.GetUtcNow()
            && JobSecurityScope.Current is { Submitter: { } context } scope
            && scope.JobId == binding.JobId && scope.IntentFingerprint == binding.IntentFingerprint
            && scope.WorkerId == binding.WorkerId && scope.AttemptCount == binding.AttemptCount
            && context.ExecutionLease == binding.Lease
            && JobExecutionLeaseIntentFingerprint.ComputeContext(context) == binding.ContextFingerprint;

    private sealed record Binding(string JobId, string IntentFingerprint, string ContextFingerprint,
        DateTimeOffset ExpiresAt, JobExecutionLeaseResource[] Resources, OperatorAuthorizationRequest[] Operations,
        TimeProvider Clock, JobExecutionLease Lease, string? WorkerId, int AttemptCount, ClaimsPrincipal AuthorizationPrincipal, Func<CancellationToken, Task<bool>> RequireCurrent);
}

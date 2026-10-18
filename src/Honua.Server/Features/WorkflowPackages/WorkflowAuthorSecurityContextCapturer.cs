// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.Geoprocessing;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.WorkflowPackages;

/// <summary>
/// Captures the AUTHOR's row/field security identity (<see cref="JobSecurityContext"/>) to pin on a
/// scheduled workflow definition at publish time — the one point at which a real principal is in
/// hand for a workflow whose cron/event ticks later run under a synthesized orchestrator identity
/// (honua-server#3068). This bundles the three collaborators the capture needs — the RBAC options,
/// the tenant context, and the managed-membership source (honua-server#3081) — behind a single seam
/// so <see cref="WorkflowPackageService"/> composes one dependency for the concern instead of
/// carrying all three. Behavior is identical to the inline capture it replaces.
/// </summary>
internal sealed class WorkflowAuthorSecurityContextCapturer(
    IOptions<RbacOptions>? rbacOptions = null,
    ITenantContext? tenantContext = null,
    IPrincipalMembershipSource? principalMembershipSource = null,
    WorkflowPackageAuthorization? packageAuthorization = null)
{
    /// <summary>Resolves resource access without letting missing policy widen a delegated agent's authority.</summary>
    public async Task<WorkflowPackageAccess?> ResolvePackageAccessAsync(
        ClaimsPrincipal? principal, CancellationToken cancellationToken, bool write = false, string? workspaceId = null, string? turnId = null)
    {
        if (packageAuthorization is not null)
            return await packageAuthorization.ResolveAsync(principal, cancellationToken, workspaceId, turnId, write: write).ConfigureAwait(false);
        if (principal?.HasClaim("permission", "agent:workspace") == true)
            throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
        return null;
    }

    /// <summary>Captures immutable package ownership from the authenticated actor and trusted tenant context.</summary>
    public WorkflowPackageOwnership? CaptureOwnership(ClaimsPrincipal principal)
        => CanonicalSecurityActor.Resolve(principal) is { IsDurablyRevalidatable: true } actor && tenantContext is not null
            ? new WorkflowPackageOwnership { OwnerActorId = actor.ActorId, TenantId = tenantContext.TenantId }
            : null;

    /// <summary>
    /// Captures the author security snapshot from <paramref name="principal"/>. When the live
    /// membership source positively owns the principal's roles the snapshot is stamped with the
    /// managed-membership marker, so a later triggered run on a replica that cannot re-resolve the
    /// author fails closed rather than trusting the captured roles (honua-server#3081). A null
    /// source, an unidentifiable principal, or a source error leaves the snapshot unmarked — the
    /// documented snapshot fallback, never a new denial.
    /// </summary>
    public async Task<JobSecurityContext> CaptureAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var membershipManaged = await JobSecurityContextCapture
            .IsManagedMembershipAsync(principal, principalMembershipSource, cancellationToken)
            .ConfigureAwait(false);
        return JobSecurityContextCapture.Capture(
            principal, rbacOptions?.Value ?? new RbacOptions(), tenantContext, membershipManaged);
    }
}

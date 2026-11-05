// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Authentication;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.WorkflowPackages;

internal sealed class WorkflowAuthorSecurityContextCapturer(
    IOptions<RbacOptions>? rbacOptions = null,
    ITenantContext? tenantContext = null,
    IPrincipalMembershipSource? principalMembershipSource = null)
{
    // A replica that cannot re-resolve a managed author fails closed rather than trusting the captured roles.
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

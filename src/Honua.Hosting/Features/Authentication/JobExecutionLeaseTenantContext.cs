// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.Security;

namespace Honua.Infrastructure.Authentication;

// Fresh authority scopes must retain the caller's already selected tenant. This
// carries request provenance only: credential, intent and policy validation still
// run independently. Neither a raw tenant claim nor job metadata can initialize it.
internal static class JobExecutionLeaseTenantContext
{
    internal static void BindRequestTenant(IServiceProvider services, ClaimsPrincipal caller)
    {
        if (services.GetService<ITenantContext>() is not RequestTenantContext target) return;
        var http = services.GetService<IHttpContextAccessor>()?.HttpContext;
        string? tenant;
        TenantContextSource source;
        if (http is not null && ReferenceEquals(http.User, caller)
            && CanonicalSecurityActor.Resolve(caller) is not null
            && http.RequestServices.GetService<ITenantContext>() is { } selected)
        {
            if (selected is RequestTenantContext { IsInitialized: false })
                throw new UnauthorizedAccessException("The current request tenant has not been resolved.");
            tenant = selected.TenantId;
            source = selected.Source;
        }
        else if (CanonicalSecurityActor.FindStampedValue(caller, CanonicalSecurityActor.CanonicalActorClaim) is not null)
        {
            // Detached observation can retain an explicitly unscoped request.
            // Never fall back to the token's raw tenant when this stamp is absent.
            tenant = CanonicalSecurityActor.FindStampedValue(caller, CanonicalSecurityActor.EffectiveTenantClaim);
            source = tenant is null ? TenantContextSource.Anonymous : TenantContextSource.Claim;
        }
        else return;

        if (target.IsInitialized)
        {
            if (!string.Equals(target.TenantId, tenant, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("The authority scope has a different selected tenant.");
            return;
        }
        target.Set(tenant, source);
    }
}

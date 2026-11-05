// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.MultiTenancy.Abstractions;

namespace Honua.Infrastructure.MultiTenancy;

internal sealed class RequestTenantContext : ITenantContext
{
    public string? TenantId { get; private set; }

    public TenantContextSource Source { get; private set; } = TenantContextSource.Anonymous;

    public void Set(string? tenantId, TenantContextSource source)
    {
        TenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId;
        Source = source;
    }

    public bool RequireTenantId(out string tenantId, out string? reason)
    {
        if (!string.IsNullOrWhiteSpace(TenantId))
        {
            tenantId = TenantId!;
            reason = null;
            return true;
        }

        tenantId = string.Empty;
        reason = Source switch
        {
            TenantContextSource.Anonymous => "anonymous request and no default tenant configured",
            TenantContextSource.Claim => "tenant claim missing from principal",
            TenantContextSource.Header => "tenant header missing or rejected",
            TenantContextSource.Default => "default tenant not configured",
            _ => "no tenant context resolved",
        };
        return false;
    }
}

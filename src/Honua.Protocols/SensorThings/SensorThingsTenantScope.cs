// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.MultiTenancy;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.SensorThings;

internal static class SensorThingsTenantScope
{
    internal const string UnresolvedMessage = "Sensing data requires a resolved tenant scope.";

    // Multi-tenant mode admits non-admins only on a claim-resolved tenant. Single-tenant mode
    // resolves every authenticated request to the deployment tenant (Source=Default).
    internal static bool IsResolved(HttpContext context)
    {
        if (context.User.IsInRole("admin"))
        {
            return true;
        }

        var tenant = context.RequestServices.GetService<ITenantContext>();
        var tenancyEnabled = context.RequestServices.GetService<IOptions<TenantContextOptions>>()?.Value.Enabled == true;
        return !string.IsNullOrWhiteSpace(tenant?.TenantId)
            && (tenant?.Source == TenantContextSource.Claim
                || (!tenancyEnabled && tenant?.Source == TenantContextSource.Default));
    }
}

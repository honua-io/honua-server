// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.MultiTenancy;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.SensorThings;

internal static class SensorThingsTenantScope
{
    internal const string UnresolvedMessage = "Sensing data requires a resolved tenant scope.";

    internal const string SchemaIsolationRequiredMessage =
        "SensorThings requires active tenant schema isolation when multi-tenancy is enabled.";

    // SensorThings tables (sta_*) carry no tenant_id column and rely on schema isolation.
    // Fail closed whenever multi-tenancy is enabled without an active isolated schema.
    internal static bool HasSchemaIsolation(HttpContext context)
    {
        var tenancyEnabled = context.RequestServices.GetService<IOptions<TenantContextOptions>>()?.Value.Enabled == true;
        if (!tenancyEnabled)
        {
            return true;
        }

        var currentSchema = context.RequestServices.GetService<ISchemaContext>()?.CurrentSchema;
        return !string.IsNullOrWhiteSpace(currentSchema);
    }

    // Multi-tenant mode admits non-admins only on a claim-resolved tenant with active schema
    // isolation. Single-tenant mode resolves every authenticated request to the deployment tenant.
    internal static bool IsResolved(HttpContext context)
    {
        if (!HasSchemaIsolation(context))
        {
            return false;
        }

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

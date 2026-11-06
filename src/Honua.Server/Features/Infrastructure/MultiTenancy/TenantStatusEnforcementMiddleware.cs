// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.MultiTenancy.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.MultiTenancy;

// A tenant missing from the catalog is allowed through, and a suspended or deleted tenant is forbidden for both reads and writes.
internal sealed class TenantStatusEnforcementMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    public async Task InvokeAsync(HttpContext context)
    {
        var tenantContext = context.RequestServices.GetService<ITenantContext>();
        if (tenantContext is null
            || string.IsNullOrEmpty(tenantContext.TenantId))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var catalog = context.RequestServices.GetService<ITenantCatalog>();
        if (catalog is null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var tenant = await catalog.GetAsync(tenantContext.TenantId, context.RequestAborted).ConfigureAwait(false);
        if (tenant is null || tenant.Status == TenantStatus.Active)
        {
            // Unmanaged tenant or active tenant: allow. Provisioning is opt-in, so tenants that were
            // never registered in the catalog keep their existing behavior.
            await _next(context).ConfigureAwait(false);
            return;
        }

        var denial = tenant.Status == TenantStatus.Deleted
            ? TenantDenialKind.TenantDeleted
            : TenantDenialKind.TenantSuspended;
        await TenantDenialResponseWriter.WriteAsync(context, denial).ConfigureAwait(false);
    }
}

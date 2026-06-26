// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.MultiTenancy.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.MultiTenancy;

/// <summary>
/// Blocks requests for a tenant that is not <see cref="TenantStatus.Active"/> (issue #2156).
/// </summary>
/// <remarks>
/// Runs after <c>TenantContextMiddleware</c> has resolved the tenant. A tenant that is not present
/// in the catalog is treated as unmanaged and allowed through, so default single-tenant and the
/// anonymous <c>public</c> tenant are unaffected. A suspended or deleted tenant receives
/// <c>403 Forbidden</c> so suspension blocks both read and write access consistently across every
/// protocol, enforced centrally rather than by caller discipline.
/// </remarks>
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

        await WriteBlockedAsync(context, tenant.Status).ConfigureAwait(false);
    }

    private static async Task WriteBlockedAsync(HttpContext context, TenantStatus status)
    {
        var (error, message) = status == TenantStatus.Deleted
            ? ("tenant_deleted", "Tenant access is unavailable.")
            : ("tenant_suspended", "Tenant access is currently suspended.");

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            $"{{\"error\":\"{error}\",\"message\":\"{message}\"}}",
            context.RequestAborted).ConfigureAwait(false);
    }
}

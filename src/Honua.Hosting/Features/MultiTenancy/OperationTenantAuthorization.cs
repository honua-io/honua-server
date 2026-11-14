// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.MultiTenancy.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.MultiTenancy;

internal static class OperationTenantAuthorization
{
    public static bool CanAccess(HttpContext context, string? ownerTenantId)
    {
        var options = context.RequestServices.GetService<IOptions<TenantContextOptions>>()?.Value
            ?? new TenantContextOptions();
        if (context.User.Identity?.IsAuthenticated == true &&
            options.MultiTenantAdminRoles.Any(role => !string.IsNullOrWhiteSpace(role) && context.User.IsInRole(role)))
        {
            return true;
        }

        var tenant = context.RequestServices.GetService<ITenantContext>();
        if (!string.IsNullOrWhiteSpace(ownerTenantId))
        {
            return string.Equals(ownerTenantId, tenant?.TenantId, StringComparison.Ordinal);
        }

        if (tenant is null || string.IsNullOrWhiteSpace(tenant.TenantId))
        {
            return true;
        }

        return !options.Enabled
            && tenant.Source == TenantContextSource.Default
            && string.Equals(tenant.TenantId, options.DefaultTenantId, StringComparison.Ordinal);
    }
}

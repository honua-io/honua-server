// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.MultiTenancy.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.MultiTenancy;

/// <summary>Shared ownership boundary for durable operation records.</summary>
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
        // Unowned legacy records cannot be attributed to any tenant safely.
        // Preserve installations with tenant resolution disabled, but fail closed
        // when a tenant is resolved. Platform operators can inspect legacy records.
        return string.IsNullOrWhiteSpace(ownerTenantId)
            ? string.IsNullOrWhiteSpace(tenant?.TenantId)
            : string.Equals(ownerTenantId, tenant?.TenantId, StringComparison.Ordinal);
    }
}

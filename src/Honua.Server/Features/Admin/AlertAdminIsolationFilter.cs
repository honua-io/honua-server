// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.MultiTenancy;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

/// <summary>Protects the instance-wide Preview alert stores from tenant-scoped access.</summary>
internal sealed class AlertAdminIsolationFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        var tenant = context.RequestServices.GetService<ITenantContext>();
        var options = context.RequestServices.GetRequiredService<IOptions<TenantContextOptions>>().Value;
        // Alert rows and delivery channels have no tenant ownership. An authenticated
        // tenant administrator must never inherit instance-wide access, even when
        // tenant resolution is disabled or a supplied override was ignored upstream.
        if (tenant?.Source is TenantContextSource.Claim or TenantContextSource.Header ||
            context.Request.Headers.ContainsKey(TenantContextOptions.TenantHeaderName) ||
            options.TenantClaimTypes.Any(type => context.User.HasClaim(claim =>
                string.Equals(claim.Type, type, StringComparison.OrdinalIgnoreCase))))
        {
            return ValueTask.FromResult<object?>(ProblemDetailsHelpers.CreateAdminProblem(
                StatusCodes.Status403Forbidden, "Forbidden",
                "Preview alert administration requires an instance administrator without a tenant scope. Tenant-owned alerts are not implemented."));
        }

        return next(invocation);
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.MultiTenancy;

internal static class OperationTenantAuthorization
{
    public static bool CanAccess(HttpContext context, string? ownerTenantId)
        => CanAccess(context.User, context.RequestServices, ownerTenantId);

    public static bool CanAccess(ClaimsPrincipal user, IServiceProvider services, string? ownerTenantId)
    {
        var options = services.GetService<IOptions<TenantContextOptions>>()?.Value
            ?? new TenantContextOptions();
        if (user.Identity?.IsAuthenticated == true &&
            options.MultiTenantAdminRoles.Any(role => !string.IsNullOrWhiteSpace(role) && user.IsInRole(role)))
        {
            return true;
        }

        var tenant = services.GetService<ITenantContext>();
        if (!string.IsNullOrWhiteSpace(ownerTenantId))
        {
            return string.Equals(ownerTenantId, tenant?.TenantId, StringComparison.Ordinal);
        }

        if (tenant is null || string.IsNullOrWhiteSpace(tenant.TenantId))
        {
            return !options.Enabled;
        }

        return !options.Enabled
            && tenant.Source == TenantContextSource.Default
            && string.Equals(tenant.TenantId, options.DefaultTenantId, StringComparison.Ordinal);
    }

    // The verified submitter snapshot names the tenant the job acts for; the audit tenant covers
    // jobs submitted without a snapshot. A job with neither is treated as unowned.
    public static string? OwnerTenantOf(ExecutionJobRecord job)
        => FirstNonBlank(job.Audit.SubmitterSecurityContext?.TenantId, job.Audit.TenantId);

    public static async Task<string?> FindJobOwnerTenantAsync(
        IServiceProvider services,
        string operationId,
        CancellationToken cancellationToken)
    {
        var jobStore = services.GetService<IExecutionJobStore>();
        var job = jobStore is null || string.IsNullOrWhiteSpace(operationId)
            ? null
            : await jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
        return job is null ? null : OwnerTenantOf(job);
    }

    public static async Task<bool> CanAccessJobAsync(
        HttpContext context,
        string operationId,
        CancellationToken cancellationToken)
    {
        var owner = await FindJobOwnerTenantAsync(context.RequestServices, operationId, cancellationToken)
            .ConfigureAwait(false);
        return CanAccess(context, owner);
    }

    // The durable job's owner wins; when there is no job, or the job records no owner, the tenant the
    // progress recorded is used.
    public static async Task<bool> CanAccessOperationAsync(
        HttpContext context,
        IOperationProgress progress,
        CancellationToken cancellationToken)
    {
        var owner = await FindJobOwnerTenantAsync(context.RequestServices, progress.OperationId, cancellationToken)
            .ConfigureAwait(false)
            ?? (progress as ITenantOwnedProgress)?.TenantId;
        return CanAccess(context, owner);
    }

    private static string? FirstNonBlank(string? first, string? second)
    {
        if (!string.IsNullOrWhiteSpace(first))
        {
            return first;
        }

        return string.IsNullOrWhiteSpace(second) ? null : second;
    }
}

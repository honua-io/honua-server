// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.MultiTenancy;

namespace Honua.Infrastructure.Authentication;

internal interface IPortalAudienceSnapshotProvider
{
    Task<IReadOnlySet<string>> GetServiceIdsAsync(
        string username, string? tenantId, CancellationToken cancellationToken);

    Task<IReadOnlySet<string>> GetServiceIdsAsync(
        string username, TenantReadScope scope, CancellationToken cancellationToken);
}

internal static class PortalAudienceSnapshot
{
    internal static readonly object Key = new();

    internal static void Set(HttpContext context, IReadOnlySet<string> serviceIds)
        => context.Items[Key] = serviceIds;

    internal static async Task RefreshAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var provider = context.RequestServices.GetService<IPortalAudienceSnapshotProvider>();
        if (provider is null)
        {
            return;
        }

        // Remove the previous grant before querying so a failed refresh cannot retain it.
        context.Items.Remove(Key);
        if (context.User.Identity?.IsAuthenticated == true
            && context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } username
            && PortalResourceReadAuthorizer.TryGetTrustedTenant(context, out _))
        {
            var scope = TenantReadScope.For(context.RequestServices.GetService<ITenantContext>());
            var serviceIds = await provider.GetServiceIdsAsync(username, scope, cancellationToken).ConfigureAwait(false);
            Set(context, serviceIds);
        }
    }

    internal static bool HasService(HttpContext context, string serviceId)
        => context.Items.TryGetValue(Key, out var value)
            && value is IReadOnlySet<string> serviceIds
            && serviceIds.Contains(serviceId);
}

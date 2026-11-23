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

// A stream's background refresh replaces the grant while the request thread reads it, so the
// request items hold one holder whose value is swapped atomically.
internal static class PortalAudienceSnapshot
{
    internal static readonly object Key = new();

    private sealed class Holder
    {
        private IReadOnlySet<string>? _serviceIds;

        internal IReadOnlySet<string>? ServiceIds
        {
            get => Volatile.Read(ref _serviceIds);
            set => Volatile.Write(ref _serviceIds, value);
        }
    }

    // Called on the request thread before any background refresh can run.
    internal static void Attach(HttpContext context)
    {
        if (context.Items[Key] is not Holder)
        {
            context.Items[Key] = new Holder { ServiceIds = context.Items[Key] as IReadOnlySet<string> };
        }
    }

    internal static void Set(HttpContext context, IReadOnlySet<string> serviceIds)
    {
        if (context.Items[Key] is Holder holder)
        {
            holder.ServiceIds = serviceIds;
        }
        else
        {
            context.Items[Key] = new Holder { ServiceIds = serviceIds };
        }
    }

    internal static async Task RefreshAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var provider = context.RequestServices.GetService<IPortalAudienceSnapshotProvider>();
        if (provider is null)
        {
            return;
        }

        // Clear the previous grant before querying so a failed refresh cannot retain it.
        Clear(context);
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
            && value is Holder { ServiceIds: { } serviceIds }
            && serviceIds.Contains(serviceId);

    private static void Clear(HttpContext context)
    {
        if (context.Items[Key] is Holder holder)
        {
            holder.ServiceIds = null;
        }
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

/// <summary>Loads one fresh, statement-consistent grant view after authentication and tenant resolution.</summary>
internal interface IPortalAudienceSnapshotProvider
{
    Task<IReadOnlySet<string>> GetServiceIdsAsync(
        string username, string? tenantId, CancellationToken cancellationToken);
}

internal static class PortalAudienceSnapshot
{
    internal static readonly object Key = new();

    internal static void Set(HttpContext context, IReadOnlySet<string> serviceIds)
        => context.Items[Key] = serviceIds;

    internal static bool HasService(HttpContext context, string serviceId)
        => context.Items.TryGetValue(Key, out var value)
            && value is IReadOnlySet<string> serviceIds
            && serviceIds.Contains(serviceId);
}

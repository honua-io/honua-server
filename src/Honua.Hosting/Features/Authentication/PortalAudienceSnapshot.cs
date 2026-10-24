// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

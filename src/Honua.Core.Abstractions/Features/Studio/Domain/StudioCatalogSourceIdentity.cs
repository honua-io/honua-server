// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.Studio.Domain;

/// <summary>Canonical Studio catalog identity; an identity grants no source access.</summary>
public static class StudioCatalogSourceIdentity
{
    /// <summary>Encodes the service identity and service-local layer index as Studio does.</summary>
    /// <param name="serviceId">Canonical service identity, not its display name or URL.</param>
    /// <param name="layerId">Service-local layer index.</param>
    /// <returns>The ECMAScript encodeURIComponent-compatible catalog identity.</returns>
    public static string Create(string serviceId, int layerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        ArgumentOutOfRangeException.ThrowIfNegative(layerId);
        var service = Uri.EscapeDataString(serviceId)
            .Replace("%21", "!", StringComparison.Ordinal)
            .Replace("%27", "'", StringComparison.Ordinal)
            .Replace("%28", "(", StringComparison.Ordinal)
            .Replace("%29", ")", StringComparison.Ordinal)
            .Replace("%2A", "*", StringComparison.Ordinal);
        return service + "/" + layerId.ToString(CultureInfo.InvariantCulture);
    }
}

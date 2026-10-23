// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.Studio.Domain;

/// <summary>Canonical Studio catalog identity; an identity grants no source access.</summary>
public static class StudioCatalogSourceIdentity
{
    /// <summary>
    /// Schema ceiling for a catalog key. <see cref="Create"/> always returns Studio's
    /// <c>encodeURIComponent(serviceId)/layerId</c> form, including when the service id is
    /// longer than a published name. The ceiling fits that encoding for a service id of
    /// several hundred characters; it is not a second key format.
    /// </summary>
    public const int MaxLength = 8192;

    /// <summary>Encodes the service identity and service-local layer index as Studio does.</summary>
    /// <param name="serviceId">Canonical service identity, not its display name or URL.</param>
    /// <param name="layerId">Service-local layer index.</param>
    /// <returns>
    /// The ECMAScript encodeURIComponent-compatible catalog identity Studio copies into
    /// <c>layer.sourceId</c>.
    /// </returns>
    public static string Create(string serviceId, int layerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        ArgumentOutOfRangeException.ThrowIfNegative(layerId);
        var layer = layerId.ToString(CultureInfo.InvariantCulture);
        var encoded = EncodeService(serviceId) + "/" + layer;
        if (encoded.Length > MaxLength)
        {
            throw new ArgumentException(
                $"The Studio catalog key for '{serviceId}' is {encoded.Length} characters and the schema allows {MaxLength}.",
                nameof(serviceId));
        }

        return encoded;
    }

    private static string EncodeService(string serviceId)
        => Uri.EscapeDataString(serviceId)
            .Replace("%21", "!", StringComparison.Ordinal)
            .Replace("%27", "'", StringComparison.Ordinal)
            .Replace("%28", "(", StringComparison.Ordinal)
            .Replace("%29", ")", StringComparison.Ordinal)
            .Replace("%2A", "*", StringComparison.Ordinal);
}

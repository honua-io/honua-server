// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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
        if (!TryCreate(serviceId, layerId, out var catalogId))
        {
            throw new ArgumentException(
                $"The Studio catalog key for '{serviceId}' exceeds {MaxLength} characters.",
                nameof(serviceId));
        }

        return catalogId;
    }

    /// <summary>
    /// Builds the Studio catalog key, or returns false when it would exceed <see cref="MaxLength"/>.
    /// Callers that walk every service must skip a false result instead of failing the walk.
    /// </summary>
    public static bool TryCreate(string serviceId, int layerId, out string catalogId)
    {
        catalogId = string.Empty;
        if (string.IsNullOrWhiteSpace(serviceId) || layerId < 0)
        {
            return false;
        }

        var encoded = EncodeService(serviceId) + "/" + layerId.ToString(CultureInfo.InvariantCulture);
        if (encoded.Length > MaxLength)
        {
            return false;
        }

        catalogId = encoded;
        return true;
    }

    private static string EncodeService(string serviceId)
        => Uri.EscapeDataString(serviceId)
            .Replace("%21", "!", StringComparison.Ordinal)
            .Replace("%27", "'", StringComparison.Ordinal)
            .Replace("%28", "(", StringComparison.Ordinal)
            .Replace("%29", ")", StringComparison.Ordinal)
            .Replace("%2A", "*", StringComparison.Ordinal);
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.Studio.Domain;

public static class StudioCatalogSourceIdentity
{
    public const int MaxLength = 8192;

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

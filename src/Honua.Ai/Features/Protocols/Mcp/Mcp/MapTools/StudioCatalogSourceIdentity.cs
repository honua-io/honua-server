// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Ai.Protocols.Mcp.MapTools;

/// <summary>Projects the existing Studio catalog identity; it grants no source access.</summary>
internal static class StudioCatalogSourceIdentity
{
    internal static string Create(string serviceId, int layerId)
    {
        // Match StudioClient's encodeURIComponent(serviceId), including the five
        // ECMAScript unescaped characters that Uri.EscapeDataString encodes.
        var service = Uri.EscapeDataString(serviceId)
            .Replace("%21", "!", StringComparison.Ordinal)
            .Replace("%27", "'", StringComparison.Ordinal)
            .Replace("%28", "(", StringComparison.Ordinal)
            .Replace("%29", ")", StringComparison.Ordinal)
            .Replace("%2A", "*", StringComparison.Ordinal);
        return service + "/" + layerId.ToString(CultureInfo.InvariantCulture);
    }
}

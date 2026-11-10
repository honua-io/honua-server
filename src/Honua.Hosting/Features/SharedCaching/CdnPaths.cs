// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.SharedCaching;

// Every path ends in a wildcard so that all query-string variants of an address are invalidated with it. CloudFront allows
// only 15 wildcard paths in progress at once across the distribution, so an edit names a small, fixed set.
internal static class CdnPaths
{
    internal static readonly string[] Everything = ["/*"];

    private const string EveryOgcSurface = "/ogc/*";

    // Routes match without regard to case and the CDN keys with regard to it; the lower-case spelling and the one given cover what clients are handed.
    internal static string[] ForEditedLayer(int layerId, IEnumerable<string> serviceIds) =>
    [
        $"/tiles/{layerId}/*",
        EveryOgcSurface,
        .. serviceIds
            .SelectMany(serviceId => new[] { serviceId, serviceId.ToLowerInvariant() })
            .Distinct(StringComparer.Ordinal)
            .Select(serviceId => $"/rest/services/{Uri.EscapeDataString(serviceId)}/*"),
    ];
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.SharedCaching;

// Longer CDN lifetimes for layers whose every write goes through the product, so each edit reaches the CDN as a tag
// purge. A layer whose table is also written outside the product must not be listed: nothing would purge its
// responses. Browser lifetimes stay as they are; only the CDN keeps the response longer.
internal sealed class SharedCachePurgeBacked
{
    private const int OneDaySeconds = 86_400;

    // Below this share of the monthly edit-purge budget left, new responses get the ordinary lifetimes again, so the
    // rest of the budget can still purge what was already stored with a long one.
    internal const int BudgetHeadroomPercent = 10;

    public bool AllLayers { get; set; }

    // Layers are named "{serviceId}/{layerId}".
    public List<string> Layers { get; set; } = [];

    public SharedCacheLifetime Tile { get; set; } =
        new() { MaxAgeSeconds = 60, SharedMaxAgeSeconds = OneDaySeconds, StaleWhileRevalidateSeconds = 60 };

    public SharedCacheLifetime FeatureQuery { get; set; } =
        new() { MaxAgeSeconds = 0, SharedMaxAgeSeconds = 300, StaleWhileRevalidateSeconds = 30 };

    internal bool Configured => AllLayers || Layers.Count > 0;

    // Only a response about one layer qualifies: a response drawing on several carries no single layer's purge.
    internal bool AppliesTo(SharedCacheSubject? subject)
        => subject?.LayerKey is { } layerKey
           && (AllLayers || Layers.Contains(layerKey, StringComparer.OrdinalIgnoreCase));

    internal SharedCacheLifetime? LifetimeFor(ResponseCacheClass cacheClass) => cacheClass switch
    {
        ResponseCacheClass.Tile => Tile,
        ResponseCacheClass.FeatureQuery => FeatureQuery,
        _ => null,
    };
}

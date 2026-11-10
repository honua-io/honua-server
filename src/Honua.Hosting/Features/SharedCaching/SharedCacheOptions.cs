// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.SharedCaching;

internal sealed class SharedCacheOptions
{
    public const string SectionName = "SharedCache";

    private const int OneYearSeconds = 31_536_000;
    private const int OneDaySeconds = 86_400;

    public SharedCacheLifetime Tile { get; set; } =
        new() { MaxAgeSeconds = 60, SharedMaxAgeSeconds = 300, StaleWhileRevalidateSeconds = 60 };

    public SharedCacheLifetime FeatureQuery { get; set; } =
        new() { MaxAgeSeconds = 0, SharedMaxAgeSeconds = 30, StaleWhileRevalidateSeconds = 30 };

    public SharedCacheLifetime MetadataDocument { get; set; } =
        new() { MaxAgeSeconds = 30, SharedMaxAgeSeconds = 60 };

    public SharedCacheLifetime StaticMapImage { get; set; } =
        new() { MaxAgeSeconds = 0, SharedMaxAgeSeconds = 60, StaleWhileRevalidateSeconds = 30 };

    public SharedCacheLifetime StaticObject { get; set; } =
        new() { MaxAgeSeconds = 60, SharedMaxAgeSeconds = 300 };

    public int GenerationAddressedLifetimeSeconds { get; set; } = OneYearSeconds;

    // Lets the CDN keep serving the last good public tile or query while the origin is unreachable.
    public int StaleIfErrorSeconds { get; set; } = OneDaySeconds;

    public SharedCacheEmergency Emergency { get; set; } = new();

    internal SharedCacheLifetime DefaultFor(ResponseCacheClass cacheClass) => cacheClass switch
    {
        ResponseCacheClass.Tile => Tile,
        ResponseCacheClass.FeatureQuery => FeatureQuery,
        ResponseCacheClass.MetadataDocument => MetadataDocument,
        ResponseCacheClass.StaticMapImage => StaticMapImage,
        ResponseCacheClass.StaticObject => StaticObject,
        _ => throw new ArgumentOutOfRangeException(nameof(cacheClass), cacheClass, null),
    };
}

// One step, reversible, and it leaves every stored per-layer lifetime untouched.
internal sealed class SharedCacheEmergency
{
    internal static readonly SharedCacheLifetime Lifetime = new() { MaxAgeSeconds = 0, SharedMaxAgeSeconds = 15 };

    public bool AllPublicLayers { get; set; }

    public List<string> Layers { get; set; } = [];

    internal bool AppliesTo(string? layerKey)
        => AllPublicLayers
           || (layerKey is not null && Layers.Contains(layerKey, StringComparer.OrdinalIgnoreCase));
}

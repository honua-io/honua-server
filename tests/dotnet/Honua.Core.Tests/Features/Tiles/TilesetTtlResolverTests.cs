// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Tiles;

namespace Honua.Core.Tests.Features.Tiles;

public class TilesetTtlResolverTests
{
    private const string ServiceId = "FeatureServer";
    private const string LayerId = "0";
    private const string TileMatrixSetId = "WebMercatorQuad";

    [Trait("Tier", "Fast")]
    [Fact]
    public void BuildKey_ComposesServiceLayerMatrixSet()
    {
        TilesetTtlResolver.BuildKey(ServiceId, LayerId, TileMatrixSetId)
            .Should().Be("FeatureServer/0/WebMercatorQuad");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Resolve_PerTilesetOverride_ReturnsOverrideTtl()
    {
        var key = TilesetTtlResolver.BuildKey(ServiceId, LayerId, TileMatrixSetId);
        var options = new TileOptions
        {
            CacheMaxAge = 3600,
            TilesetLifecycle = new Dictionary<string, TilesetCacheLifecycle>
            {
                [key] = new TilesetCacheLifecycle { TtlSeconds = 60 }
            }
        };

        TilesetTtlResolver.Resolve(options, ServiceId, LayerId, TileMatrixSetId)
            .Should().Be(60);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Resolve_KeyMiss_FallsBackToGlobalCacheMaxAge()
    {
        var options = new TileOptions
        {
            CacheMaxAge = 3600,
            TilesetLifecycle = new Dictionary<string, TilesetCacheLifecycle>
            {
                ["OtherService/9/WebMercatorQuad"] = new TilesetCacheLifecycle { TtlSeconds = 60 }
            }
        };

        TilesetTtlResolver.Resolve(options, ServiceId, LayerId, TileMatrixSetId)
            .Should().Be(3600);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Resolve_NullLifecycleMap_ReturnsGlobalCacheMaxAge()
    {
        var options = new TileOptions { CacheMaxAge = 1800, TilesetLifecycle = null };

        TilesetTtlResolver.Resolve(options, ServiceId, LayerId, TileMatrixSetId)
            .Should().Be(1800);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Resolve_EmptyLifecycleMap_ReturnsGlobalCacheMaxAge()
    {
        var options = new TileOptions
        {
            CacheMaxAge = 1800,
            TilesetLifecycle = new Dictionary<string, TilesetCacheLifecycle>()
        };

        TilesetTtlResolver.Resolve(options, ServiceId, LayerId, TileMatrixSetId)
            .Should().Be(1800);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Resolve_OverrideEntryWithNullTtl_FallsBackToGlobal()
    {
        // A configured tileset entry that does not pin a TtlSeconds must defer to
        // the global default rather than coercing to zero (no-cache).
        var key = TilesetTtlResolver.BuildKey(ServiceId, LayerId, TileMatrixSetId);
        var options = new TileOptions
        {
            CacheMaxAge = 3600,
            TilesetLifecycle = new Dictionary<string, TilesetCacheLifecycle>
            {
                [key] = new TilesetCacheLifecycle { TtlSeconds = null }
            }
        };

        TilesetTtlResolver.Resolve(options, ServiceId, LayerId, TileMatrixSetId)
            .Should().Be(3600);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Resolve_KeyOverload_MatchesComposedKey()
    {
        var key = TilesetTtlResolver.BuildKey(ServiceId, LayerId, TileMatrixSetId);
        var options = new TileOptions
        {
            CacheMaxAge = 3600,
            TilesetLifecycle = new Dictionary<string, TilesetCacheLifecycle>
            {
                [key] = new TilesetCacheLifecycle { TtlSeconds = 120 }
            }
        };

        TilesetTtlResolver.Resolve(options, key).Should().Be(120);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void TileOptions_MetatilingDefaultsOffAndIsConfigurable()
    {
        new TileOptions().MetatileFactor.Should().Be(1);
        new TileOptions { MetatileFactor = 4 }.MetatileFactor.Should().Be(4);
    }
}

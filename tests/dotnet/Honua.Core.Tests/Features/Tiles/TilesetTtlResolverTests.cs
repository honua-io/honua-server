// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Tiles;

namespace Honua.Core.Tests.Features.Tiles;

public class TilesetTtlResolverTests
{
    private const string ServiceId = "FeatureServer";
    private const string LayerId = "0";
    private const string TileMatrixSetId = "WebMercatorQuad";

    [Fact]
    public void BuildKey_ComposesServiceLayerMatrixSet()
    {
        TilesetTtlResolver.BuildKey(ServiceId, LayerId, TileMatrixSetId)
            .Should().Be("FeatureServer/0/WebMercatorQuad");
    }

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

    [Fact]
    public void Resolve_NullLifecycleMap_ReturnsGlobalCacheMaxAge()
    {
        var options = new TileOptions { CacheMaxAge = 1800, TilesetLifecycle = null };

        TilesetTtlResolver.Resolve(options, ServiceId, LayerId, TileMatrixSetId)
            .Should().Be(1800);
    }

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

    // ---------------------------------------------------------------------
    // Formerly-deferred seams (#1794) now implemented in #1837. The dedicated
    // behavior is covered by TileCacheQuotaPolicyTests (size-quota / LRU
    // eviction) and MetatileGroupingTests (metatiling); scheduled invalidation
    // is covered by TileCacheExpiryHostedServiceTests. These guards keep the
    // resolver / options seam wired so a future refactor cannot silently drop
    // the eviction / metatiling configuration.
    // ---------------------------------------------------------------------

    [Fact]
    public void TileOptions_ExposesEvictionAndMetatilingSeams()
    {
        var options = new TileOptions
        {
            MetatileFactor = 4,
            Eviction = new TileCacheEvictionOptions
            {
                Enabled = true,
                MaxEntries = 1000,
                MaxBytes = 50_000_000
            }
        };

        options.MetatileFactor.Should().Be(4);
        options.Eviction.Enabled.Should().BeTrue();
        options.Eviction.MaxEntries.Should().Be(1000);
        options.Eviction.MaxBytes.Should().Be(50_000_000);
    }

    [Fact]
    public void TileOptions_DefaultEvictionDisabled_AndMetatilingOff()
    {
        var options = new TileOptions();

        options.MetatileFactor.Should().Be(1);
        options.Eviction.Should().NotBeNull();
        options.Eviction.Enabled.Should().BeFalse();
    }
}

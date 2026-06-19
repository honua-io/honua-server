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
    // DEFERRED SEAMS (#1794) — placeholder stubs marking work intentionally
    // out of scope for this PR. These are skipped so they show up as pending
    // in the test report without failing the suite.
    // ---------------------------------------------------------------------

    [Fact(Skip = "Deferred (#1794): size-quota / LRU eviction is not implemented on the serve path yet.")]
    public void SizeQuotaLruEviction_NotYetImplemented()
    {
        // TODO(#1794): when cache size-quota / LRU eviction lands, assert the
        // resolver (or its successor) honours per-tileset max-bytes / entry caps.
    }

    [Fact(Skip = "Deferred (#1794): scheduled time-based cache invalidation is not implemented yet.")]
    public void ScheduledInvalidation_NotYetImplemented()
    {
        // TODO(#1794): when scheduled invalidation lands, assert expiry windows
        // (absolute / cron-style) drive cache purges for a tileset.
    }

    [Fact(Skip = "Deferred (#1794): metatiling is not implemented yet.")]
    public void Metatiling_NotYetImplemented()
    {
        // TODO(#1794): when metatiling lands, assert the meta-tile grouping /
        // sub-tile slicing interacts correctly with the resolved TTL.
    }
}

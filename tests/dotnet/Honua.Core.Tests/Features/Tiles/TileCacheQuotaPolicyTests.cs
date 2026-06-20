// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Tiles;

namespace Honua.Core.Tests.Features.Tiles;

public class TileCacheQuotaPolicyTests
{
    private static readonly DateTimeOffset Base = new(2026, 6, 18, 0, 0, 0, TimeSpan.Zero);

    private static TileCacheEntry Entry(string key, long bytes, int minutesAgo)
        => new(key, bytes, Base.AddMinutes(-minutesAgo));

    [Fact]
    public void SelectEvictions_Disabled_ReturnsEmpty()
    {
        var entries = new[] { Entry("a", 100, 10), Entry("b", 100, 5) };
        var options = new TileCacheEvictionOptions { Enabled = false, MaxEntries = 1 };

        TileCacheQuotaPolicy.SelectEvictions(entries, options).Should().BeEmpty();
    }

    [Fact]
    public void SelectEvictions_NoQuotaConfigured_ReturnsEmpty()
    {
        var entries = new[] { Entry("a", 100, 10), Entry("b", 100, 5) };
        var options = new TileCacheEvictionOptions { Enabled = true };

        TileCacheQuotaPolicy.SelectEvictions(entries, options).Should().BeEmpty();
    }

    [Fact]
    public void SelectEvictions_WithinQuota_ReturnsEmpty()
    {
        var entries = new[] { Entry("a", 100, 10), Entry("b", 100, 5) };
        var options = new TileCacheEvictionOptions { Enabled = true, MaxEntries = 5, MaxBytes = 10_000 };

        TileCacheQuotaPolicy.SelectEvictions(entries, options).Should().BeEmpty();
    }

    [Fact]
    public void SelectEvictions_EntryCapExceeded_EvictsLeastRecentlyUsedFirst()
    {
        var entries = new[]
        {
            Entry("oldest", 100, 30),
            Entry("middle", 100, 20),
            Entry("newest", 100, 1)
        };
        var options = new TileCacheEvictionOptions { Enabled = true, MaxEntries = 2 };

        var evicted = TileCacheQuotaPolicy.SelectEvictions(entries, options);

        evicted.Should().ContainSingle().Which.Should().Be("oldest");
    }

    [Fact]
    public void SelectEvictions_ByteCapExceeded_EvictsUntilUnderQuota()
    {
        var entries = new[]
        {
            Entry("oldest", 500, 30),
            Entry("middle", 500, 20),
            Entry("newest", 500, 1)
        };
        // Cap at 1000 bytes; total is 1500, so the single oldest (500) must go to reach 1000.
        var options = new TileCacheEvictionOptions { Enabled = true, MaxBytes = 1000 };

        var evicted = TileCacheQuotaPolicy.SelectEvictions(entries, options);

        evicted.Should().ContainSingle().Which.Should().Be("oldest");
    }

    [Fact]
    public void SelectEvictions_BothQuotas_EvictsToSatisfyTighter()
    {
        var entries = new[]
        {
            Entry("e1", 400, 50),
            Entry("e2", 400, 40),
            Entry("e3", 400, 30),
            Entry("e4", 400, 20)
        };
        // Entry cap of 3 needs 1 eviction; byte cap of 800 needs 2 evictions. Take the tighter (2).
        var options = new TileCacheEvictionOptions { Enabled = true, MaxEntries = 3, MaxBytes = 800 };

        var evicted = TileCacheQuotaPolicy.SelectEvictions(entries, options);

        evicted.Should().Equal("e1", "e2");
    }

    [Fact]
    public void SelectEvictions_EmptyInput_ReturnsEmpty()
    {
        var options = new TileCacheEvictionOptions { Enabled = true, MaxEntries = 1 };

        TileCacheQuotaPolicy.SelectEvictions([], options).Should().BeEmpty();
    }

    [Fact]
    public void SelectEvictions_NonPositiveCaps_TreatedAsNoCap()
    {
        var entries = new[] { Entry("a", 100, 10), Entry("b", 100, 5) };
        var options = new TileCacheEvictionOptions { Enabled = true, MaxEntries = 0, MaxBytes = -1 };

        TileCacheQuotaPolicy.SelectEvictions(entries, options).Should().BeEmpty();
    }
}

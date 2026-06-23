// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.Core.Features.Tiles;
using Honua.Server.Features.Admin.TileOperations;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Admin;

[Protocol(TestProtocols.Admin)]
[Operation(Operations.Cache)]
public sealed class TileCacheEvictionServiceTests
{
    [UnitTest]
    public async Task SweepAsync_WhenEvictionDisabled_IsNoOp()
    {
        var index = new FakeTileCacheKeyIndex(enabled: false);
        var service = CreateService(index, eviction: new TileCacheEvictionOptions { Enabled = false });

        var result = await service.SweepAsync(CancellationToken.None);

        result.Enabled.Should().BeFalse();
        result.Evicted.Should().Be(0);
        index.Removed.Should().BeEmpty();
    }

    [UnitTest]
    public async Task SweepAsync_WhenIndexDisabled_IsNoOpEvenIfOptionsEnabled()
    {
        var index = new FakeTileCacheKeyIndex(enabled: false);
        var service = CreateService(index, eviction: new TileCacheEvictionOptions
        {
            Enabled = true,
            MaxEntries = 1
        });

        var result = await service.SweepAsync(CancellationToken.None);

        result.Enabled.Should().BeFalse();
        index.Removed.Should().BeEmpty();
    }

    [UnitTest]
    public async Task SweepAsync_WhenWithinQuota_EvictsNothing()
    {
        var now = DateTimeOffset.UtcNow;
        var index = new FakeTileCacheKeyIndex(enabled: true);
        index.Seed(new TileCacheEntry("a", 10, now.AddMinutes(-2)));
        index.Seed(new TileCacheEntry("b", 10, now.AddMinutes(-1)));

        var service = CreateService(index, eviction: new TileCacheEvictionOptions
        {
            Enabled = true,
            MaxEntries = 5
        });

        var result = await service.SweepAsync(CancellationToken.None);

        result.Enabled.Should().BeTrue();
        result.Scanned.Should().Be(2);
        result.Evicted.Should().Be(0);
        index.Removed.Should().BeEmpty();
    }

    [UnitTest]
    public async Task SweepAsync_OverEntryQuota_EvictsLeastRecentlyUsedFirst()
    {
        var now = DateTimeOffset.UtcNow;
        var index = new FakeTileCacheKeyIndex(enabled: true);
        index.Seed(new TileCacheEntry("oldest", 10, now.AddMinutes(-30)));
        index.Seed(new TileCacheEntry("middle", 10, now.AddMinutes(-20)));
        index.Seed(new TileCacheEntry("newest", 10, now.AddMinutes(-1)));

        var service = CreateService(index, eviction: new TileCacheEvictionOptions
        {
            Enabled = true,
            MaxEntries = 1
        });

        var result = await service.SweepAsync(CancellationToken.None);

        result.Enabled.Should().BeTrue();
        result.Scanned.Should().Be(3);
        result.Evicted.Should().Be(2);

        // The two least-recently-used keys are removed; the most recently used survives.
        index.Removed.Should().BeEquivalentTo(["oldest", "middle"]);
    }

    [UnitTest]
    public async Task SweepAsync_OverByteQuota_EvictsUntilWithinBudget()
    {
        var now = DateTimeOffset.UtcNow;
        var index = new FakeTileCacheKeyIndex(enabled: true);
        index.Seed(new TileCacheEntry("oldest", 100, now.AddMinutes(-30)));
        index.Seed(new TileCacheEntry("newest", 100, now.AddMinutes(-1)));

        var service = CreateService(index, eviction: new TileCacheEvictionOptions
        {
            Enabled = true,
            MaxBytes = 150
        });

        var result = await service.SweepAsync(CancellationToken.None);

        result.Evicted.Should().Be(1);
        index.Removed.Should().BeEquivalentTo(["oldest"]);
    }

    private static TileCacheEvictionService CreateService(
        FakeTileCacheKeyIndex index,
        TileCacheEvictionOptions eviction)
    {
        var tileOptions = Options.Create(new Honua.Core.Features.Tiles.TileOptions
        {
            Eviction = eviction
        });

        return new TileCacheEvictionService(
            index,
            tileOptions,
            NullLogger<TileCacheEvictionService>.Instance,
            storage: null);
    }

    private sealed class FakeTileCacheKeyIndex(bool enabled) : ITileCacheKeyIndex
    {
        private readonly List<TileCacheEntry> _entries = [];

        public ConcurrentBag<string> Removed { get; } = [];

        public bool IsEnabled { get; } = enabled;

        public void Seed(TileCacheEntry entry) => _entries.Add(entry);

        public Task RecordAccessAsync(string key, long sizeBytes, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<TileCacheEntry>> SnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TileCacheEntry>>([.. _entries]);

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            Removed.Add(key);
            return Task.CompletedTask;
        }
    }
}

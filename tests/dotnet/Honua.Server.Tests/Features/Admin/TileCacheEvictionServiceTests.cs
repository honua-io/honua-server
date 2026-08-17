// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Tiles;
using Honua.Server.Features.Admin.TileOperations;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

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

    [UnitTest]
    public async Task SweepAsync_WhenIndexIsUnavailable_ReportsIndexFaultAndEvictsNothing()
    {
        // A Redis outage must not be reported the same way as a deliberately disabled evictor:
        // both evict nothing, but only one is a fault the operator has to act on.
        var index = new FakeTileCacheKeyIndex(enabled: true) { SnapshotAvailable = false };
        index.Seed(new TileCacheEntry("oldest", 100, DateTimeOffset.UtcNow.AddMinutes(-30)));
        var storage = Substitute.For<ICloudFileStorage>();
        var service = CreateService(
            index,
            new TileCacheEvictionOptions { Enabled = true, MaxBytes = 1 },
            storage);

        var result = await service.SweepAsync(CancellationToken.None);

        result.Enabled.Should().BeTrue();
        result.IndexAvailable.Should().BeFalse();
        result.Evicted.Should().Be(0);
        index.Removed.Should().BeEmpty();
        await storage.DidNotReceive()
            .DeleteIfMatchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await storage.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task SweepAsync_AtExactlyTheEntryQuota_EvictsNothing()
    {
        // Pins the quota comparison at the boundary: MaxEntries is an inclusive ceiling, so a
        // cache sitting exactly on it is within quota. Without this, `>` and `>=` are
        // indistinguishable to the suite and an off-by-one would silently evict a live tile.
        var now = DateTimeOffset.UtcNow;
        var index = new FakeTileCacheKeyIndex(enabled: true);
        index.Seed(new TileCacheEntry("a", 10, now.AddMinutes(-3)));
        index.Seed(new TileCacheEntry("b", 10, now.AddMinutes(-2)));
        index.Seed(new TileCacheEntry("c", 10, now.AddMinutes(-1)));

        var service = CreateService(index, eviction: new TileCacheEvictionOptions
        {
            Enabled = true,
            MaxEntries = 3
        });

        var result = await service.SweepAsync(CancellationToken.None);

        result.Enabled.Should().BeTrue();
        result.Scanned.Should().Be(3);
        result.Evicted.Should().Be(0);
        index.Removed.Should().BeEmpty();
    }

    [UnitTest]
    public async Task SweepAsync_AtExactlyTheByteQuota_EvictsNothing()
    {
        var now = DateTimeOffset.UtcNow;
        var index = new FakeTileCacheKeyIndex(enabled: true);
        index.Seed(new TileCacheEntry("oldest", 100, now.AddMinutes(-30)));
        index.Seed(new TileCacheEntry("newest", 100, now.AddMinutes(-1)));

        var service = CreateService(index, eviction: new TileCacheEvictionOptions
        {
            Enabled = true,
            MaxBytes = 200
        });

        var result = await service.SweepAsync(CancellationToken.None);

        result.Evicted.Should().Be(0);
        index.Removed.Should().BeEmpty();
    }

    [UnitTest]
    public async Task SweepAsync_WhenConditionalDeleteMissesNewerGeneration_LeavesKeyTracked()
    {
        var index = new FakeTileCacheKeyIndex(enabled: true);
        index.Seed(new TileCacheEntry("oldest", 100, DateTimeOffset.UtcNow.AddMinutes(-30)));
        var storage = Substitute.For<ICloudFileStorage>();
        storage.GetMetadataAsync("oldest", Arg.Any<CancellationToken>()).Returns(
            StoredTile("oldest", "etag-old"),
            StoredTile("oldest", "etag-new"));
        storage.DeleteIfMatchAsync("oldest", "etag-old", Arg.Any<CancellationToken>()).Returns(false);
        var service = CreateService(
            index,
            new TileCacheEvictionOptions { Enabled = true, MaxBytes = 50 },
            storage);

        var result = await service.SweepAsync(CancellationToken.None);

        result.Evicted.Should().Be(0);
        index.Removed.Should().BeEmpty();
        await storage.Received(1)
            .DeleteIfMatchAsync("oldest", "etag-old", Arg.Any<CancellationToken>());
        await storage.DidNotReceive().DeleteAsync("oldest", Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task SweepAsync_WhenVictimWasReplacedBeforeFence_DoesNotDeleteFreshObject()
    {
        var index = new FakeTileCacheKeyIndex(enabled: true) { RejectCurrent = true };
        index.Seed(new TileCacheEntry(
            "oldest",
            100,
            DateTimeOffset.UtcNow.AddMinutes(-30),
            WriteVersion: "snapshot-version"));
        var storage = Substitute.For<ICloudFileStorage>();
        var service = CreateService(
            index,
            new TileCacheEvictionOptions { Enabled = true, MaxBytes = 50 },
            storage);

        var result = await service.SweepAsync(CancellationToken.None);

        result.Evicted.Should().Be(0);
        index.Removed.Should().BeEmpty();
        await storage.DidNotReceive().GetMetadataAsync("oldest", Arg.Any<CancellationToken>());
        await storage.DidNotReceive()
            .DeleteIfMatchAsync("oldest", Arg.Any<string>(), Arg.Any<CancellationToken>());
        await storage.DidNotReceive().DeleteAsync("oldest", Arg.Any<CancellationToken>());
    }

    private static CloudFile StoredTile(string key, string eTag) => new()
    {
        FileId = key,
        FileName = "tile.png",
        StoragePath = key,
        ContentType = "image/png",
        SizeBytes = 100,
        UploadedAt = DateTimeOffset.UtcNow,
        ETag = eTag,
        Provider = CloudStorageProvider.Local,
    };

    private static TileCacheEvictionService CreateService(
        FakeTileCacheKeyIndex index,
        TileCacheEvictionOptions eviction,
        ICloudFileStorage? storage = null)
    {
        var tileOptions = Options.Create(new Honua.Core.Features.Tiles.TileOptions
        {
            Eviction = eviction
        });

        return new TileCacheEvictionService(
            index,
            tileOptions,
            NullLogger<TileCacheEvictionService>.Instance,
            storage);
    }

    private sealed class FakeTileCacheKeyIndex(bool enabled) : ITileCacheKeyIndex, ITileCacheMutationCoordinator
    {
        private readonly List<TileCacheEntry> _entries = [];

        public ConcurrentBag<string> Removed { get; } = [];

        public bool IsEnabled { get; } = enabled;

        public bool RejectCurrent { get; init; }

        public bool SnapshotAvailable { get; init; } = true;

        public void Seed(TileCacheEntry entry) => _entries.Add(entry);

        public Task RecordAccessAsync(
            string key,
            long sizeBytes,
            DateTimeOffset? expiresAt,
            string? tenantScope = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RecordWriteAsync(
            string key,
            long sizeBytes,
            DateTimeOffset expiresAt,
            string? tenantScope = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> IsExpiredAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<bool> MarkExpiredAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<IReadOnlyList<TileCacheEntry>> SnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TileCacheEntry>>([.. _entries]);

        public Task<TileCacheIndexSnapshot> SnapshotWithStatusAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(new TileCacheIndexSnapshot([.. _entries], SnapshotAvailable));

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            Removed.Add(key);
            return Task.CompletedTask;
        }

        public Task ExecuteSerializedAsync(
            string key,
            Func<TileCacheMutationContext, Task> mutation,
            CancellationToken cancellationToken = default)
            => mutation(new TileCacheMutationContext(
                cancellationToken,
                CancellationToken.None));

        public Task<bool> IsCurrentAsync(
            TileCacheEntry entry,
            CancellationToken cancellationToken = default)
            => Task.FromResult(!RejectCurrent);

        public Task<TileCacheExpirationMarkResult> TryMarkExpiredIfCurrentAsync(
            TileCacheEntry entry,
            CancellationToken cancellationToken = default)
            => Task.FromResult(RejectCurrent
                ? TileCacheExpirationMarkResult.NotCurrent
                : TileCacheExpirationMarkResult.Added);
    }
}

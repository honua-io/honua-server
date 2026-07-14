// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Tiles;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Unit coverage for the generated tile-cache generation checkpoint store contract (issue #2661):
/// Load/Save/Delete round-trip and the deterministic bounding of the persisted failed-unit set.
/// </summary>
public sealed class TileCacheGenerationCheckpointStoreTests
{
    [Fact]
    public async Task SaveLoadDelete_RoundTrips()
    {
        var store = new InMemoryTileCacheGenerationCheckpointStore();
        var checkpoint = new TileCacheGenerationCheckpoint
        {
            GenerationId = "gen-1",
            Operation = "seed",
            CompletedMetatileBlocks = 3,
            CompletedUnitCount = 12,
            FailedUnitCount = 2,
            FailedUnits = ["1/5/2/3", "1/5/4/6"],
            CapturedAt = DateTimeOffset.UtcNow,
            Attempt = 2
        };

        await store.SaveAsync(checkpoint);

        var loaded = await store.LoadAsync("gen-1");
        loaded.Should().NotBeNull();
        loaded!.CompletedMetatileBlocks.Should().Be(3);
        loaded.CompletedUnitCount.Should().Be(12);
        loaded.FailedUnits.Should().Equal("1/5/2/3", "1/5/4/6");
        loaded.Attempt.Should().Be(2);

        (await store.DeleteAsync("gen-1")).Should().BeTrue();
        (await store.LoadAsync("gen-1")).Should().BeNull();
        (await store.DeleteAsync("gen-1")).Should().BeFalse("a second delete finds nothing to remove");
    }

    [Fact]
    public async Task Save_BoundsFailedUnitSet_ToDeterministicUpperBound()
    {
        var store = new InMemoryTileCacheGenerationCheckpointStore();
        var oversized = Enumerable
            .Range(0, TileCacheGenerationCheckpointBounds.MaxFailedUnits + 500)
            .Select(i => $"1/10/{i}/0")
            .ToArray();

        await store.SaveAsync(new TileCacheGenerationCheckpoint
        {
            GenerationId = "gen-bounded",
            Operation = "seed",
            CompletedMetatileBlocks = 1,
            CompletedUnitCount = 0,
            FailedUnitCount = oversized.Length,
            FailedUnits = oversized,
            CapturedAt = DateTimeOffset.UtcNow
        });

        var loaded = await store.LoadAsync("gen-bounded");
        loaded!.FailedUnits.Count.Should().Be(
            TileCacheGenerationCheckpointBounds.MaxFailedUnits,
            "the store truncates the failed-unit set so persisted state stays release-safe");
    }

    [Fact]
    public void Sanitize_ClampsNegativeCountsAndAttempt_AndDeduplicates()
    {
        var sanitized = TileCacheGenerationCheckpointBounds.Sanitize(new TileCacheGenerationCheckpoint
        {
            GenerationId = "  gen-2  ",
            Operation = "warm",
            CompletedMetatileBlocks = -5,
            CompletedUnitCount = -1,
            FailedUnitCount = -3,
            FailedUnits = ["1/1/1/1", "1/1/1/1", "  ", "1/1/1/2"],
            CapturedAt = DateTimeOffset.UtcNow,
            Attempt = 0
        });

        sanitized.GenerationId.Should().Be("gen-2");
        sanitized.CompletedMetatileBlocks.Should().Be(0);
        sanitized.CompletedUnitCount.Should().Be(0);
        sanitized.FailedUnitCount.Should().Be(0);
        sanitized.Attempt.Should().Be(1);
        sanitized.FailedUnits.Should().Equal("1/1/1/1", "1/1/1/2");
    }
}

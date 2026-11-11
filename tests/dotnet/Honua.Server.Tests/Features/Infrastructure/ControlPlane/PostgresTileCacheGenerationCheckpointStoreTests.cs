// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Tiles;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresTileCacheGenerationCheckpointStoreTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task LoadAsync_OnAnotherStore_ReturnsTheSavedCheckpoint()
    {
        await using var harness = await CreateHarnessAsync();
        var first = Checkpoint();
        var latest = first with { CompletedMetatileBlocks = 7, CompletedUnitCount = 448, FailedUnits = ["4/2/3"], FailedUnitCount = 1, Attempt = 2 };
        await harness.CreateTileCheckpointStore().SaveAsync(first);
        await harness.CreateTileCheckpointStore().SaveAsync(latest);

        var loaded = await harness.CreateTileCheckpointStore().LoadAsync(first.GenerationId);

        loaded.Should().BeEquivalentTo(latest);
        (await harness.CreateTileCheckpointStore().LoadAsync("no-such-generation")).Should().BeNull();
    }

    [IntegrationTest]
    public async Task SaveAsync_FailedUnitsOverTheBound_StoresTheSanitizedCheckpoint()
    {
        await using var harness = await CreateHarnessAsync();
        var checkpoint = Checkpoint() with
        {
            FailedUnits = Enumerable.Range(0, TileCacheGenerationCheckpointBounds.MaxFailedUnits + 10).Select(unit => $"9/{unit}/0").ToArray()
        };
        await harness.CreateTileCheckpointStore().SaveAsync(checkpoint);

        var loaded = await harness.CreateTileCheckpointStore().LoadAsync(checkpoint.GenerationId);

        loaded!.FailedUnits.Should().HaveCount(TileCacheGenerationCheckpointBounds.MaxFailedUnits);
    }

    [IntegrationTest]
    public async Task DeleteAsync_OnAnotherStore_RemovesTheCheckpointOnce()
    {
        await using var harness = await CreateHarnessAsync();
        var checkpoint = Checkpoint();
        await harness.CreateTileCheckpointStore().SaveAsync(checkpoint);

        (await harness.CreateTileCheckpointStore().DeleteAsync(checkpoint.GenerationId)).Should().BeTrue();
        (await harness.CreateTileCheckpointStore().DeleteAsync(checkpoint.GenerationId)).Should().BeFalse();
        (await harness.CreateTileCheckpointStore().LoadAsync(checkpoint.GenerationId)).Should().BeNull();
    }

    [IntegrationTest]
    public async Task LoadAsync_UnreadableRecord_FailsAndKeepsTheRow()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateTileCheckpointStore();
        var checkpoint = Checkpoint();
        await store.SaveAsync(checkpoint);
        await harness.ExecuteAsync(
            $"UPDATE {harness.Table("tile_generation_checkpoints")} SET record = '[1]' WHERE generation_id = @id",
            new NpgsqlParameter("id", checkpoint.GenerationId));

        await store.Invoking(s => s.LoadAsync(checkpoint.GenerationId).AsTask()).Should().ThrowAsync<InvalidDataException>();
        (await harness.ScalarAsync<long>(
            $"SELECT count(*) FROM {harness.Table("tile_generation_checkpoints")} WHERE generation_id = @id",
            new NpgsqlParameter("id", checkpoint.GenerationId))).Should().Be(1);
    }

    [IntegrationTest]
    public async Task LoadAsync_AfterRetention_ReturnsNullAndTheSweepDeletesTheCheckpoint()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateTileCheckpointStore();
        var expired = Checkpoint();
        var kept = Checkpoint();
        await store.SaveAsync(expired);
        await store.SaveAsync(kept);
        await harness.ExecuteAsync(
            $"UPDATE {harness.Table("tile_generation_checkpoints")} SET expires_at = now() - interval '1 second' WHERE generation_id = @id",
            new NpgsqlParameter("id", expired.GenerationId));

        (await store.LoadAsync(expired.GenerationId)).Should().BeNull();
        (await store.DeleteExpiredAsync(batchSize: 100, CancellationToken.None)).Should().Be(1);
        (await store.LoadAsync(kept.GenerationId)).Should().NotBeNull();
    }

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresTileCacheGenerationCheckpointStoreTests));

    private static TileCacheGenerationCheckpoint Checkpoint()
        => new()
        {
            GenerationId = $"generation-{Guid.NewGuid():N}",
            Operation = "seed",
            CompletedMetatileBlocks = 3,
            CompletedUnitCount = 192,
            FailedUnits = [],
            CapturedAt = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero),
        };
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Collaboration.Operations;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using TestOperations = Honua.TestKit.Constants.Operations;

namespace Honua.Core.Tests.Features.Collaboration.Operations;

[Protocol(ProtocolNames.TestQuality)]
public sealed class InMemorySavedMapOperationLogRepositoryTests
{
    private static readonly DateTimeOffset _acceptedAt = new(2026, 5, 11, 10, 0, 0, TimeSpan.Zero);

    [UnitTest]
    [Operation(TestOperations.Append)]
    public async Task AppendAsync_NewOperations_AssignsMonotonicServerCursors()
    {
        var repository = CreateRepository();

        var first = await repository.AppendAsync(CreateRequest("op-1", SavedMapOperationCursor.Empty));
        var second = await repository.AppendAsync(CreateRequest("op-2", new SavedMapOperationCursor(1)));

        Assert.Equal(SavedMapOperationAppendStatus.Accepted, first.Status);
        Assert.Equal(1, first.Operation?.ServerCursor.Value);
        Assert.Equal(_acceptedAt, first.Operation?.AcceptedAt);
        Assert.Equal(SavedMapOperationAppendStatus.Accepted, second.Status);
        Assert.Equal(2, second.Operation?.ServerCursor.Value);
        Assert.Equal(2, second.HeadCursor.Value);
    }

    [UnitTest]
    [Operation(TestOperations.Append)]
    public async Task AppendAsync_DuplicateOperationId_ReturnsOriginalOperationWithoutAdvancingHead()
    {
        var repository = CreateRepository();

        var first = await repository.AppendAsync(CreateRequest("op-1", SavedMapOperationCursor.Empty));
        var duplicate = await repository.AppendAsync(CreateRequest(
            "op-1",
            SavedMapOperationCursor.Empty,
            kind: SavedMapOperationKind.PatchStyle));

        Assert.Equal(SavedMapOperationAppendStatus.Accepted, duplicate.Status);
        Assert.True(duplicate.IsDuplicate);
        Assert.Equal(first.Operation?.ServerCursor, duplicate.Operation?.ServerCursor);
        Assert.Equal(first.Operation?.Kind, duplicate.Operation?.Kind);
        Assert.Equal(1, duplicate.HeadCursor.Value);
    }

    [UnitTest]
    [Operation(TestOperations.Append)]
    public async Task AppendAsync_DuplicateIdempotencyKey_ReturnsOriginalOperationWithoutAdvancingHead()
    {
        var repository = CreateRepository();

        var first = await repository.AppendAsync(CreateRequest(
            "op-1",
            SavedMapOperationCursor.Empty,
            idempotencyKey: "idem-1"));
        var duplicate = await repository.AppendAsync(CreateRequest(
            "op-2",
            new SavedMapOperationCursor(1),
            idempotencyKey: "idem-1"));

        Assert.Equal(SavedMapOperationAppendStatus.Accepted, duplicate.Status);
        Assert.True(duplicate.IsDuplicate);
        Assert.Equal(first.Operation?.OperationId, duplicate.Operation?.OperationId);
        Assert.Equal(1, duplicate.HeadCursor.Value);
    }

    [UnitTest]
    [Operation(TestOperations.Query)]
    public async Task ReplayAsync_SinceCursor_ReturnsOrderedOperationsAfterCursor()
    {
        var repository = CreateRepository();
        await repository.AppendAsync(CreateRequest("op-1", SavedMapOperationCursor.Empty));
        await repository.AppendAsync(CreateRequest("op-2", new SavedMapOperationCursor(1)));
        await repository.AppendAsync(CreateRequest("op-3", new SavedMapOperationCursor(2)));

        var replay = await repository.ReplayAsync(new SavedMapId("map-1"), new SavedMapOperationCursor(1));

        Assert.Equal(SavedMapOperationReplayStatus.Ok, replay.Status);
        Assert.Equal(3, replay.HeadCursor.Value);
        Assert.Equal([2L, 3L], replay.Operations.Select(operation => operation.ServerCursor.Value).ToArray());
        Assert.Equal(["op-2", "op-3"], replay.Operations.Select(operation => operation.OperationId.Value).ToArray());
    }

    [UnitTest]
    [Operation(TestOperations.Query)]
    public async Task ReplayAsync_UnknownOrStaleCursor_ReturnsResyncRequired()
    {
        var repository = CreateRepository(retainedOperationCount: 2);
        await repository.AppendAsync(CreateRequest("op-1", SavedMapOperationCursor.Empty));
        await repository.AppendAsync(CreateRequest("op-2", new SavedMapOperationCursor(1)));
        await repository.AppendAsync(CreateRequest("op-3", new SavedMapOperationCursor(2)));

        var stale = await repository.ReplayAsync(new SavedMapId("map-1"), SavedMapOperationCursor.Empty);
        var unknownAhead = await repository.ReplayAsync(new SavedMapId("map-1"), new SavedMapOperationCursor(99));

        Assert.Equal(SavedMapOperationReplayStatus.ResyncRequired, stale.Status);
        Assert.Equal(1, stale.MinimumReplayCursor.Value);
        Assert.Equal(SavedMapOperationReplayStatus.ResyncRequired, unknownAhead.Status);
        Assert.Equal(3, unknownAhead.HeadCursor.Value);
    }

    [UnitTest]
    [Operation(TestOperations.Append)]
    public async Task AppendAsync_SafeConcurrentOperation_AppendsAfterStaleBase()
    {
        var repository = CreateRepository();
        await repository.AppendAsync(CreateRequest("op-1", SavedMapOperationCursor.Empty, kind: SavedMapOperationKind.SetViewport));

        var result = await repository.AppendAsync(CreateRequest(
            "op-2",
            SavedMapOperationCursor.Empty,
            kind: SavedMapOperationKind.PatchStyle));

        Assert.Equal(SavedMapOperationAppendStatus.Accepted, result.Status);
        Assert.Equal(2, result.Operation?.ServerCursor.Value);
    }

    [UnitTest]
    [Operation(TestOperations.Append)]
    public async Task AppendAsync_UnsafeConcurrentOperation_ReturnsConflictResponse()
    {
        var repository = CreateRepository();
        await repository.AppendAsync(CreateRequest(
            "op-1",
            SavedMapOperationCursor.Empty,
            kind: SavedMapOperationKind.SetViewport));

        var result = await repository.AppendAsync(CreateRequest(
            "op-2",
            SavedMapOperationCursor.Empty,
            kind: SavedMapOperationKind.ReplaceWebMapDocument));

        Assert.Equal(SavedMapOperationAppendStatus.Conflict, result.Status);
        Assert.NotNull(result.Conflict);
        Assert.Equal("saved-map.operation.conflict", result.Conflict.Code);
        Assert.Equal(SavedMapOperationCursor.Empty, result.Conflict.BaseCursor);
        Assert.Equal(1, result.Conflict.HeadCursor.Value);
        Assert.Equal("op-1", Assert.Single(result.Conflict.ConflictingOperations).OperationId.Value);
    }

    [UnitTest]
    [Operation(TestOperations.Append)]
    public async Task AppendAsync_BaseCursorOutsideReplayWindow_ReturnsResyncRequired()
    {
        var repository = CreateRepository(retainedOperationCount: 1);
        await repository.AppendAsync(CreateRequest("op-1", SavedMapOperationCursor.Empty));
        await repository.AppendAsync(CreateRequest("op-2", new SavedMapOperationCursor(1)));

        var result = await repository.AppendAsync(CreateRequest("op-3", SavedMapOperationCursor.Empty));

        Assert.Equal(SavedMapOperationAppendStatus.ResyncRequired, result.Status);
        Assert.Equal(2, result.HeadCursor.Value);
    }

    [UnitTest]
    [Operation(TestOperations.Append)]
    public async Task AppendAsync_PrunesDedupeIndexesWithReplayWindow()
    {
        var repository = CreateRepository(retainedOperationCount: 1);
        await repository.AppendAsync(CreateRequest(
            "op-1",
            SavedMapOperationCursor.Empty,
            idempotencyKey: "idem-1"));
        await repository.AppendAsync(CreateRequest("op-2", new SavedMapOperationCursor(1)));

        var duplicatePruned = await repository.AppendAsync(CreateRequest(
            "op-1",
            SavedMapOperationCursor.Empty,
            idempotencyKey: "idem-1"));

        Assert.Equal(SavedMapOperationAppendStatus.ResyncRequired, duplicatePruned.Status);
        Assert.False(duplicatePruned.IsDuplicate);
    }

    private static InMemorySavedMapOperationLogRepository CreateRepository(int retainedOperationCount = 512) =>
        new(timeProvider: new FixedTimeProvider(_acceptedAt), retainedOperationCount: retainedOperationCount);

    private static SavedMapOperationAppendRequest CreateRequest(
        string operationId,
        SavedMapOperationCursor baseCursor,
        SavedMapOperationKind kind = SavedMapOperationKind.SetViewport,
        string? idempotencyKey = null) => new()
        {
            OperationId = new SavedMapOperationId(operationId),
            MapId = new SavedMapId("map-1"),
            ActorId = new SavedMapActorId("actor-1"),
            BaseCursor = baseCursor,
            Kind = kind,
            Payload = JsonSerializer.SerializeToElement(new { operationId, kind = kind.ToString() }),
            IdempotencyKey = idempotencyKey,
        };

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

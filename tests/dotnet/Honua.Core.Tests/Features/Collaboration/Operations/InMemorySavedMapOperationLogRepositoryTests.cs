// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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
    [Operation(TestOperations.Query)]
    public void DurabilityCapabilities_ProcessLocalStore_FailClosedForCheckpointing()
    {
        var repository = CreateRepository();

        Assert.False(repository.SupportsReplicaSharedReplay);
        Assert.False(repository.SupportsRestartDurableReplay);
        Assert.False(repository.SupportsRestartDurableCheckpointCursors);
        Assert.False(repository.SupportsRestartDurableCheckpointing);
    }

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
        var duplicate = await repository.AppendAsync(CreateRequest("op-1", SavedMapOperationCursor.Empty));

        Assert.Equal(SavedMapOperationAppendStatus.Accepted, duplicate.Status);
        Assert.True(duplicate.IsDuplicate);
        Assert.Equal(first.Operation?.ServerCursor, duplicate.Operation?.ServerCursor);
        Assert.Equal(first.Operation?.Kind, duplicate.Operation?.Kind);
        Assert.Equal(1, duplicate.HeadCursor.Value);
    }

    [UnitTest]
    [Operation(TestOperations.Append)]
    public async Task AppendAsync_SameOperationIdDifferentRequest_ReturnsIdempotencyMismatch()
    {
        var repository = CreateRepository();
        var first = await repository.AppendAsync(CreateRequest("op-1", SavedMapOperationCursor.Empty));

        var mismatch = await repository.AppendAsync(CreateRequest(
            "op-1",
            SavedMapOperationCursor.Empty,
            kind: SavedMapOperationKind.PatchStyle));

        Assert.Equal(SavedMapOperationAppendStatus.IdempotencyMismatch, mismatch.Status);
        Assert.False(mismatch.IsDuplicate);
        Assert.Equal(first.Operation?.ServerCursor, mismatch.Operation?.ServerCursor);
        Assert.Equal(1, mismatch.HeadCursor.Value);
        var replay = await repository.ReplayAsync(new SavedMapId("map-1"), SavedMapOperationCursor.Empty);
        Assert.Single(replay.Operations);
    }

    [UnitTest]
    [Operation(TestOperations.Append)]
    public async Task AppendAsync_SameOperationIdDifferentActor_AppendsIndependently()
    {
        var repository = CreateRepository();
        var first = await repository.AppendAsync(CreateRequest("op-1", SavedMapOperationCursor.Empty));
        var other = await repository.AppendAsync(CreateRequest(
            "op-1",
            new SavedMapOperationCursor(1),
            actorId: "actor-2"));

        Assert.Equal(SavedMapOperationAppendStatus.Accepted, first.Status);
        Assert.Equal(SavedMapOperationAppendStatus.Accepted, other.Status);
        Assert.False(other.IsDuplicate);
        Assert.Equal(2, other.HeadCursor.Value);
        Assert.Equal("actor-2", other.Operation?.ActorId.Value);
    }

    [UnitTest]
    [Operation(TestOperations.Append)]
    public async Task AppendAsync_DuplicatePayload_IgnoresObjectKeyOrder()
    {
        var repository = CreateRepository();
        var firstPayload = JsonSerializer.SerializeToElement(new { zoom = 8, bearing = 1 });
        using var reordered = JsonDocument.Parse("""{"bearing":1,"zoom":8}""");
        await repository.AppendAsync(CreateRequest("op-1", SavedMapOperationCursor.Empty, payload: firstPayload));

        var duplicate = await repository.AppendAsync(CreateRequest(
            "op-1",
            SavedMapOperationCursor.Empty,
            payload: reordered.RootElement.Clone()));

        Assert.Equal(SavedMapOperationAppendStatus.Accepted, duplicate.Status);
        Assert.True(duplicate.IsDuplicate);
        Assert.Equal(1, duplicate.HeadCursor.Value);
    }

    [UnitTheory]
    [InlineData("""{"quote\"":1}""", "quote\"")]
    [InlineData("""{"slash\\":1}""", "slash\\")]
    [InlineData("""{"line\n\t\u0000":1}""", "line\n\t\0")]
    [InlineData("""{"café🌍":1}""", "café🌍")]
    [InlineData("""{"<>&":1}""", "<>&")]
    [Operation(TestOperations.Query)]
    public void CanonicalPayload_EscapedPropertyName_RoundTripsWithoutChangingIdentity(string json, string propertyName)
    {
        using var original = JsonDocument.Parse(json);

        var canonical = SavedMapOperationIdempotency.CanonicalPayload(original.RootElement);

        using var parsed = JsonDocument.Parse(canonical);
        Assert.Equal(1, parsed.RootElement.GetProperty(propertyName).GetInt32());
        Assert.Single(parsed.RootElement.EnumerateObject());
        Assert.Equal(canonical, SavedMapOperationIdempotency.CanonicalPayload(parsed.RootElement));
    }

    [UnitTheory]
    [InlineData("""{"z":[{"é":1,"a":2}],"a":0}""", """{"a":0,"z":[{"a":2,"\u00e9":1}]}""", true)]
    [InlineData("""{"quote\"":1}""", """{"quote\\":1}""", false)]
    [InlineData("""{"a":[1,2]}""", """{"a":[2,1]}""", false)]
    [InlineData("""{"a":1}""", """{"a":1.0}""", false)]
    [Operation(TestOperations.Append)]
    public async Task AppendAsync_CanonicalPayload_PreservesEscapingAndEqualityRules(string originalJson, string retryJson, bool equivalent)
    {
        var repository = CreateRepository();
        using var original = JsonDocument.Parse(originalJson);
        using var retry = JsonDocument.Parse(retryJson);
        await repository.AppendAsync(CreateRequest("op-1", SavedMapOperationCursor.Empty, payload: original.RootElement.Clone()));

        var result = await repository.AppendAsync(CreateRequest("op-1", SavedMapOperationCursor.Empty, payload: retry.RootElement.Clone()));

        Assert.Equal(equivalent ? SavedMapOperationAppendStatus.Accepted : SavedMapOperationAppendStatus.IdempotencyMismatch, result.Status);
        Assert.Equal(equivalent, result.IsDuplicate);
        Assert.Equal(1, result.HeadCursor.Value);
    }

    [UnitTest]
    [Operation(TestOperations.Append)]
    public async Task AppendAsync_DuplicateIdempotencyKey_ReturnsOriginalOperationWithoutAdvancingHead()
    {
        var repository = CreateRepository();

        var payload = JsonSerializer.SerializeToElement(new { zoom = 8 });
        var first = await repository.AppendAsync(CreateRequest(
            "op-1",
            SavedMapOperationCursor.Empty,
            idempotencyKey: "idem-1",
            payload: payload));
        var duplicate = await repository.AppendAsync(CreateRequest(
            "op-2",
            new SavedMapOperationCursor(1),
            idempotencyKey: "idem-1",
            payload: payload));

        Assert.Equal(SavedMapOperationAppendStatus.Accepted, duplicate.Status);
        Assert.True(duplicate.IsDuplicate);
        Assert.Equal(first.Operation?.OperationId, duplicate.Operation?.OperationId);
        Assert.Equal(1, duplicate.HeadCursor.Value);

        var mismatch = await repository.AppendAsync(CreateRequest(
            "op-3",
            new SavedMapOperationCursor(1),
            idempotencyKey: "idem-1",
            payload: JsonSerializer.SerializeToElement(new { zoom = 9 })));
        Assert.Equal(SavedMapOperationAppendStatus.IdempotencyMismatch, mismatch.Status);
        Assert.Equal(1, mismatch.HeadCursor.Value);
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
    [Operation(TestOperations.Query)]
    public async Task ReplayPendingCheckpointAsync_RecordedCursor_ReplaysOnlyNewSuffix()
    {
        var repository = CreateRepository();
        await repository.AppendAsync(CreateRequest("op-1", SavedMapOperationCursor.Empty));
        await repository.AppendAsync(CreateRequest("op-2", new SavedMapOperationCursor(1)));
        await repository.RecordCheckpointAsync(new SavedMapId("map-1"), new SavedMapOperationCursor(2));
        await repository.AppendAsync(CreateRequest("op-3", new SavedMapOperationCursor(2)));

        var replay = await repository.ReplayPendingCheckpointAsync(new SavedMapId("map-1"));

        Assert.Equal(SavedMapOperationReplayStatus.Ok, replay.Status);
        Assert.Equal(2, replay.SinceCursor.Value);
        Assert.Equal("op-3", Assert.Single(replay.Operations).OperationId.Value);
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
        string? idempotencyKey = null,
        string actorId = "actor-1",
        JsonElement? payload = null) => new()
        {
            OperationId = new SavedMapOperationId(operationId),
            MapId = new SavedMapId("map-1"),
            ActorId = new SavedMapActorId(actorId),
            BaseCursor = baseCursor,
            Kind = kind,
            Payload = payload ?? JsonSerializer.SerializeToElement(new { operationId, kind = kind.ToString() }),
            IdempotencyKey = idempotencyKey,
        };

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

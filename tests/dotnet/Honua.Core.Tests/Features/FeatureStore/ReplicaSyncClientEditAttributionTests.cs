// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.FeatureStore;

/// <summary>
/// Regression coverage for how a durable conflict records whether the client edit actually landed
/// (#2430). Getting this wrong is not cosmetic: the flag is the input the resolution planner uses to
/// decide whether keeping the server needs a write, so a conflict wrongly marked "not applied" lets a
/// keep-server resolution plan a no-op and mark itself resolved while the client's overwrite is still
/// committed.
/// </summary>
public sealed class ReplicaSyncClientEditAttributionTests
{
    [UnitTest]
    public async Task ApplyUpload_MixedOutcomeBatch_AttributesClientEditPerEditNotPerLayer()
    {
        // With rollbackOnFailure=false the shared edit pipeline commits rows independently, so the
        // layer-wide Failed flag is true even though the conflicting edit itself committed. Only the
        // per-edit committed ids may drive the flag.
        var tracker = new RecordingChangeTracker(
            ServerChange(objectId: 42),
            ServerChange(objectId: 43));
        var repository = new RecordingConflictRepository();
        var service = new ReplicaSyncService(tracker, repository, NullLogger<ReplicaSyncService>.Instance);

        // 42 commits; 43 is rejected by the provider. The applier reports the layer as failed but
        // attributes 42 as committed.
        var applier = new PartialFailureEditApplier(committedEditIndexes: [0], failed: true);
        var report = await service.ApplyUploadAsync(
            CreateRequest(
                ImmutableArray.Create(
                    new ReplicaUploadEdit(FeatureEditOperationKind.Update, ObjectId: 42, Payload: null),
                    new ReplicaUploadEdit(FeatureEditOperationKind.Update, ObjectId: 43, Payload: null))),
            applier);

        report.Conflicts.Should().HaveCount(2);
        report.Conflicts.Single(c => c.ObjectId == 42).Applied.Should().BeTrue(
            "the conflicting edit for 42 committed even though a sibling row failed");
        report.Conflicts.Single(c => c.ObjectId == 43).Applied.Should().BeFalse(
            "the conflicting edit for 43 never committed");

        // Every conflict on the layer is stamped with the resolution-base generation; only the one
        // whose own edit committed is also promoted to client-edit-applied.
        repository.DetectionUpdates.Should().HaveCount(2);
        repository.DetectionUpdates.Should().OnlyContain(u => u.ResolutionBaseGeneration != null);
        var promoted = repository.DetectionUpdates.Should().ContainSingle(u => u.ClientEditApplied == true).Subject;
        repository.RecordFor(promoted.ConflictId).ObjectId.Should().Be(42);
    }

    [UnitTest]
    public async Task ApplyUpload_ConflictRecordsStartAsNotApplied()
    {
        // The record is written before the batch runs, so it must start conservative: claiming the
        // client state landed and then failing to commit is the dishonest direction.
        var tracker = new RecordingChangeTracker(ServerChange(objectId: 42));
        var repository = new RecordingConflictRepository();
        var service = new ReplicaSyncService(tracker, repository, NullLogger<ReplicaSyncService>.Instance);

        await service.ApplyUploadAsync(
            CreateRequest(
                ImmutableArray.Create(
                    new ReplicaUploadEdit(FeatureEditOperationKind.Update, ObjectId: 42, Payload: null))),
            new PartialFailureEditApplier(committedEditIndexes: [], failed: true));

        repository.Upserts.Should().ContainSingle();
        repository.Upserts.Single().ClientEditApplied.Should().BeFalse();
        repository.Upserts.Single().StorageLayerId.Should().Be(
            10, "a resolution needs the storage layer id to probe the change log for post-conflict edits");
        repository.DetectionUpdates.Should().NotContain(
            u => u.ClientEditApplied == true, "a failed edit is never promoted");
    }

    [UnitTest]
    public async Task ApplyUpload_PromotionUsesGuardedDetectionUpdate_NotWholeRecordUpsert()
    {
        // An operator can resolve a freshly listed conflict while this post-processing is still
        // running. Rewriting the whole record from a stale read would reopen that resolution, so the
        // promotion must go through the status-guarded, column-scoped update.
        var tracker = new RecordingChangeTracker(ServerChange(objectId: 42));
        var repository = new RecordingConflictRepository();
        var service = new ReplicaSyncService(tracker, repository, NullLogger<ReplicaSyncService>.Instance);

        await service.ApplyUploadAsync(
            CreateRequest(
                ImmutableArray.Create(
                    new ReplicaUploadEdit(FeatureEditOperationKind.Update, ObjectId: 42, Payload: null))),
            new PartialFailureEditApplier(committedEditIndexes: [0], failed: false));

        repository.Upserts.Should().ContainSingle("only the initial detection record is upserted");
        repository.DetectionUpdates.Should().ContainSingle();
        var update = repository.DetectionUpdates.Single();
        update.ClientEditApplied.Should().BeTrue();
        update.ResolutionBaseGeneration.Should().NotBeNull(
            "the resolution staleness precondition needs the generation the captured states describe");
        update.ConflictType.Should().BeNull("promotion must not overwrite the refined classification");
        update.ClientStateJson.Should().BeNull();
        update.ServerStateJson.Should().BeNull();
    }

    [UnitTest]
    public async Task ApplyUpload_ApplierWithoutPerEditAttribution_LeavesConflictNotApplied()
    {
        // An adapter that cannot attribute per-row outcomes must not have its conflicts promoted:
        // the conservative false makes a later accept-client a real write rather than a no-op.
        var tracker = new RecordingChangeTracker(ServerChange(objectId: 42));
        var repository = new RecordingConflictRepository();
        var service = new ReplicaSyncService(tracker, repository, NullLogger<ReplicaSyncService>.Instance);

        var report = await service.ApplyUploadAsync(
            CreateRequest(
                ImmutableArray.Create(
                    new ReplicaUploadEdit(FeatureEditOperationKind.Update, ObjectId: 42, Payload: null))),
            new PartialFailureEditApplier(committedEditIndexes: null, failed: false));

        report.Conflicts.Single().Applied.Should().BeFalse();
        repository.DetectionUpdates.Should().NotContain(u => u.ClientEditApplied == true);
    }

    [UnitTest]
    public async Task ApplyUpload_RepeatedEditsForOneObject_PromotesTheCommittedRequestSlot()
    {
        // One payload can carry several operations for the same object. With rollbackOnFailure=false a
        // failed first operation alongside a successful second must promote exactly one of that
        // object's conflicts — collapsing the committed ids to a set promoted both, and a later
        // resolution then planned against a state that was never committed.
        var tracker = new RecordingChangeTracker(ServerChange(objectId: 42));
        var repository = new RecordingConflictRepository();
        var service = new ReplicaSyncService(tracker, repository, NullLogger<ReplicaSyncService>.Instance);

        // Slot 1 (the second operation) commits; slot 0 fails.
        var applier = new PartialFailureEditApplier(committedEditIndexes: [1], failed: true);
        var report = await service.ApplyUploadAsync(
            CreateRequest(
                ImmutableArray.Create(
                    new ReplicaUploadEdit(FeatureEditOperationKind.Update, ObjectId: 42, Payload: null),
                    new ReplicaUploadEdit(FeatureEditOperationKind.Delete, ObjectId: 42, Payload: null))),
            applier);

        report.Conflicts.Should().HaveCount(2, "both operations conflicted with the same server edit");
        report.Conflicts.Count(c => c.Applied).Should().Be(
            1, "only one of the two operations for object 42 committed");
        report.Conflicts[1].Applied.Should().BeTrue("the committed slot is the second operation");
        report.Conflicts[0].Applied.Should().BeFalse("the failed slot must not be promoted");
        repository.DetectionUpdates.Count(u => u.ClientEditApplied == true).Should().Be(1);
    }

    [UnitTest]
    public async Task ApplyUpload_RepeatedEditsForOneObject_TagsEachConflictWithItsRequestSlot()
    {
        // The adapter attaches the client state envelope per conflict, and a payload can carry several
        // operations for one object. Without the slot, every record for that object ended up holding
        // the last envelope, so accepting an earlier conflict wrote the later edit.
        var tracker = new RecordingChangeTracker(ServerChange(objectId: 42));
        var repository = new RecordingConflictRepository();
        var service = new ReplicaSyncService(tracker, repository, NullLogger<ReplicaSyncService>.Instance);

        var report = await service.ApplyUploadAsync(
            CreateRequest(
                ImmutableArray.Create(
                    new ReplicaUploadEdit(FeatureEditOperationKind.Update, ObjectId: 42, Payload: null),
                    new ReplicaUploadEdit(FeatureEditOperationKind.Update, ObjectId: 42, Payload: null))),
            new PartialFailureEditApplier(committedEditIndexes: [0, 1], failed: false));

        report.Conflicts.Should().HaveCount(2);
        report.Conflicts.Select(c => c.EditIndex).Should().Equal(0, 1);
    }

    [UnitTest]
    public async Task ApplyUpload_TwoCommittedEditsForOneObject_PromotesOnlyTheLastOne()
    {
        // Both updates commit, but only the last leaves its state in the row. Promoting both made
        // accepting the earlier conflict look like a no-op while the feature held the later edit, so
        // acceptClient reported resolved without writing the state the operator reviewed.
        var tracker = new RecordingChangeTracker(ServerChange(objectId: 42));
        var repository = new RecordingConflictRepository();
        var service = new ReplicaSyncService(tracker, repository, NullLogger<ReplicaSyncService>.Instance);

        var report = await service.ApplyUploadAsync(
            CreateRequest(
                ImmutableArray.Create(
                    new ReplicaUploadEdit(FeatureEditOperationKind.Update, ObjectId: 42, Payload: null),
                    new ReplicaUploadEdit(FeatureEditOperationKind.Update, ObjectId: 42, Payload: null))),
            new PartialFailureEditApplier(committedEditIndexes: [0, 1], failed: false));

        report.Conflicts.Should().HaveCount(2);
        report.Conflicts[0].Applied.Should().BeFalse("the first update was superseded by the second");
        report.Conflicts[1].Applied.Should().BeTrue("only the last committed edit remains in the row");
        repository.DetectionUpdates.Count(u => u.ClientEditApplied == true).Should().Be(1);

        // The superseded edit is marked as such: it is NOT a withheld manual-review edit, and reading it
        // as one made keepServer finalize a no-op while the row held the later client update.
        repository.DetectionUpdates.Count(u => u.ClientEditSuperseded == true).Should().Be(1);
    }

    [UnitTest]
    public async Task ApplyUpload_LaterIndeterminateEditForOneObject_ShadowsTheEarlierCommittedOne()
    {
        // rollbackOnFailure=false commits rows independently: the first update lands, the second loses
        // its acknowledgement. Promoting the first as the definite final writer let acceptClient on it
        // finalize as a no-op even though the row may hold the second edit.
        var tracker = new RecordingChangeTracker(ServerChange(objectId: 42));
        var repository = new RecordingConflictRepository();
        var service = new ReplicaSyncService(tracker, repository, NullLogger<ReplicaSyncService>.Instance);

        var report = await service.ApplyUploadAsync(
            CreateRequest(
                ImmutableArray.Create(
                    new ReplicaUploadEdit(FeatureEditOperationKind.Update, ObjectId: 42, Payload: null),
                    new ReplicaUploadEdit(FeatureEditOperationKind.Update, ObjectId: 42, Payload: null))),
            new PartialFailureEditApplier(
                committedEditIndexes: [0], failed: true, indeterminateEditIndexes: [1]));

        report.Conflicts.Should().HaveCount(2);
        report.Conflicts.Should().OnlyContain(c => !c.Applied, "no edit is the definite final writer");
        repository.DetectionUpdates.Should().OnlyContain(u => u.ClientEditApplied == null);
        repository.DetectionUpdates.Count(u => u.ClientEditOutcomeUnknown == true)
            .Should().Be(2, "both the indeterminate edit and the one it may have overwritten are unknown");
    }

    [UnitTest]
    public async Task ApplyUpload_ManualReview_BindsNonConflictingEditsToTheStateDetectionSaw()
    {
        // Manual review promises to withhold a conflicting edit. A server edit committing between the
        // change-log read and the write would otherwise be silently overwritten by an edit detection
        // had judged non-conflicting, so those rows carry the token detection saw and fail instead.
        var tracker = new RecordingChangeTracker();
        var repository = new RecordingConflictRepository();
        var service = new ReplicaSyncService(tracker, repository, NullLogger<ReplicaSyncService>.Instance);
        var applier = new PartialFailureEditApplier(committedEditIndexes: [0], failed: false);

        await service.ApplyUploadAsync(
            CreateRequest(
                ImmutableArray.Create(
                    new ReplicaUploadEdit(FeatureEditOperationKind.Update, ObjectId: 7, Payload: null))) with
            {
                LastWriteWins = false,
            },
            applier,
            serverStateCapturer: new TokenCapturer());

        applier.Preconditions.Should().ContainSingle()
            .Which.Should().Match<FeatureEditPrecondition>(
                p => p.ObjectId == 7 && p.ExpectedStateToken == "token-7");
    }

    [UnitTest]
    public async Task ApplyUpload_ManualReview_WithholdsEditsTargetingAnAbsentRow()
    {
        // Absence cannot be expressed as a precondition, so an edit whose target was gone at capture
        // time is withheld: dispatching it unguarded would update or delete a row inserted under the
        // same object id between the capture and the write.
        var tracker = new RecordingChangeTracker();
        var repository = new RecordingConflictRepository();
        var service = new ReplicaSyncService(tracker, repository, NullLogger<ReplicaSyncService>.Instance);
        var applier = new PartialFailureEditApplier(committedEditIndexes: [], failed: false);

        var report = await service.ApplyUploadAsync(
            CreateRequest(
                ImmutableArray.Create(
                    new ReplicaUploadEdit(FeatureEditOperationKind.Update, ObjectId: 7, Payload: null))) with
            {
                LastWriteWins = false,
            },
            applier,
            serverStateCapturer: new TokenCapturer(absentObjectIds: 7));

        applier.DispatchedEdits.IsDefaultOrEmpty.Should().BeTrue("an unguardable edit must not be dispatched");
        report.Success.Should().BeFalse("the client is told to re-synchronize");
    }

    [UnitTest]
    public async Task ApplyUpload_LastWriteWins_DoesNotBindEditsToAPrecondition()
    {
        // Last-write-wins exists so the client edit wins over concurrent server state; failing it on a
        // concurrent server edit would invert the mode's contract.
        var tracker = new RecordingChangeTracker();
        var repository = new RecordingConflictRepository();
        var service = new ReplicaSyncService(tracker, repository, NullLogger<ReplicaSyncService>.Instance);
        var applier = new PartialFailureEditApplier(committedEditIndexes: [0], failed: false);

        await service.ApplyUploadAsync(
            CreateRequest(
                ImmutableArray.Create(
                    new ReplicaUploadEdit(FeatureEditOperationKind.Update, ObjectId: 7, Payload: null))),
            applier,
            serverStateCapturer: new TokenCapturer());

        applier.Preconditions.Should().BeEmpty();
    }

    /// <summary>Capturer that reports a token per requested feature, minus any it treats as absent.</summary>
    private sealed class TokenCapturer(params long[] absentObjectIds) : IReplicaServerStateCapturer
    {
        public Task<IReadOnlyDictionary<(int PublicLayerId, long ObjectId), string>> CaptureAsync(
            ImmutableArray<ReplicaConflictCaptureTarget> targets,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<(int, long), string>>(
                new Dictionary<(int, long), string>());

        public Task<IReadOnlyDictionary<long, string>> CaptureTokensAsync(
            ImmutableArray<ReplicaConflictCaptureTarget> targets,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<long, string>>(
                targets
                    .Where(t => !absentObjectIds.Contains(t.ObjectId))
                    .ToDictionary(t => t.ObjectId, t => $"token-{t.ObjectId}"));
    }

    [UnitTest]
    public async Task ApplyUpload_ManualReview_PersistsTheClientEnvelopeWithTheConflictRecord()
    {
        // Under manual review the conflicting edit is withheld, so the record is the only copy of the
        // client's intent. Everything after the insert - the server snapshot, the edit batch, the
        // adapter's later state attachment - can be cut short by a disconnect, and a record inserted
        // without its envelope reads as settled once the detection window passes while acceptClient
        // has nothing to apply.
        var tracker = new RecordingChangeTracker(ServerChange(objectId: 42));
        var repository = new RecordingConflictRepository();
        var service = new ReplicaSyncService(tracker, repository, NullLogger<ReplicaSyncService>.Instance);

        await service.ApplyUploadAsync(
            CreateRequest(
                ImmutableArray.Create(
                    new ReplicaUploadEdit(FeatureEditOperationKind.Update, ObjectId: 42, Payload: "client-payload"))) with
            {
                LastWriteWins = false,
            },
            new PartialFailureEditApplier(committedEditIndexes: [], failed: false),
            serverStateCapturer: null,
            clientStateSerializer: new EchoClientStateSerializer());

        repository.Upserts.Should().ContainSingle()
            .Which.ClientStateJson.Should().Be(
                "client-payload", "the envelope is written with the record, not attached afterwards");
    }

    /// <summary>Stands in for the adapter seam that owns the wire shape of a feature.</summary>
    private sealed class EchoClientStateSerializer : IReplicaClientStateSerializer
    {
        public string? Serialize(ReplicaUploadEdit edit) => edit.Payload as string;
    }

    [UnitTest]
    public async Task ApplyUpload_IndeterminateEdit_RecordsTheUnknownCommitOutcome()
    {
        // The writer explicitly says the row MAY have committed. Recording that as a definite
        // not-applied let a later keepServer plan a no-op while the client overwrite may have been in
        // place, so the conflict carries the indeterminate flag instead.
        var tracker = new RecordingChangeTracker(ServerChange(objectId: 42));
        var repository = new RecordingConflictRepository();
        var service = new ReplicaSyncService(tracker, repository, NullLogger<ReplicaSyncService>.Instance);

        var report = await service.ApplyUploadAsync(
            CreateRequest(
                ImmutableArray.Create(
                    new ReplicaUploadEdit(FeatureEditOperationKind.Update, ObjectId: 42, Payload: null))),
            new PartialFailureEditApplier(
                committedEditIndexes: [], failed: true, indeterminateEditIndexes: [0]));

        report.Conflicts.Should().ContainSingle()
            .Which.Applied.Should().BeFalse("an indeterminate edit is not a committed one either");
        repository.DetectionUpdates.Should().ContainSingle()
            .Which.ClientEditOutcomeUnknown.Should().BeTrue();
        repository.DetectionUpdates.Should().OnlyContain(u => u.ClientEditApplied == null);
    }

    [UnitTest]
    public async Task ApplyUpload_DeleteListedBeforeUpdateForOneObject_PromotesTheDelete()
    {
        // The shared edit pipeline groups a batch into creates, then updates, then deletes rather than
        // honouring the listed order, so this upload ends with the row deleted even though the update
        // occupies the later request slot. Ranking by slot promoted the update, and an acceptClient on
        // it then finalized as a no-op while the feature was in fact gone.
        var tracker = new RecordingChangeTracker(ServerChange(objectId: 42));
        var repository = new RecordingConflictRepository();
        var service = new ReplicaSyncService(tracker, repository, NullLogger<ReplicaSyncService>.Instance);

        var report = await service.ApplyUploadAsync(
            CreateRequest(
                ImmutableArray.Create(
                    new ReplicaUploadEdit(FeatureEditOperationKind.Delete, ObjectId: 42, Payload: null),
                    new ReplicaUploadEdit(FeatureEditOperationKind.Update, ObjectId: 42, Payload: null))),
            new PartialFailureEditApplier(committedEditIndexes: [0, 1], failed: false));

        report.Conflicts.Should().HaveCount(2);
        report.Conflicts[0].Applied.Should().BeTrue("deletes execute last, so the delete leaves the row state");
        report.Conflicts[1].Applied.Should().BeFalse("the update executed before the delete that removed the row");
        repository.DetectionUpdates.Count(u => u.ClientEditApplied == true).Should().Be(1);
    }

    [UnitTest]
    public async Task ApplyUpload_ForeignEditAfterTheBatch_DoesNotBecomeTheConflictBaseGeneration()
    {
        // The change feed collapses to the latest change per object, so a foreign edit landing between
        // this batch committing and the change-log probe would be read as the generation our own edit
        // produced. A staleness check starting after it would then never see it, and a later
        // keepServer would overwrite the foreign state with the older snapshot.
        var foreignEdit = ServerChange(objectId: 42) with { Generation = 90 };
        var tracker = new RecordingChangeTracker(foreignEdit) { Generations = new Queue<long>([50L, 60L]) };
        var repository = new RecordingConflictRepository();
        var service = new ReplicaSyncService(tracker, repository, NullLogger<ReplicaSyncService>.Instance);

        await service.ApplyUploadAsync(
            CreateRequest(
                ImmutableArray.Create(
                    new ReplicaUploadEdit(FeatureEditOperationKind.Update, ObjectId: 42, Payload: null))),
            new PartialFailureEditApplier(committedEditIndexes: [0], failed: false));

        repository.DetectionUpdates.Should().ContainSingle()
            .Which.ResolutionBaseGeneration.Should().Be(
                60, "the base is clamped to the watermark taken immediately after the batch");
    }

    private static FeatureChange ServerChange(long objectId) => new()
    {
        ChangeId = objectId,
        Generation = 12,
        LayerId = 10,
        ObjectId = objectId,
        Operation = FeatureChangeOperation.Update,
        ChangedAt = DateTimeOffset.UtcNow,
    };

    private static ReplicaSyncRequest CreateRequest(ImmutableArray<ReplicaUploadEdit> edits) => new(
        ReplicaId: "replica-1",
        ServiceId: "svc-1",
        Direction: ReplicaSyncDirection.Upload,
        BaseGeneration: 10,
        LayerEdits: ImmutableArray.Create(new ReplicaUploadLayerEdits(PublicLayerId: 0, StorageLayerId: 10, edits)));

    private sealed class RecordingChangeTracker(params FeatureChange[] changes) : IChangeTracker
    {
        /// <summary>Successive watermark readings; the last value repeats once the queue drains.</summary>
        public Queue<long>? Generations { get; init; }

        private long _lastGeneration = 99L;

        public Task<long> GetCurrentGenerationAsync(CancellationToken cancellationToken = default)
        {
            if (Generations is { Count: > 0 })
            {
                _lastGeneration = Generations.Dequeue();
            }

            return Task.FromResult(_lastGeneration);
        }

        public Task<IReadOnlyList<FeatureChange>> GetChangesSinceAsync(
            long sinceGeneration,
            int[] layerIds,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FeatureChange>>(changes);

        public Task<IReadOnlyList<FeatureChange>> GetChangesSinceAsync(
            long sinceGeneration,
            int[] layerIds,
            IReadOnlySet<long>? objectIds,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FeatureChange>>(
                objectIds is null ? changes : changes.Where(change => objectIds.Contains(change.ObjectId)).ToArray());
    }

    /// <summary>
    /// Edit applier that reports a layer-wide failure while still attributing the rows that
    /// committed, mirroring the shared pipeline's best-effort per-row behavior.
    /// </summary>
    private sealed class PartialFailureEditApplier(
        int[]? committedEditIndexes,
        bool failed,
        int[]? indeterminateEditIndexes = null) : IReplicaEditApplier
    {
        /// <summary>Preconditions the sync service bound the batch to.</summary>
        public ImmutableArray<FeatureEditPrecondition> Preconditions { get; private set; }

        /// <summary>Edits the sync service actually dispatched.</summary>
        public ImmutableArray<ReplicaUploadEdit> DispatchedEdits { get; private set; }

        public Task<ReplicaLayerApplyResult> ApplyAsync(
            string serviceId,
            int publicLayerId,
            ImmutableArray<ReplicaUploadEdit> edits,
            bool rollbackOnFailure,
            ImmutableArray<FeatureEditPrecondition> preconditions = default,
            CancellationToken cancellationToken = default)
        {
            Preconditions = preconditions;
            DispatchedEdits = edits;
            return Task.FromResult(new ReplicaLayerApplyResult(
                publicLayerId,
                AppliedAdds: 0,
                AppliedUpdates: committedEditIndexes?.Length ?? 0,
                AppliedDeletes: 0,
                Failed: failed,
                FailureMessage: failed ? "partial failure" : null,
                CommittedEditIndexes: committedEditIndexes is null
                    ? default
                    : [.. committedEditIndexes],
                IndeterminateEditIndexes: indeterminateEditIndexes is null
                    ? default
                    : [.. indeterminateEditIndexes]));
        }
    }

    private sealed class RecordingConflictRepository : IReplicaConflictRepository
    {
        private readonly Dictionary<string, ReplicaConflictRecord> _records = [];

        public bool SupportsConflictReview => true;

        public List<ReplicaConflictRecord> Upserts { get; } = [];

        public List<ReplicaConflictDetectionUpdate> DetectionUpdates { get; } = [];

        public ReplicaConflictRecord RecordFor(string conflictId) => _records[conflictId];

        public Task UpsertAsync(ReplicaConflictRecord record, CancellationToken cancellationToken = default)
        {
            Upserts.Add(record);
            _records[record.ConflictId] = record;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ReplicaConflictRecord>> ListByReplicaAsync(
            string replicaId,
            ReplicaConflictStatus? status = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ReplicaConflictRecord>>([.. _records.Values]);

        public Task<ReplicaConflictRecord?> GetAsync(string conflictId, CancellationToken cancellationToken = default)
            => Task.FromResult<ReplicaConflictRecord?>(
                _records.TryGetValue(conflictId, out var record) ? record : null);

        public Task<bool> TryUpdateDetectionStateAsync(
            ReplicaConflictDetectionUpdate update,
            CancellationToken cancellationToken = default)
        {
            DetectionUpdates.Add(update);
            if (!_records.TryGetValue(update.ConflictId, out var record) ||
                record.Status != ReplicaConflictStatus.Pending)
            {
                return Task.FromResult(false);
            }

            _records[update.ConflictId] = record with
            {
                ConflictType = update.ConflictType ?? record.ConflictType,
                ClientStateJson = update.ClientStateJson ?? record.ClientStateJson,
                ServerStateJson = update.ServerStateJson ?? record.ServerStateJson,
                ClientEditApplied = update.ClientEditApplied ?? record.ClientEditApplied,
                ResolutionBaseGeneration = update.ResolutionBaseGeneration ?? record.ResolutionBaseGeneration,
            };
            return Task.FromResult(true);
        }

        public Task<bool> TryUpdateFinalizationStateAsync(
            ReplicaConflictFinalizationUpdate update,
            CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<bool> TryTakeOverClaimAsync(
            string conflictId,
            string resolvedBy,
            ReplicaConflictResolutionAction action,
            DateTimeOffset expectedResolvedAt,
            DateTimeOffset newResolvedAt,
            CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<bool> TryReleaseClaimAsync(
            string conflictId,
            string resolvedBy,
            ReplicaConflictResolutionAction action,
            DateTimeOffset resolvedAt,
            CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<ReplicaConflictResolutionOutcome> ResolveAsync(
            ReplicaConflictResolution resolution,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ReplicaConflictResolutionOutcome(null, Applied: false));
    }
}

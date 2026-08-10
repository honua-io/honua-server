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
        public Task<long> GetCurrentGenerationAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(99L);

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
    private sealed class PartialFailureEditApplier(int[]? committedEditIndexes, bool failed) : IReplicaEditApplier
    {
        public Task<ReplicaLayerApplyResult> ApplyAsync(
            string serviceId,
            int publicLayerId,
            ImmutableArray<ReplicaUploadEdit> edits,
            bool rollbackOnFailure,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ReplicaLayerApplyResult(
                publicLayerId,
                AppliedAdds: 0,
                AppliedUpdates: committedEditIndexes?.Length ?? 0,
                AppliedDeletes: 0,
                Failed: failed,
                FailureMessage: failed ? "partial failure" : null,
                CommittedEditIndexes: committedEditIndexes is null
                    ? default
                    : [.. committedEditIndexes]));
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

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
/// Unit tests proving the canonical replica-sync conflict probe restricts the change-tracker query
/// to the uploaded update/delete object ids, so a long-offline replica's upload does not
/// materialize the entire change log since its base generation.
/// </summary>
public sealed class ReplicaSyncServiceUploadFilterTests
{
    [UnitTest]
    public async Task ApplyUpload_UpdateAndDeleteEdits_PassesUploadedObjectIdsToChangeTracker()
    {
        var tracker = new RecordingChangeTracker(
            new FeatureChange
            {
                ChangeId = 1,
                Generation = 12,
                LayerId = 10,
                ObjectId = 42,
                Operation = FeatureChangeOperation.Update,
                ChangedAt = DateTimeOffset.UtcNow
            });
        var service = CreateService(tracker);
        var edits = ImmutableArray.Create(
            new ReplicaUploadEdit(FeatureEditOperationKind.Create, ObjectId: null, Payload: null),
            new ReplicaUploadEdit(FeatureEditOperationKind.Update, ObjectId: 42, Payload: null),
            new ReplicaUploadEdit(FeatureEditOperationKind.Delete, ObjectId: 7, Payload: null));
        var request = CreateRequest(edits);

        var report = await service.ApplyUploadAsync(request, new CountingEditApplier());

        report.Success.Should().BeTrue();
        tracker.LastObjectIdFilter.Should().NotBeNull();
        tracker.LastObjectIdFilter.Should().BeEquivalentTo(new long[] { 42, 7 });

        // The server-side change to objectid 42 since the base generation is still detected through
        // the filtered probe (last-write-wins: the conflicting edit remains applied).
        report.Conflicts.Should().HaveCount(1);
        report.Conflicts[0].ObjectId.Should().Be(42);
        report.Conflicts[0].Applied.Should().BeTrue();
    }

    [UnitTest]
    public async Task ApplyUpload_CreateOnlyEdits_DoesNotQueryChangeTracker()
    {
        var tracker = new RecordingChangeTracker();
        var service = CreateService(tracker);
        var edits = ImmutableArray.Create(
            new ReplicaUploadEdit(FeatureEditOperationKind.Create, ObjectId: null, Payload: null));
        var request = CreateRequest(edits);

        var report = await service.ApplyUploadAsync(request, new CountingEditApplier());

        report.Success.Should().BeTrue();
        tracker.FilteredCalls.Should().Be(0, "creates use server-assigned ids and cannot conflict");
    }

    [UnitTest]
    public async Task ApplyUpload_RollbackOnFailureRequested_FlowsThroughToEditApplier()
    {
        // The protocol adapter's rollbackOnFailure=true must reach the edit applier so the shared edit
        // pipeline applies each layer's batch atomically (#2136).
        var tracker = new RecordingChangeTracker();
        var service = CreateService(tracker);
        var applier = new CountingEditApplier();
        var edits = ImmutableArray.Create(
            new ReplicaUploadEdit(FeatureEditOperationKind.Create, ObjectId: null, Payload: null));
        var request = CreateRequest(edits, rollbackOnFailure: true);

        var report = await service.ApplyUploadAsync(request, applier);

        report.Success.Should().BeTrue();
        applier.LastRollbackOnFailure.Should().BeTrue();
    }

    [UnitTest]
    public async Task ApplyUpload_RollbackOnFailureDefault_DoesNotRequestAtomicApply()
    {
        var tracker = new RecordingChangeTracker();
        var service = CreateService(tracker);
        var applier = new CountingEditApplier();
        var edits = ImmutableArray.Create(
            new ReplicaUploadEdit(FeatureEditOperationKind.Create, ObjectId: null, Payload: null));
        var request = CreateRequest(edits);

        await service.ApplyUploadAsync(request, applier);

        applier.LastRollbackOnFailure.Should().BeFalse("rollbackOnFailure defaults to false (best-effort per-row)");
    }

    private static ReplicaSyncService CreateService(RecordingChangeTracker tracker)
        => new(tracker, new NoReviewConflictRepository(), NullLogger<ReplicaSyncService>.Instance);

    private static ReplicaSyncRequest CreateRequest(
        ImmutableArray<ReplicaUploadEdit> edits,
        bool rollbackOnFailure = false) => new(
        ReplicaId: "replica-1",
        ServiceId: "svc-1",
        Direction: ReplicaSyncDirection.Upload,
        BaseGeneration: 10,
        LayerEdits: ImmutableArray.Create(new ReplicaUploadLayerEdits(PublicLayerId: 0, StorageLayerId: 10, edits)),
        RollbackOnFailure: rollbackOnFailure);

    private sealed class RecordingChangeTracker : IChangeTracker
    {
        private readonly FeatureChange[] _changes;

        public RecordingChangeTracker(params FeatureChange[] changes) => _changes = changes;

        public int FilteredCalls { get; private set; }

        public IReadOnlySet<long>? LastObjectIdFilter { get; private set; }

        public Task<long> GetCurrentGenerationAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(99L);

        public Task<IReadOnlyList<FeatureChange>> GetChangesSinceAsync(
            long sinceGeneration,
            int[] layerIds,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FeatureChange>>(_changes);

        public Task<IReadOnlyList<FeatureChange>> GetChangesSinceAsync(
            long sinceGeneration,
            int[] layerIds,
            IReadOnlySet<long>? objectIds,
            CancellationToken cancellationToken = default)
        {
            FilteredCalls++;
            LastObjectIdFilter = objectIds;
            var filtered = objectIds is null
                ? _changes
                : _changes.Where(change => objectIds.Contains(change.ObjectId)).ToArray();
            return Task.FromResult<IReadOnlyList<FeatureChange>>(filtered);
        }
    }

    private sealed class CountingEditApplier : IReplicaEditApplier
    {
        public bool? LastRollbackOnFailure { get; private set; }

        public Task<ReplicaLayerApplyResult> ApplyAsync(
            string serviceId,
            int publicLayerId,
            ImmutableArray<ReplicaUploadEdit> edits,
            bool rollbackOnFailure,
            CancellationToken cancellationToken = default)
        {
            LastRollbackOnFailure = rollbackOnFailure;
            var result = new ReplicaLayerApplyResult(
                publicLayerId,
                AppliedAdds: edits.Count(edit => edit.Kind == FeatureEditOperationKind.Create),
                AppliedUpdates: edits.Count(edit => edit.Kind == FeatureEditOperationKind.Update),
                AppliedDeletes: edits.Count(edit => edit.Kind == FeatureEditOperationKind.Delete),
                Failed: false,
                FailureMessage: null);
            return Task.FromResult(result);
        }
    }

    /// <summary>
    /// Conflict repository without durable review support: the sync service then falls back to
    /// last-write-wins and never writes conflict records, keeping the test focused on the probe.
    /// </summary>
    private sealed class NoReviewConflictRepository : IReplicaConflictRepository
    {
        public bool SupportsConflictReview => false;

        public Task UpsertAsync(ReplicaConflictRecord record, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<ReplicaConflictRecord>> ListByReplicaAsync(
            string replicaId,
            ReplicaConflictStatus? status = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ReplicaConflictRecord>>([]);

        public Task<ReplicaConflictRecord?> GetAsync(string conflictId, CancellationToken cancellationToken = default)
            => Task.FromResult<ReplicaConflictRecord?>(null);

        public Task<ReplicaConflictResolutionOutcome> ResolveAsync(
            ReplicaConflictResolution resolution,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ReplicaConflictResolutionOutcome(null, false));
    }
}

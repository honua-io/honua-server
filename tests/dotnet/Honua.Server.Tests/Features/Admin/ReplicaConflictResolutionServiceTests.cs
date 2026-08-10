// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Server.Features.Admin.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Regression coverage for the conflict-resolution claim lifecycle (#2430). Every case here is a way
/// a conflict could end up recorded as resolved without the corresponding feature write having
/// committed — the exact dishonesty the write-through resolution path exists to remove.
/// </summary>
public sealed class ReplicaConflictResolutionServiceTests
{
    [UnitTest]
    public async Task ResolveAsync_WhenWriteFailsOnACancelledRequest_ReleasesTheClaim()
    {
        // The applier can return Applied=false precisely because the request was cancelled. Releasing
        // on the request's own token would throw out of the cleanup and strand the conflict claimed
        // with nothing written.
        var repository = new FakeConflictRepository(Conflict(clientEditApplied: true));
        var service = CreateService(repository, new FailingApplier());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer), cancelled.Token);

        result.Status.Should().Be(ReplicaConflictResolutionStatus.WriteFailed);
        repository.Current.Status.Should().Be(
            ReplicaConflictStatus.Pending,
            "a resolution whose write never committed must leave the conflict reviewable");
        repository.Current.ResolutionAction.Should().BeNull();
        repository.Current.ResolvedBy.Should().BeNull();
        repository.Current.ResolvedServerGeneration.Should().BeNull();
    }

    [UnitTest]
    public async Task ResolveAsync_WhenApplierThrowsOnACancelledRequest_ReleasesTheClaim()
    {
        var repository = new FakeConflictRepository(Conflict(clientEditApplied: true));
        var service = CreateService(repository, new ThrowingApplier());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = async () => await service.ResolveAsync(
            Request(ReplicaConflictResolutionAction.KeepServer), cancelled.Token);

        await act.Should().ThrowAsync<InvalidOperationException>();
        repository.Current.Status.Should().Be(ReplicaConflictStatus.Pending);
        repository.Current.ResolutionAction.Should().BeNull();
    }

    [UnitTest]
    public async Task ResolveAsync_ClaimsBeforeWriting_SoAConcurrentLoserNeverWrites()
    {
        // The guarded status transition is the single-winner primitive: a caller that loses it must
        // not reach the applier at all, or the loser's write could land after the winner's.
        var repository = new FakeConflictRepository(Conflict(clientEditApplied: true)) { ClaimSucceeds = false };
        var applier = new RecordingApplier();
        var service = CreateService(repository, applier);

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.AlreadyResolved);
        applier.Calls.Should().Be(0, "the losing caller must not write feature state");
    }

    [UnitTest]
    public async Task ResolveAsync_WhenWriteSucceeds_RecordsTheGenerationProducedAfterTheWrite()
    {
        var repository = new FakeConflictRepository(Conflict(clientEditApplied: true));
        var applier = new RecordingApplier();
        var service = CreateService(repository, applier);

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.Applied);
        result.CommittedNewServerState.Should().BeTrue();
        result.Effect.Should().Be(ReplicaConflictResolutionEffect.WriteFeatureState);
        applier.Calls.Should().Be(1);
        repository.Current.Status.Should().Be(ReplicaConflictStatus.Resolved);
        repository.Current.ResolvedServerGeneration.Should().Be(FakeChangeTracker.Generation);
    }

    [UnitTest]
    public async Task ResolveAsync_WhenFeatureWasEditedAfterTheConflict_ReturnsStaleAndDoesNotWrite()
    {
        // A resolution reviewed long after detection must not apply the conflict-time state over a
        // legitimate newer edit. The change tracker reports a change to the conflicting feature after
        // the generation the captured states describe, so the write is refused.
        var repository = new FakeConflictRepository(Conflict(clientEditApplied: true));
        var tracker = new FakeChangeTracker();
        tracker.ChangesSinceBase.Add(new FeatureChange
        {
            ChangeId = 1,
            Generation = 55,
            LayerId = 10,
            ObjectId = 42,
            Operation = FeatureChangeOperation.Update,
            ChangedAt = DateTimeOffset.UtcNow,
        });
        var applier = new RecordingApplier();
        var service = CreateService(repository, applier, tracker);

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.Stale);
        applier.Calls.Should().Be(0, "a stale resolution must never overwrite the newer edit");
        repository.Current.Status.Should().Be(
            ReplicaConflictStatus.Pending, "the conflict stays reviewable against the current state");
        repository.Current.ResolutionAction.Should().BeNull();
    }

    [UnitTest]
    public async Task ResolveAsync_WhenAnUnrelatedFeatureChanged_StillResolves()
    {
        // The probe is scoped to the conflicting feature: unrelated churn on the layer must not block
        // every resolution.
        var repository = new FakeConflictRepository(Conflict(clientEditApplied: true));
        var tracker = new FakeChangeTracker();
        tracker.ChangesSinceBase.Add(new FeatureChange
        {
            ChangeId = 1,
            Generation = 55,
            LayerId = 10,
            ObjectId = 4242,
            Operation = FeatureChangeOperation.Update,
            ChangedAt = DateTimeOffset.UtcNow,
        });
        var applier = new RecordingApplier();
        var service = CreateService(repository, applier, tracker);

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.Applied);
        applier.Calls.Should().Be(1);
    }

    [UnitTest]
    public async Task ResolveAsync_WhenConflictPredatesThePrecondition_SkipsTheStalenessCheck()
    {
        // Conflicts recorded before the precondition existed carry no storage layer / base generation.
        // They must stay resolvable rather than being permanently blocked.
        var legacy = Conflict(clientEditApplied: true) with
        {
            StorageLayerId = null,
            ResolutionBaseGeneration = null,
        };
        var repository = new FakeConflictRepository(legacy);
        var applier = new RecordingApplier();
        var service = CreateService(repository, applier);

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.Applied);
        applier.Calls.Should().Be(1);
    }

    [UnitTest]
    public async Task ResolveAsync_WhenFinalizationFailsAfterTheWrite_ARetryResumesAndCompletesIt()
    {
        // The write commits, then finalization throws. The claim is terminal, so a naive retry would
        // answer AlreadyResolved and the produced generation would be lost forever. The retry must
        // instead resume finalization — without re-applying the feature write.
        var repository = new FakeConflictRepository(Conflict(clientEditApplied: true));
        var applier = new RecordingApplier();
        var service = CreateService(repository, applier);

        repository.FinalizationThrows = true;
        var first = async () => await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));
        await first.Should().ThrowAsync<InvalidOperationException>();

        repository.Current.Status.Should().Be(ReplicaConflictStatus.Resolved);
        repository.Current.FinalizationPending.Should().BeTrue("the resolution is incomplete and resumable");
        applier.Calls.Should().Be(1);

        // Retry: same operator, same action.
        repository.FinalizationThrows = false;
        var resumed = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        resumed.Status.Should().Be(ReplicaConflictResolutionStatus.Applied);
        applier.Calls.Should().Be(1, "a resumed finalization must not re-apply the committed write");
        repository.Current.FinalizationPending.Should().BeFalse();
        repository.Current.ResolvedServerGeneration.Should().Be(FakeChangeTracker.Generation);
    }

    [UnitTest]
    public async Task ResolveAsync_WhenResolutionIsComplete_StillReportsAlreadyResolved()
    {
        // Resume must not turn a genuinely finished resolution into a repeatable one.
        var repository = new FakeConflictRepository(Conflict(clientEditApplied: true));
        var service = CreateService(repository, new RecordingApplier());

        var first = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));
        first.Status.Should().Be(ReplicaConflictResolutionStatus.Applied);

        var second = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));
        second.Status.Should().Be(ReplicaConflictResolutionStatus.AlreadyResolved);
    }

    [UnitTest]
    public async Task ResolveAsync_WhenClaimedByAnotherOperator_IsNotResumedByThisRequest()
    {
        // An unfinalized claim belonging to someone else is still already-resolved from here: resume
        // is only for retries of the same resolution.
        var repository = new FakeConflictRepository(Conflict(clientEditApplied: true) with
        {
            Status = ReplicaConflictStatus.Resolved,
            ResolutionAction = ReplicaConflictResolutionAction.KeepServer,
            ResolvedBy = "someone-else",
            FinalizationPending = true,
            WriteCommitted = true,
        })
        { ClaimSucceeds = false };
        var applier = new RecordingApplier();
        var service = CreateService(repository, applier);

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.AlreadyResolved);
        applier.Calls.Should().Be(0);
    }

    private static ReplicaConflictResolutionService CreateService(
        FakeConflictRepository repository,
        IReplicaConflictResolutionApplier applier,
        FakeChangeTracker? changeTracker = null)
        => new(
            repository,
            changeTracker ?? new FakeChangeTracker(),
            new NoOpAuditLog(),
            NullLogger<ReplicaConflictResolutionService>.Instance,
            applier);

    private static ReplicaConflictResolutionServiceRequest Request(ReplicaConflictResolutionAction action)
        => new(
            ReplicaId: "replica-1",
            ConflictId: "conflict-1",
            Action: action,
            ActionName: "keepServer",
            Inputs: new ReplicaConflictResolutionInputs(FieldValues: null, GeometrySource: null),
            Actor: "operator-1",
            CorrelationId: "trace-1");

    private static ReplicaConflictRecord Conflict(bool clientEditApplied) => new()
    {
        ConflictId = "conflict-1",
        ReplicaId = "replica-1",
        ServiceId = "svc-1",
        LayerId = 0,
        ObjectId = 42,
        ConflictType = ReplicaConflictType.Attribute,
        Status = ReplicaConflictStatus.Pending,
        ServerGeneration = 5,
        StorageLayerId = 10,
        ResolutionBaseGeneration = 40,
        ClientEditApplied = clientEditApplied,
        ClientStateJson = """{"attributes":{"objectid":42,"name":"client"}}""",
        ServerStateJson = """{"attributes":{"objectid":42,"name":"server"}}""",
        DetectedAt = DateTimeOffset.UtcNow,
    };

    private sealed class FakeChangeTracker : IChangeTracker
    {
        public const long Generation = 77L;

        /// <summary>Changes the tracker reports for the staleness probe.</summary>
        public List<FeatureChange> ChangesSinceBase { get; } = [];

        public Task<long> GetCurrentGenerationAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Generation);

        public Task<IReadOnlyList<FeatureChange>> GetChangesSinceAsync(
            long sinceGeneration,
            int[] layerIds,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FeatureChange>>([]);

        public Task<IReadOnlyList<FeatureChange>> GetChangesSinceAsync(
            long sinceGeneration,
            int[] layerIds,
            IReadOnlySet<long>? objectIds,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FeatureChange>>(
                objectIds is null
                    ? ChangesSinceBase
                    : ChangesSinceBase.Where(c => objectIds.Contains(c.ObjectId)).ToList());
    }

    private sealed class NoOpAuditLog : IAuditLog
    {
        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingApplier : IReplicaConflictResolutionApplier
    {
        public int Calls { get; private set; }

        public Task<ReplicaConflictApplyResult> ApplyAsync(
            ReplicaConflictResolutionCommand command,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ReplicaConflictApplyResult(Applied: true, FailureMessage: null));
        }
    }

    private sealed class FailingApplier : IReplicaConflictResolutionApplier
    {
        public Task<ReplicaConflictApplyResult> ApplyAsync(
            ReplicaConflictResolutionCommand command,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ReplicaConflictApplyResult(Applied: false, FailureMessage: "write rejected"));
    }

    private sealed class ThrowingApplier : IReplicaConflictResolutionApplier
    {
        public Task<ReplicaConflictApplyResult> ApplyAsync(
            ReplicaConflictResolutionCommand command,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("provider fault");
    }

    /// <summary>
    /// Conflict repository whose writes honor the cancellation token, so a test can prove the cleanup
    /// path does not depend on the (possibly cancelled) request token.
    /// </summary>
    private sealed class FakeConflictRepository(ReplicaConflictRecord seed) : IReplicaConflictRepository
    {
        public bool SupportsConflictReview => true;

        public bool ClaimSucceeds { get; init; } = true;

        public bool FinalizationThrows { get; set; }

        public List<ReplicaConflictFinalizationUpdate> FinalizationUpdates { get; } = [];

        public ReplicaConflictRecord Current { get; private set; } = seed;

        public Task UpsertAsync(ReplicaConflictRecord record, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Current = record;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ReplicaConflictRecord>> ListByReplicaAsync(
            string replicaId,
            ReplicaConflictStatus? status = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ReplicaConflictRecord>>([Current]);

        public Task<ReplicaConflictRecord?> GetAsync(string conflictId, CancellationToken cancellationToken = default)
            => Task.FromResult<ReplicaConflictRecord?>(
                string.Equals(conflictId, Current.ConflictId, StringComparison.Ordinal) ? Current : null);

        public Task<bool> TryUpdateDetectionStateAsync(
            ReplicaConflictDetectionUpdate update,
            CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<bool> TryUpdateFinalizationStateAsync(
            ReplicaConflictFinalizationUpdate update,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FinalizationUpdates.Add(update);

            // Fail only the finalize step, so the write-committed marker still lands: that is the
            // state a post-write finalization failure actually leaves behind.
            if (FinalizationThrows && update.Finalized == true)
            {
                throw new InvalidOperationException("finalization store unavailable");
            }

            Current = Current with
            {
                WriteCommitted = update.WriteCommitted ?? Current.WriteCommitted,
                ResolvedServerGeneration = update.ResolvedServerGeneration ?? Current.ResolvedServerGeneration,
                FinalizationPending = update.Finalized is { } f ? !f : Current.FinalizationPending,
            };
            return Task.FromResult(true);
        }

        public Task<ReplicaConflictResolutionOutcome> ResolveAsync(
            ReplicaConflictResolution resolution,
            CancellationToken cancellationToken = default)
        {
            if (!ClaimSucceeds)
            {
                return Task.FromResult(new ReplicaConflictResolutionOutcome(Current, Applied: false));
            }

            Current = Current with
            {
                Status = resolution.Action == ReplicaConflictResolutionAction.Defer
                    ? ReplicaConflictStatus.Deferred
                    : ReplicaConflictStatus.Resolved,
                ResolutionAction = resolution.Action,
                ResolvedBy = resolution.ResolvedBy,
                ResolvedAt = resolution.ResolvedAt,
                ResolvedServerGeneration = resolution.ResolvedServerGeneration,
                WriteCommitted = false,
                FinalizationPending = true,
            };
            return Task.FromResult(new ReplicaConflictResolutionOutcome(Current, Applied: true));
        }
    }
}

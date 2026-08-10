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
            ResolvedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
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

    [UnitTest]
    public async Task ResolveAsync_WhenAuditFails_LeavesTheResolutionResumableRatherThanComplete()
    {
        // Marking the resolution finalized before the audit is durable would make an audit-sink outage
        // permanent: the retry would see a complete resolution and the evidence would never be written.
        var repository = new FakeConflictRepository(Conflict(clientEditApplied: true));
        var audit = new NoOpAuditLog { Throws = true };
        var service = CreateService(repository, new RecordingApplier(), auditLog: audit);

        var act = async () => await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));
        await act.Should().ThrowAsync<InvalidOperationException>();

        repository.Current.FinalizationPending.Should().BeTrue(
            "the resolution is incomplete until its audit evidence is durable");
        repository.Current.ResolvedServerGeneration.Should().Be(
            FakeChangeTracker.Generation, "the produced generation is still persisted");

        // Retry once the sink recovers: finalization resumes and the evidence lands.
        audit.Throws = false;
        var resumed = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        resumed.Status.Should().Be(ReplicaConflictResolutionStatus.Applied);
        audit.Records.Should().Be(1);
        repository.Current.FinalizationPending.Should().BeFalse();
    }

    [UnitTest]
    public async Task ResolveAsync_WhenAbandonedClaimHasACommittedWrite_ResumesInsteadOfReapplying()
    {
        // An abandoned claim whose write is durably marked committed resumes finalization; the
        // committed edit is never dispatched a second time.
        var claimed = Conflict(clientEditApplied: true) with
        {
            Status = ReplicaConflictStatus.Resolved,
            ResolutionAction = ReplicaConflictResolutionAction.KeepServer,
            ResolvedBy = "operator-1",
            FinalizationPending = true,
            WriteCommitted = true,
            ResolvedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        };
        var repository = new FakeConflictRepository(claimed) { ClaimSucceeds = false };
        var applier = new RecordingApplier();
        var service = CreateService(repository, applier);

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.Applied);
        applier.Calls.Should().Be(0, "the committed write must not be applied twice");
        repository.Current.FinalizationPending.Should().BeFalse();
    }

    [UnitTest]
    public async Task ResolveAsync_WhenAnotherAttemptIsStillWithinTheClaimLease_DoesNotTearItDown()
    {
        // Two requests from the same operator with the same action: the second must not release or
        // resume the first's live claim, or the first's edit could land while a third request writes
        // concurrently.
        var live = Conflict(clientEditApplied: true) with
        {
            Status = ReplicaConflictStatus.Resolved,
            ResolutionAction = ReplicaConflictResolutionAction.KeepServer,
            ResolvedBy = "operator-1",
            ResolvedAt = DateTimeOffset.UtcNow,
            FinalizationPending = true,
            WriteCommitted = false,
        };
        var repository = new FakeConflictRepository(live) { ClaimSucceeds = false };
        var applier = new RecordingApplier();
        var service = CreateService(repository, applier);

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.AlreadyResolved);
        applier.Calls.Should().Be(0);
        repository.Current.Status.Should().Be(
            ReplicaConflictStatus.Resolved, "the live claim must survive a concurrent duplicate request");
    }

    [UnitTest]
    public async Task ResolveAsync_WhenWriteMarkerIsFalseAndNothingLanded_ReleasesTheClaim()
    {
        var claimed = Conflict(clientEditApplied: true) with
        {
            Status = ReplicaConflictStatus.Resolved,
            ResolutionAction = ReplicaConflictResolutionAction.KeepServer,
            ResolvedBy = "operator-1",
            FinalizationPending = true,
            WriteCommitted = false,
            ResolvedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        };
        var repository = new FakeConflictRepository(claimed) { ClaimSucceeds = false };
        var applier = new RecordingApplier();
        var service = CreateService(repository, applier);

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.WriteFailed);
        applier.Calls.Should().Be(0);
        repository.Current.Status.Should().Be(ReplicaConflictStatus.Pending);
    }

    [UnitTest]
    public async Task ResolveAsync_WhileDetectionIsStillRecording_RefusesRatherThanPlanningOnAProvisionalSnapshot()
    {
        // A freshly listed last-write-wins conflict whose upload is still applying has not had
        // ClientEditApplied decided yet. Resolving now would record keepServer as a no-op and then the
        // client edit would commit, leaving the feature holding the client state while the resolution
        // claims the server state was kept.
        var inFlight = Conflict(clientEditApplied: false) with
        {
            ResolutionBaseGeneration = null,
            DetectedAt = DateTimeOffset.UtcNow,
        };
        var repository = new FakeConflictRepository(inFlight);
        var applier = new RecordingApplier();
        var service = CreateService(repository, applier);

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.DetectionInFlight);
        applier.Calls.Should().Be(0);
        repository.Current.Status.Should().Be(ReplicaConflictStatus.Pending, "the conflict stays reviewable");
    }

    [UnitTest]
    public async Task ResolveAsync_ForALegacyConflictWithoutABaseGeneration_StillResolves()
    {
        // Conflicts recorded before the base generation existed also lack it; the age test is what
        // stops them being permanently unresolvable.
        var legacy = Conflict(clientEditApplied: true) with
        {
            StorageLayerId = null,
            ResolutionBaseGeneration = null,
            DetectedAt = DateTimeOffset.UtcNow.AddDays(-3),
        };
        var repository = new FakeConflictRepository(legacy);
        var service = CreateService(repository, new RecordingApplier());

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.Applied);
    }

    [UnitTest]
    public async Task ReleaseClaim_WhenTheClaimHasAlreadyBeenReplaced_DoesNotClearTheReplacement()
    {
        // Two retries can both judge an expired claim abandoned. Once the first releases it and a third
        // request re-claims, the second release must be a no-op or it would strip the new owner.
        var expired = Conflict(clientEditApplied: true) with
        {
            Status = ReplicaConflictStatus.Resolved,
            ResolutionAction = ReplicaConflictResolutionAction.KeepServer,
            ResolvedBy = "operator-1",
            ResolvedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            FinalizationPending = true,
            WriteCommitted = false,
        };
        var repository = new FakeConflictRepository(expired) { ClaimSucceeds = false };
        var service = CreateService(repository, new RecordingApplier());

        // A replacement claim is taken before the stale release runs.
        var replacementClaimedAt = DateTimeOffset.UtcNow;
        repository.Replace(expired with { ResolvedBy = "operator-2", ResolvedAt = replacementClaimedAt });

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.AlreadyResolved);
        repository.Current.ResolvedBy.Should().Be("operator-2", "the replacement claim must survive");
        repository.Current.Status.Should().Be(ReplicaConflictStatus.Resolved);
    }

    [UnitTest]
    public async Task ResolveAsync_WhenAServerEditLandedWhileTheConflictWasRecorded_RefusesToRestoreTheSnapshot()
    {
        // The pre-apply server snapshot is taken before conflict detection, so an edit landing in that
        // window is detected as the conflict yet missing from ServerStateJson. Restoring the snapshot
        // would discard that edit, so the restoration is refused.
        var repository = new FakeConflictRepository(Conflict(clientEditApplied: true));
        var tracker = new FakeChangeTracker();
        // Two changes to the feature between the replica base (5) and the conflict generation (40):
        // the sync batch contributes one, so the second is a foreign server edit.
        tracker.ChangesSinceBase.Add(Change(generation: 20));
        tracker.ChangesSinceBase.Add(Change(generation: 35));
        var applier = new RecordingApplier();
        var service = CreateService(repository, applier, tracker);

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.Stale);
        applier.Calls.Should().Be(0, "the captured server state may not contain the interleaved edit");
        repository.Current.Status.Should().Be(ReplicaConflictStatus.Pending);
    }

    [UnitTest]
    public async Task ResolveAsync_WhenOnlyTheSyncBatchTouchedTheFeature_RestoresNormally()
    {
        var repository = new FakeConflictRepository(Conflict(clientEditApplied: true));
        var tracker = new FakeChangeTracker();
        tracker.ChangesSinceBase.Add(Change(generation: 20));
        var applier = new RecordingApplier();
        var service = CreateService(repository, applier, tracker);

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.Applied);
        applier.Calls.Should().Be(1);
    }

    [UnitTest]
    public async Task ResolveAsync_WhenEnvelopesAreNotYetAttached_TreatsDetectionAsStillInFlight()
    {
        // The base generation is stamped before the adapter attaches the client/server envelopes, so it
        // is not on its own a completion signal: without the states a resolution reads, the record is
        // still in flight.
        var partial = Conflict(clientEditApplied: false) with
        {
            ClientStateJson = null,
            ServerStateJson = null,
            DetectedAt = DateTimeOffset.UtcNow,
        };
        var repository = new FakeConflictRepository(partial);
        var applier = new RecordingApplier();
        var service = CreateService(repository, applier);

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.DetectionInFlight);
        applier.Calls.Should().Be(0);
    }

    [UnitTest]
    public async Task ResolveAsync_WhenADeferredClaimNeverFinalized_ResumesItRatherThanSupersedingIt()
    {
        // A defer whose audit failed is still owed its evidence; letting a later action supersede it
        // would lose that permanently.
        var deferred = Conflict(clientEditApplied: true) with
        {
            Status = ReplicaConflictStatus.Deferred,
            ResolutionAction = ReplicaConflictResolutionAction.Defer,
            ResolvedBy = "operator-1",
            ResolvedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            FinalizationPending = true,
        };
        var repository = new FakeConflictRepository(deferred);
        var audit = new NoOpAuditLog();
        var service = CreateService(repository, new RecordingApplier(), auditLog: audit);

        var result = await service.ResolveAsync(
            Request(ReplicaConflictResolutionAction.Defer) with { ActionName = "defer" });

        result.Status.Should().Be(ReplicaConflictResolutionStatus.Applied);
        audit.Records.Should().Be(1, "the deferral's audit evidence is finally written");
        repository.Current.FinalizationPending.Should().BeFalse();
    }

    private static FeatureChange Change(long generation) => new()
    {
        ChangeId = generation,
        Generation = generation,
        LayerId = 10,
        ObjectId = 42,
        Operation = FeatureChangeOperation.Update,
        ChangedAt = DateTimeOffset.UtcNow,
    };

    private static ReplicaConflictResolutionService CreateService(
        FakeConflictRepository repository,
        IReplicaConflictResolutionApplier applier,
        FakeChangeTracker? changeTracker = null,
        NoOpAuditLog? auditLog = null)
        => new(
            repository,
            changeTracker ?? new FakeChangeTracker(),
            auditLog ?? new NoOpAuditLog(),
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
        DetectedAt = DateTimeOffset.UtcNow.AddHours(-1),
        ClientStateJson = """{"attributes":{"objectid":42,"name":"client"}}""",
        ServerStateJson = """{"attributes":{"objectid":42,"name":"server"}}""",
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
            // Honour sinceGeneration as the real tracker does: the resolution preconditions probe
            // different windows and would otherwise see each other's changes.
            => Task.FromResult<IReadOnlyList<FeatureChange>>(
                ChangesSinceBase
                    .Where(c => c.Generation > sinceGeneration)
                    .Where(c => objectIds is null || objectIds.Contains(c.ObjectId))
                    .ToList());
    }

    private sealed class NoOpAuditLog : IAuditLog
    {
        public bool Throws { get; set; }

        public int Records { get; private set; }

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            if (Throws)
            {
                throw new InvalidOperationException("audit sink unavailable");
            }

            Records++;
            return Task.CompletedTask;
        }
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

        /// <summary>Swaps the stored record, simulating another request winning the row.</summary>
        public void Replace(ReplicaConflictRecord record) => Current = record;

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

        public Task<bool> TryReleaseClaimAsync(
            string conflictId,
            string resolvedBy,
            ReplicaConflictResolutionAction action,
            DateTimeOffset resolvedAt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Mirror the real guard: a release only applies to the exact claim it names.
            if (!string.Equals(resolvedBy, Current.ResolvedBy, StringComparison.Ordinal) ||
                action != Current.ResolutionAction ||
                resolvedAt != Current.ResolvedAt)
            {
                return Task.FromResult(false);
            }

            Current = Current with
            {
                Status = ReplicaConflictStatus.Pending,
                ResolutionAction = null,
                ResolvedBy = null,
                ResolvedAt = null,
                ResolvedServerGeneration = null,
                WriteCommitted = false,
                FinalizationPending = false,
            };
            return Task.FromResult(true);
        }

        public Task<bool> TryUpdateFinalizationStateAsync(
            ReplicaConflictFinalizationUpdate update,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FinalizationUpdates.Add(update);

            // Mirror the real guard: progress only applies to the claim that produced it.
            if (!string.Equals(update.ResolvedBy, Current.ResolvedBy, StringComparison.Ordinal) ||
                update.Action != Current.ResolutionAction)
            {
                return Task.FromResult(false);
            }

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

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
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
    public async Task ResolveAsync_UnknownUploadOutcomeWithAPostBaseChange_RebaselinesForReview()
    {
        // The post-base change may be the upload whose commit acknowledgement was lost. Returning
        // Stale without moving the base makes every retry rediscover the same change forever.
        var conflict = Conflict(clientEditApplied: false) with { ClientEditOutcomeUnknown = true };
        var repository = new FakeConflictRepository(conflict);
        var tracker = new FakeChangeTracker();
        tracker.ChangesSinceBase.Add(Change(generation: 55));
        var applier = new RecordingApplier();
        var service = CreateService(repository, applier, tracker);

        var first = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));
        var reviewed = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        first.Status.Should().Be(ReplicaConflictResolutionStatus.Stale,
            "the operator must review the state that survived the ambiguous upload");
        repository.DetectionUpdates.Should().ContainSingle()
            .Which.ResolutionBaseGeneration.Should().Be(FakeChangeTracker.Generation);
        repository.Current.ClientEditOutcomeUnknown.Should().BeFalse();
        reviewed.Status.Should().Be(ReplicaConflictResolutionStatus.Applied,
            "the same originating change must not strand every later resolution as stale");
        applier.Calls.Should().Be(0,
            "keepServer becomes a no-op after the record is re-pointed at the current server state");
    }

    [UnitTest]
    public async Task ResolveAsync_CustomPublicObjectId_ProbesStoredChangeLogIdentity()
    {
        const long publicObjectId = 7001;
        const long storageObjectId = 19;
        var repository = new FakeConflictRepository(Conflict(clientEditApplied: true) with
        {
            ObjectId = publicObjectId,
            StorageObjectId = storageObjectId,
        });
        var tracker = new FakeChangeTracker();
        tracker.ChangesSinceBase.Add(new FeatureChange
        {
            ChangeId = 1,
            Generation = 55,
            LayerId = 10,
            ObjectId = storageObjectId,
            Operation = FeatureChangeOperation.Update,
            ChangedAt = DateTimeOffset.UtcNow,
        });
        var applier = new RecordingApplier();
        var service = CreateService(repository, applier, tracker);

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.Stale);
        applier.Calls.Should().Be(0);
        tracker.ObjectIdFilters.Should().ContainSingle()
            .Which.Should().BeEquivalentTo([storageObjectId],
                "the change log stores Feature.Id, not the custom public id.primary value");
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
            PreWriteStateToken = "token-at-claim",
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
    public async Task ResolveAsync_WhenAbandonedClaimHasNoWriteMarker_ReappliesTheIdempotentWrite()
    {
        // Whether the previous attempt's write landed cannot be told from durable state, and both
        // guesses are wrong: releasing strands the conflict behind its own staleness probe, assuming it
        // landed can finalize a state that never existed. The write is idempotent, so it is re-run.
        var claimed = Conflict(clientEditApplied: true) with
        {
            Status = ReplicaConflictStatus.Resolved,
            ResolutionAction = ReplicaConflictResolutionAction.KeepServer,
            ResolvedBy = "operator-1",
            ResolvedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            FinalizationPending = true,
            WriteCommitted = false,
            PreWriteStateToken = "token-at-claim",
        };
        var repository = new FakeConflictRepository(claimed);
        var applier = new RecordingApplier();
        var service = CreateService(repository, applier);

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.Applied);
        applier.Calls.Should().Be(1, "the idempotent write is re-run exactly once");
        repository.Current.FinalizationPending.Should().BeFalse();
        repository.Current.WriteCommitted.Should().BeTrue();
    }

    [UnitTest]
    public async Task ResolveAsync_WhenTheReappliedWriteFails_ReleasesTheClaim()
    {
        var claimed = Conflict(clientEditApplied: true) with
        {
            Status = ReplicaConflictStatus.Resolved,
            ResolutionAction = ReplicaConflictResolutionAction.KeepServer,
            ResolvedBy = "operator-1",
            FinalizationPending = true,
            WriteCommitted = false,
            ResolvedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            PreWriteStateToken = "token-at-claim",
        };
        var repository = new FakeConflictRepository(claimed);
        var service = CreateService(repository, new FailingApplier());

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.WriteFailed);
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
            PreWriteStateToken = "token-at-claim",
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
    public async Task ResolveAsync_WhenTheOnlyChangeIsWithinTheConflictWindow_RestoresNormally()
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
    public async Task ResolveAsync_WhenACurrentRecordNeverFinishedDetection_StaysBlockedPastTheSettleWindow()
    {
        // A sync cut short between the insert and the enrichment leaves a current record with no base
        // generation. Ageing it out would skip the staleness precondition entirely - there is nothing
        // to compare against - and let the resolution overwrite edits made after the aborted sync.
        var incomplete = Conflict(clientEditApplied: false) with
        {
            ResolutionBaseGeneration = null,
            ServerStateJson = null,
            DetectedAt = DateTimeOffset.UtcNow.AddHours(-3),
        };
        var repository = new FakeConflictRepository(incomplete);
        var applier = new RecordingApplier();
        var service = CreateService(repository, applier);

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.AcceptClient));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.DetectionInFlight);
        result.Message.Should().Contain("did not finish recording");
        applier.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(ReplicaConflictType.DeleteUpdate)]
    [InlineData(ReplicaConflictType.UpdateDelete)]
    public async Task ResolveAsync_StructuralDeleteConflictMissingItsAbsentSide_IsStillResolvable(
        ReplicaConflictType conflictType)
    {
        // A client delete carries no client feature state, and a feature the server already deleted has
        // no server state to capture. Demanding both envelopes universally left every delete conflict
        // blocked forever.
        var structural = Conflict(clientEditApplied: false) with
        {
            ConflictType = conflictType,
            ClientStateJson = conflictType == ReplicaConflictType.DeleteUpdate ? null : """{"attributes":{"objectid":42}}""",
            ServerStateJson = conflictType == ReplicaConflictType.UpdateDelete ? null : """{"attributes":{"objectid":42}}""",
            DetectedAt = DateTimeOffset.UtcNow,
        };
        var repository = new FakeConflictRepository(structural);
        var service = CreateService(repository, new RecordingApplier());

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().NotBe(ReplicaConflictResolutionStatus.DetectionInFlight);
    }

    [UnitTest]
    public async Task ResolveAsync_LegacyRecordWithoutDetectionState_StaysResolvable()
    {
        // A record written before detection persisted any of this state will never gain it. Blocking
        // those forever would make every pre-existing conflict permanently unresolvable.
        var legacy = Conflict(clientEditApplied: true) with
        {
            StorageLayerId = null,
            ResolutionBaseGeneration = null,
            DetectedAt = DateTimeOffset.UtcNow.AddHours(-3),
        };
        var repository = new FakeConflictRepository(legacy);
        var service = CreateService(repository, new RecordingApplier());

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.Applied);
    }

    [UnitTest]
    public async Task ResolveAsync_WhenOnlyTheClientEnvelopeIsAttached_TreatsDetectionAsStillInFlight()
    {
        // The client envelope is now written with the conflict record while the server envelope is
        // attached afterwards, so "either one present" let an operator resolve in between - and a field
        // merge or geometry choice would then run against the client envelope alone and overwrite the
        // server attributes it was supposed to preserve.
        var partial = Conflict(clientEditApplied: false) with
        {
            ServerStateJson = null,
            DetectedAt = DateTimeOffset.UtcNow,
        };
        var repository = new FakeConflictRepository(partial);
        var applier = new RecordingApplier();
        var service = CreateService(repository, applier);

        var result = await service.ResolveAsync(
            Request(ReplicaConflictResolutionAction.MergeFields) with
            {
                ActionName = "mergeFields",
                Inputs = new ReplicaConflictResolutionInputs(
                    new Dictionary<string, JsonElement>
                    {
                        ["name"] = JsonDocument.Parse("\"merged\"").RootElement.Clone(),
                    },
                    GeometrySource: null),
            });

        result.Status.Should().Be(ReplicaConflictResolutionStatus.DetectionInFlight);
        applier.Calls.Should().Be(0);
    }

    [UnitTest]
    public async Task ResolveAsync_WhenTheFinalMarkerLosesTheClaim_DoesNotReportApplied()
    {
        // A slow audit can outlive the lease and a retry can take the claim over by restamping
        // resolved_at. Clearing the local pending flag and reporting Applied would describe a
        // resolution this request no longer owns, and if the replacement fails the durable conflict
        // stays unfinalized behind a success response.
        var repository = new FakeConflictRepository(Conflict(clientEditApplied: true))
        {
            LoseClaimBeforeFinalMarker = true,
        };
        var service = CreateService(repository, new RecordingApplier());

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.AlreadyResolved);
        repository.Current.FinalizationPending.Should().BeTrue(
            "the row still belongs to the replacement claim, unfinalized");
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

    [UnitTest]
    public async Task ResolveAsync_WhenANoWriteResolutionsAssertionIsSuperseded_ReturnsStale()
    {
        // acceptClient over an edit that last-write-wins already committed plans no write, but it still
        // ASSERTS the row holds the client state. A later ordinary edit invalidates that assertion, so
        // finalizing it would record a decision that is no longer true.
        var repository = new FakeConflictRepository(Conflict(clientEditApplied: true));
        var tracker = new FakeChangeTracker();
        tracker.ChangesSinceBase.Add(Change(generation: 90));
        var applier = new RecordingApplier();
        var service = CreateService(repository, applier, tracker);

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.AcceptClient));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.Stale);
        repository.Current.Status.Should().Be(ReplicaConflictStatus.Pending);
    }

    [UnitTest]
    public async Task ResolveAsync_Defer_IsExemptFromTheStalenessAssertion()
    {
        // Deferral deliberately asserts nothing about the committed state, so a later edit must not
        // stop an operator from parking the conflict for review.
        var repository = new FakeConflictRepository(Conflict(clientEditApplied: true));
        var tracker = new FakeChangeTracker();
        tracker.ChangesSinceBase.Add(Change(generation: 90));
        var service = CreateService(repository, new RecordingApplier(), tracker);

        var result = await service.ResolveAsync(
            Request(ReplicaConflictResolutionAction.Defer) with { ActionName = "defer" });

        result.Status.Should().Be(ReplicaConflictResolutionStatus.Applied);
        repository.Current.Status.Should().Be(ReplicaConflictStatus.Deferred);
    }

    [UnitTest]
    public async Task ResolveAsync_WhenARetryChangesTheRequestedInputs_DoesNotFinalizeTheEarlierWrite()
    {
        // A mergeFields retry naming different values is a DIFFERENT requested state. Matching only
        // operator and action let it finalize the earlier committed write while the response and audit
        // described the new selection, which never landed.
        var claimed = Conflict(clientEditApplied: true) with
        {
            Status = ReplicaConflictStatus.Resolved,
            ResolutionAction = ReplicaConflictResolutionAction.MergeFields,
            ResolvedBy = "operator-1",
            ResolvedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            FinalizationPending = true,
            WriteCommitted = true,
            ResolutionInputHash = "a-different-request",
        };
        var repository = new FakeConflictRepository(claimed) { ClaimSucceeds = false };
        var applier = new RecordingApplier();
        var service = CreateService(repository, applier);

        var result = await service.ResolveAsync(
            Request(ReplicaConflictResolutionAction.MergeFields) with
            {
                ActionName = "mergeFields",
                Inputs = new ReplicaConflictResolutionInputs(
                    new Dictionary<string, JsonElement>
                    {
                        ["name"] = JsonDocument.Parse("\"different\"").RootElement.Clone(),
                    },
                    GeometrySource: null),
            });

        result.Status.Should().Be(ReplicaConflictResolutionStatus.AlreadyResolved);
        applier.Calls.Should().Be(0);
        repository.Current.FinalizationPending.Should().BeTrue(
            "the earlier write must not be finalized under a different request's description");
    }

    [UnitTest]
    public async Task ResolveAsync_WhenTheClaimIsLostMidFinalization_DoesNotReportApplied()
    {
        // A slow write can outlive the lease and have its claim released or replaced. The guarded
        // finalization update then matches nothing, and reporting success would describe a resolution
        // this request no longer owns.
        var repository = new FakeConflictRepository(Conflict(clientEditApplied: true))
        {
            LoseClaimAfterWrite = true,
        };
        var applier = new RecordingApplier();
        var audit = new NoOpAuditLog();
        var service = CreateService(repository, applier, auditLog: audit);

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.AlreadyResolved);
        audit.Records.Should().Be(0, "a request that lost its claim must not write success evidence");
    }

    [UnitTest]
    public async Task ResolveAsync_AfterAReleasedClaim_TheConflictCanBeClaimedAgain()
    {
        // A released row must return to a claimable state. `finalized` means "no attempt in flight",
        // and the claim guard requires it — leaving it false made a released conflict unclaimable and,
        // because the release also clears the actor/action identity, unresumable too.
        var repository = new FakeConflictRepository(Conflict(clientEditApplied: true));
        var service = CreateService(repository, new FailingApplier());

        var failed = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));
        failed.Status.Should().Be(ReplicaConflictResolutionStatus.WriteFailed);
        repository.Current.Status.Should().Be(ReplicaConflictStatus.Pending);
        repository.Current.FinalizationPending.Should().BeFalse("the released row is not mid-flight");

        var retried = await CreateService(repository, new RecordingApplier())
            .ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        retried.Status.Should().Be(
            ReplicaConflictResolutionStatus.Applied, "a released conflict must be resolvable again");
    }

    [UnitTest]
    public async Task ResolveAsync_ResumeIgnoresInputsTheActionDoesNotUse()
    {
        // fieldValues and geometry are documented as ignored outside their own actions, so including
        // one must not make an otherwise identical request unresumable.
        var claimed = Conflict(clientEditApplied: true) with
        {
            Status = ReplicaConflictStatus.Resolved,
            ResolutionAction = ReplicaConflictResolutionAction.KeepServer,
            ResolvedBy = "operator-1",
            ResolvedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            FinalizationPending = true,
            WriteCommitted = true,
        };
        var repository = new FakeConflictRepository(claimed) { ClaimSucceeds = false };
        var service = CreateService(repository, new RecordingApplier());

        // Claim the hash the way a request carrying an irrelevant geometry hint would have.
        repository.Replace(claimed with
        {
            ResolutionInputHash = ComputeKeepServerHashViaService(service, geometrySource: "client"),
        });

        var result = await service.ResolveAsync(
            Request(ReplicaConflictResolutionAction.KeepServer) with
            {
                Inputs = new ReplicaConflictResolutionInputs(FieldValues: null, GeometrySource: null),
            });

        result.Status.Should().Be(ReplicaConflictResolutionStatus.Applied);
    }

    /// <summary>
    /// Drives the service once to capture the hash it records for a keepServer claim carrying an
    /// irrelevant geometry hint, so the test asserts against the real normalization rather than a
    /// duplicate of it.
    /// </summary>
    private static string? ComputeKeepServerHashViaService(
        ReplicaConflictResolutionService service,
        string geometrySource)
    {
        var probeRepository = new FakeConflictRepository(Conflict(clientEditApplied: true));
        var probeService = CreateService(probeRepository, new RecordingApplier());
        probeService.ResolveAsync(
                Request(ReplicaConflictResolutionAction.KeepServer) with
                {
                    Inputs = new ReplicaConflictResolutionInputs(FieldValues: null, GeometrySource: geometrySource),
                })
            .GetAwaiter().GetResult();
        return probeRepository.Current.ResolutionInputHash;
    }

    [UnitTest]
    public async Task ResolveAsync_NoWriteResumeDuringALiveClaim_ReportsAlreadyResolved()
    {
        // A no-write plan used to skip the lease entirely and go straight to finalization. A retry
        // arriving while the first request was still inside its staleness probe would therefore audit
        // and report Applied, and the original - finding a post-conflict edit - would then release the
        // same timestamp-bound claim back to Pending.
        var live = Conflict(clientEditApplied: false) with
        {
            Status = ReplicaConflictStatus.Resolved,
            ResolutionAction = ReplicaConflictResolutionAction.KeepServer,
            ResolvedBy = "operator-1",
            ResolvedAt = DateTimeOffset.UtcNow,
            FinalizationPending = true,
            WriteCommitted = false,
            PreWriteStateToken = "token-at-claim",
        };
        var repository = new FakeConflictRepository(live) { ClaimSucceeds = false };
        var audit = new NoOpAuditLog();
        var service = CreateService(repository, new RecordingApplier(), auditLog: audit);

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.AlreadyResolved);
        audit.Records.Should().Be(0, "a live claim must not be finalized by a concurrent retry");
        repository.Current.FinalizationPending.Should().BeTrue("the original attempt still owns it");
    }

    [UnitTest]
    public async Task ResolveAsync_WhenTheWritePreconditionFails_ReportsStaleAndReleasesTheClaim()
    {
        // The staleness probe covers detection-to-now; the write's own precondition covers now-to-write.
        // An edit arriving inside that second window used to be overwritten unconditionally even though
        // the service promises to reject post-conflict changes.
        var repository = new FakeConflictRepository(Conflict(clientEditApplied: true));
        var applier = new PreconditionFailingApplier();
        var service = CreateService(repository, applier);

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.Stale);
        applier.Calls.Should().Be(1);
        repository.Current.Status.Should().Be(
            ReplicaConflictStatus.Pending, "nothing was written, so the conflict is reviewable again");
        repository.Current.FinalizationPending.Should().BeFalse();
    }

    [UnitTest]
    public async Task ResolveAsync_CapturesTheStateTokenBeforeTheStalenessProbeAndCarriesItIntoTheWrite()
    {
        // The token has to describe the same snapshot the staleness probe judged. Captured afterwards
        // it would already include an edit the probe did not see, and the write would accept exactly
        // the change the probe exists to reject.
        var repository = new FakeConflictRepository(Conflict(clientEditApplied: true));
        var applier = new RecordingApplier();
        var service = CreateService(repository, applier);

        await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        applier.LastCommand!.Value.StorageLayerId.Should().Be(10);
        applier.LastCommand!.Value.ExpectedStateToken.Should().Be("token-1");
        applier.LastCommand!.Value.ReplaceAttributes.Should().BeTrue();
        applier.Trace.Should().Equal("capture", "apply");
    }

    [UnitTest]
    public async Task ResolveAsync_WhenTheWriteOutcomeIsUnknown_KeepsTheClaimResumable()
    {
        // An indeterminate write is not a failure. Releasing the claim here means that if the write DID
        // land, the next attempt sees this resolution's own change as a post-conflict edit and returns
        // Stale forever, stranding the conflict. Keeping it claimed with WriteCommitted=false is
        // exactly the resumable state, and the write is idempotent by construction.
        var repository = new FakeConflictRepository(Conflict(clientEditApplied: true));
        var service = CreateService(repository, new IndeterminateApplier());

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.WriteOutcomeUnknown);
        repository.Current.Status.Should().Be(
            ReplicaConflictStatus.Resolved, "the claim is retained, not released");
        repository.Current.WriteCommitted.Should().BeFalse();
        repository.Current.FinalizationPending.Should().BeTrue("the resolution is resumable");

        // The retry resumes and completes it.
        var retried = await CreateService(repository, new RecordingApplier())
            .ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        retried.Status.Should().Be(ReplicaConflictResolutionStatus.Applied);
        repository.Current.FinalizationPending.Should().BeFalse();
    }

    [UnitTest]
    public async Task ResolveAsync_WhenTheStalenessProbeFaults_ReleasesTheClaim()
    {
        // The claim has already moved the record out of the reviewable state, but nothing was written
        // and the staleness question is still unanswered. Holding the claim would let a retry resume
        // straight to finalization, or take it over with the check skipped, and either path can accept
        // or overwrite the post-conflict edit the aborted probe was about to detect.
        var repository = new FakeConflictRepository(Conflict(clientEditApplied: true));
        var applier = new RecordingApplier();
        var tracker = new FakeChangeTracker { ThrowOnProbe = true };
        var service = CreateService(repository, applier, changeTracker: tracker);

        var act = () => service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        await act.Should().ThrowAsync<OperationCanceledException>();
        applier.Calls.Should().Be(0, "the write must not run when staleness is unknown");
        repository.Current.Status.Should().Be(ReplicaConflictStatus.Pending, "the claim is released");
        repository.Current.FinalizationPending.Should().BeFalse("a released row is not mid-flight");
    }

    [UnitTest]
    public async Task ResolveAsync_ResumeIgnoresFieldNameCasing()
    {
        // The planner matches operator-supplied field names to schema fields case-insensitively, so
        // `status` and `STATUS` request the identical state. Hashing them differently made the retry
        // look like a new request and left the committed write's finalization and audit unfinished.
        var claimed = Conflict(clientEditApplied: true) with
        {
            Status = ReplicaConflictStatus.Resolved,
            ResolutionAction = ReplicaConflictResolutionAction.MergeFields,
            ResolvedBy = "operator-1",
            ResolvedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            FinalizationPending = true,
            WriteCommitted = true,
        };
        var repository = new FakeConflictRepository(claimed) { ClaimSucceeds = false };
        var service = CreateService(repository, new RecordingApplier());

        // Capture the hash the way the original lower-cased request recorded it.
        repository.Replace(claimed with
        {
            ResolutionInputHash = ComputeMergeFieldsHashViaService("name"),
        });

        var result = await service.ResolveAsync(MergeFieldsRequest("NAME"));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.Applied);
        repository.Current.FinalizationPending.Should().BeFalse("the resumed resolution is complete");
    }

    private static ReplicaConflictResolutionServiceRequest MergeFieldsRequest(string fieldName)
        => Request(ReplicaConflictResolutionAction.MergeFields) with
        {
            ActionName = "mergeFields",
            Inputs = new ReplicaConflictResolutionInputs(
                new Dictionary<string, JsonElement>
                {
                    [fieldName] = JsonDocument.Parse("\"merged\"").RootElement.Clone(),
                },
                GeometrySource: null),
        };

    /// <summary>
    /// Drives the service once to capture the hash it records for a mergeFields claim naming
    /// <paramref name="fieldName"/>, so the test asserts against the real normalization rather than a
    /// duplicate of it.
    /// </summary>
    private static string? ComputeMergeFieldsHashViaService(string fieldName)
    {
        var probeRepository = new FakeConflictRepository(Conflict(clientEditApplied: true));
        CreateService(probeRepository, new RecordingApplier())
            .ResolveAsync(MergeFieldsRequest(fieldName))
            .GetAwaiter().GetResult();
        return probeRepository.Current.ResolutionInputHash;
    }

    [UnitTest]
    public async Task ResolveAsync_WhenTheResumedWriteMarkerLosesTheClaim_StopsInsteadOfFinalizing()
    {
        // A re-applied write can outlive the lease and another retry can restamp resolved_at.
        // Continuing would finalize a row this request no longer owns, and if the replacement then
        // fails and releases its claim the conflict is left pending behind a committed feature change.
        var expired = Conflict(clientEditApplied: true) with
        {
            Status = ReplicaConflictStatus.Resolved,
            ResolutionAction = ReplicaConflictResolutionAction.KeepServer,
            ResolvedBy = "operator-1",
            ResolvedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            FinalizationPending = true,
            WriteCommitted = false,
            PreWriteStateToken = "token-at-claim",
        };
        var repository = new FakeConflictRepository(expired)
        {
            ClaimSucceeds = false,
            LoseClaimOnWriteMarker = true,
        };
        var audit = new NoOpAuditLog();
        var service = CreateService(repository, new RecordingApplier(), auditLog: audit);

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.AlreadyResolved);
        audit.Records.Should().Be(0, "a request that lost its claim must not write success evidence");
        repository.Current.FinalizationPending.Should().BeTrue();
    }

    [UnitTest]
    public async Task ResolveAsync_WhenThePreWriteTokenCannotBePersisted_StopsBeforeWriting()
    {
        // Capture plus staleness probe can outlast the lease. If a retry took the claim over in that
        // window this guarded update matches nothing, and continuing would write under an ownership
        // another attempt now holds.
        var repository = new FakeConflictRepository(Conflict(clientEditApplied: true))
        {
            LoseClaimOnTokenPersist = true,
        };
        var applier = new RecordingApplier();
        var service = CreateService(repository, applier);

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.AlreadyResolved);
        applier.Calls.Should().Be(0, "no write may run once ownership is lost");
    }

    [UnitTest]
    public async Task ResolveAsync_WhenTheRowIsObservedAbsent_TellsTheApplierSoItDoesNotDeleteAReinsert()
    {
        // A delete dispatched against a row observed absent would remove one reinserted with the same
        // object id in the meantime, and the shared pipeline cannot express "only if still absent".
        var deleteConflict = Conflict(clientEditApplied: false) with
        {
            ConflictType = ReplicaConflictType.DeleteUpdate,
            ClientStateJson = null,
        };
        var repository = new FakeConflictRepository(deleteConflict);
        var applier = new AbsentRowApplier();
        var service = CreateService(repository, applier);

        await service.ResolveAsync(Request(ReplicaConflictResolutionAction.AcceptClient));

        applier.LastCommand!.Value.Effect.Should().Be(ReplicaConflictResolutionEffect.DeleteFeature);
        applier.LastCommand!.Value.ExpectedRowAbsent.Should().BeTrue();
        applier.LastCommand!.Value.ExpectedStateToken.Should().BeNull();
        repository.Current.PreWriteRowAbsent.Should().BeTrue(
            "the absent snapshot must survive if this write later needs recovery");
    }

    [UnitTest]
    public async Task ResolveAsync_AbsentRowUnknownCommitRetryReusesDurableAbsence()
    {
        // A null token is the correct snapshot for an absent row, not a missing pre-write phase. If
        // the first transaction loses its acknowledgement, the same-request retry must re-apply with
        // expected absence instead of rejecting the durable claim as unbound.
        var repository = new FakeConflictRepository(Conflict(clientEditApplied: true));
        var applier = new AbsentRowIndeterminateThenAppliedApplier();
        var service = CreateService(repository, applier);

        var first = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));
        var retry = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        first.Status.Should().Be(ReplicaConflictResolutionStatus.WriteOutcomeUnknown);
        retry.Status.Should().Be(ReplicaConflictResolutionStatus.Applied);
        repository.Current.PreWriteRowAbsent.Should().BeTrue();
        applier.Commands.Should().HaveCount(2);
        applier.Commands[1].ExpectedStateToken.Should().BeNull();
        applier.Commands[1].ExpectedRowAbsent.Should().BeTrue(
            "recovery must restore the claim-time absence precondition");
    }

    [UnitTest]
    public async Task ResolveAsync_WhenARecoveredWriteCannotBeAttributed_RebaselinesTheConflict()
    {
        // Releasing alone would strand it: if the earlier write did land, every later attempt's
        // staleness probe trips over this resolution's own change and answers Stale forever.
        var expired = Conflict(clientEditApplied: true) with
        {
            Status = ReplicaConflictStatus.Resolved,
            ResolutionAction = ReplicaConflictResolutionAction.KeepServer,
            ResolvedBy = "operator-1",
            ResolvedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            FinalizationPending = true,
            WriteCommitted = false,
            PreWriteStateToken = "token-at-claim",
        };
        var repository = new FakeConflictRepository(expired) { ClaimSucceeds = false };
        var service = CreateService(repository, new PreconditionFailingApplier());

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.Stale);
        repository.Current.Status.Should().Be(ReplicaConflictStatus.Pending);
        repository.DetectionUpdates.Should().ContainSingle()
            .Which.ResolutionBaseGeneration.Should().Be(
                FakeChangeTracker.Generation, "the conflict is re-pointed at the state that is there now");

        // The refreshed envelope IS the current server side, so the attribution that described the
        // original upload must not survive: a later acceptClient would otherwise take its no-op
        // shortcut and report success while the row still held the server state.
        repository.Current.ClientEditApplied.Should().BeFalse();
        repository.Current.ClientEditOutcomeUnknown.Should().BeFalse();
        repository.Current.ClientEditSuperseded.Should().BeFalse();
    }

    [UnitTest]
    public async Task ResolveAsync_RebaselineOntoADeletedRow_ClearsTheServerEnvelope()
    {
        // null means "leave unchanged" for every other detection field, so without an explicit clear the
        // record kept advertising a server state the feature no longer has, and keepServer would then
        // report success against it.
        var expired = Conflict(clientEditApplied: true) with
        {
            Status = ReplicaConflictStatus.Resolved,
            ResolutionAction = ReplicaConflictResolutionAction.KeepServer,
            ResolvedBy = "operator-1",
            ResolvedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            FinalizationPending = true,
            WriteCommitted = false,
            PreWriteStateToken = "token-at-claim",
        };
        var repository = new FakeConflictRepository(expired) { ClaimSucceeds = false };
        var service = CreateService(repository, new AbsentRowPreconditionFailingApplier());

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.Stale);
        repository.Current.ServerStateJson.Should().BeNull("the row is gone, so there is no server state");
        repository.Current.ConflictType.Should().Be(
            ReplicaConflictType.UpdateDelete, "a client edit against a server deletion owes no server envelope");
    }

    [UnitTest]
    public async Task ResolveAsync_RebaselineCompletedClientDelete_AsDeleteDelete()
    {
        var expired = Conflict(clientEditApplied: false) with
        {
            ConflictType = ReplicaConflictType.DeleteUpdate,
            ClientStateJson = null,
            Status = ReplicaConflictStatus.Resolved,
            ResolutionAction = ReplicaConflictResolutionAction.AcceptClient,
            ResolvedBy = "operator-1",
            ResolvedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            FinalizationPending = true,
            WriteCommitted = false,
            PreWriteStateToken = "token-at-claim",
        };
        var repository = new FakeConflictRepository(expired) { ClaimSucceeds = false };
        var service = CreateService(repository, new AbsentRowPreconditionFailingApplier());

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.AcceptClient));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.Stale);
        repository.Current.Status.Should().Be(ReplicaConflictStatus.Pending);
        repository.Current.ConflictType.Should().Be(ReplicaConflictType.DeleteDelete,
            "an absent row after a client delete represents the same deletion on both sides");
        repository.Current.ClientStateJson.Should().BeNull();
        repository.Current.ServerStateJson.Should().BeNull();
    }

    /// <summary>Applier whose precondition fails and whose row has since been deleted.</summary>
    private sealed class AbsentRowPreconditionFailingApplier : IReplicaConflictResolutionApplier
    {
        public Task<ReplicaConflictRowSnapshot> CaptureStateTokenAsync(
            int storageLayerId, long objectId, CancellationToken cancellationToken = default)
            => Task.FromResult(new ReplicaConflictRowSnapshot(false, null, null));

        public Task<ReplicaConflictApplyResult> ApplyAsync(
            ReplicaConflictResolutionCommand command,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ReplicaConflictApplyResult(
                Applied: false, FailureMessage: "precondition failed", PreconditionFailed: true));
    }

    [UnitTest]
    public async Task ResolveAsync_NoWriteTakeover_RerunsTheStalenessProbe()
    {
        // The takeover says nothing about whether the attempt it replaced finished its probe, and a
        // no-write resume goes straight to audit and finalization.
        var expired = Conflict(clientEditApplied: false) with
        {
            Status = ReplicaConflictStatus.Resolved,
            ResolutionAction = ReplicaConflictResolutionAction.KeepServer,
            ResolvedBy = "operator-1",
            ResolvedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            FinalizationPending = true,
        };
        var repository = new FakeConflictRepository(expired) { ClaimSucceeds = false };
        var tracker = new FakeChangeTracker();
        tracker.ChangesSinceBase.Add(Change(generation: 60));
        var audit = new NoOpAuditLog();
        var service = CreateService(repository, new RecordingApplier(), changeTracker: tracker, auditLog: audit);

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.Stale);
        audit.Records.Should().Be(0, "a post-conflict edit must not be finalized as resolved by a retry");
        repository.Current.Status.Should().Be(ReplicaConflictStatus.Pending);
    }

    [UnitTest]
    public async Task ResolveAsync_RecoveryWithoutARetainedToken_RefusesToReapply()
    {
        // The claim's pre-write phase never became durable, so there is no snapshot to bind the
        // recovered write to - and recovery skips the staleness probe by design.
        var expired = Conflict(clientEditApplied: true) with
        {
            Status = ReplicaConflictStatus.Resolved,
            ResolutionAction = ReplicaConflictResolutionAction.KeepServer,
            ResolvedBy = "operator-1",
            ResolvedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            FinalizationPending = true,
            WriteCommitted = false,
            PreWriteStateToken = null,
        };
        var repository = new FakeConflictRepository(expired) { ClaimSucceeds = false };
        var applier = new RecordingApplier();
        var service = CreateService(repository, applier);

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.Stale);
        applier.Calls.Should().Be(0, "an unbindable write must not run");
        repository.Current.Status.Should().Be(ReplicaConflictStatus.Pending);
    }

    [UnitTest]
    public async Task ResolveAsync_RecoveryReappliesAgainstTheRetainedToken_NotARetryTimeRead()
    {
        // Recovery skips the staleness probe on purpose, so a token derived now would describe whatever
        // is in the row at this moment - including a normal edit that landed during the lease - and the
        // precondition would happily overwrite it.
        var expired = Conflict(clientEditApplied: true) with
        {
            Status = ReplicaConflictStatus.Resolved,
            ResolutionAction = ReplicaConflictResolutionAction.KeepServer,
            ResolvedBy = "operator-1",
            ResolvedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            FinalizationPending = true,
            WriteCommitted = false,
            PreWriteStateToken = "token-at-claim",
        };
        var repository = new FakeConflictRepository(expired) { ClaimSucceeds = false };
        var applier = new RecordingApplier();
        var service = CreateService(repository, applier);

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.Applied);
        applier.LastCommand!.Value.ExpectedStateToken.Should().Be(
            "token-at-claim", "the recovered write is bound to the row as it was when the claim was taken");
    }

    [UnitTest]
    public async Task ResolveAsync_PersistsThePreWriteTokenOnTheClaim()
    {
        var repository = new FakeConflictRepository(Conflict(clientEditApplied: true));
        var service = CreateService(repository, new RecordingApplier());

        await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        repository.Current.PreWriteStateToken.Should().Be("token-1");
        repository.Current.PreWriteRowAbsent.Should().BeFalse();
    }

    [UnitTest]
    public async Task ResolveAsync_WhenAnotherRecoveryWinsTheTakeover_DoesNotReapplyTheWrite()
    {
        // Recovery re-dispatches the write, so it has to be single-winner in its own right: two
        // retries that both judged the same expired claim abandoned would otherwise both re-apply, and
        // a failure in one would release a claim the other had already committed against.
        var expired = Conflict(clientEditApplied: true) with
        {
            Status = ReplicaConflictStatus.Resolved,
            ResolutionAction = ReplicaConflictResolutionAction.KeepServer,
            ResolvedBy = "operator-1",
            ResolvedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            FinalizationPending = true,
            WriteCommitted = false,
            PreWriteStateToken = "token-at-claim",
        };
        var repository = new FakeConflictRepository(expired) { TakeoverLoses = true };
        var applier = new RecordingApplier();
        var service = CreateService(repository, applier);

        var result = await service.ResolveAsync(Request(ReplicaConflictResolutionAction.KeepServer));

        result.Status.Should().Be(ReplicaConflictResolutionStatus.AlreadyResolved);
        applier.Calls.Should().Be(0, "the losing recovery must not re-dispatch the write");
        repository.Current.Status.Should().Be(ReplicaConflictStatus.Resolved, "the claim is untouched");
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

        /// <summary>Object-id filters supplied to the scoped staleness probe.</summary>
        public List<long[]> ObjectIdFilters { get; } = [];

        /// <summary>Makes the staleness probe fault, as a cancelled request or provider error would.</summary>
        public bool ThrowOnProbe { get; init; }

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
        {
            ObjectIdFilters.Add(objectIds?.ToArray() ?? []);

            // Honour sinceGeneration as the real tracker does: the resolution preconditions probe
            // different windows and would otherwise see each other's changes.
            return ThrowOnProbe
                ? Task.FromException<IReadOnlyList<FeatureChange>>(new OperationCanceledException())
                : Task.FromResult<IReadOnlyList<FeatureChange>>(
                    ChangesSinceBase
                        .Where(c => c.Generation > sinceGeneration)
                        .Where(c => objectIds is null || objectIds.Contains(c.ObjectId))
                        .ToList());
        }
    }

    private sealed class NoOpAuditLog : IAuditLog
    {
        public bool Throws { get; set; }

        public int Records { get; private set; }

        public Task<string?> RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            if (Throws)
            {
                throw new InvalidOperationException("audit sink unavailable");
            }

            Records++;
            return Task.FromResult<string?>("audit-test");
        }
    }

    private sealed class RecordingApplier : IReplicaConflictResolutionApplier
    {
        public int Calls { get; private set; }

        public ReplicaConflictResolutionCommand? LastCommand { get; private set; }

        /// <summary>Order of seam calls, so the token capture can be asserted to precede the probe.</summary>
        public List<string> Trace { get; } = [];

        public Task<ReplicaConflictRowSnapshot> CaptureStateTokenAsync(
            int storageLayerId, long objectId, CancellationToken cancellationToken = default)
        {
            Trace.Add("capture");
            return Task.FromResult(new ReplicaConflictRowSnapshot(true, "token-1", "{\"attributes\":{}}"));
        }

        public Task<ReplicaConflictApplyResult> ApplyAsync(
            ReplicaConflictResolutionCommand command,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastCommand = command;
            Trace.Add("apply");
            return Task.FromResult(new ReplicaConflictApplyResult(Applied: true, FailureMessage: null));
        }
    }

    private sealed class FailingApplier : IReplicaConflictResolutionApplier
    {
        public Task<ReplicaConflictRowSnapshot> CaptureStateTokenAsync(
            int storageLayerId, long objectId, CancellationToken cancellationToken = default)
            => Task.FromResult(new ReplicaConflictRowSnapshot(true, "token-1", "{\"attributes\":{}}"));

        public Task<ReplicaConflictApplyResult> ApplyAsync(
            ReplicaConflictResolutionCommand command,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ReplicaConflictApplyResult(Applied: false, FailureMessage: "write rejected"));
    }

    /// <summary>Applier that observes no row, as a resolution targeting an already-deleted feature does.</summary>
    private sealed class AbsentRowApplier : IReplicaConflictResolutionApplier
    {
        public ReplicaConflictResolutionCommand? LastCommand { get; private set; }

        public Task<ReplicaConflictRowSnapshot> CaptureStateTokenAsync(
            int storageLayerId, long objectId, CancellationToken cancellationToken = default)
            => Task.FromResult(new ReplicaConflictRowSnapshot(false, null, null));

        public Task<ReplicaConflictApplyResult> ApplyAsync(
            ReplicaConflictResolutionCommand command,
            CancellationToken cancellationToken = default)
        {
            LastCommand = command;
            return Task.FromResult(new ReplicaConflictApplyResult(Applied: true, FailureMessage: null));
        }
    }

    /// <summary>Absent-row applier whose first write loses its transaction acknowledgement.</summary>
    private sealed class AbsentRowIndeterminateThenAppliedApplier : IReplicaConflictResolutionApplier
    {
        public List<ReplicaConflictResolutionCommand> Commands { get; } = [];

        public Task<ReplicaConflictRowSnapshot> CaptureStateTokenAsync(
            int storageLayerId, long objectId, CancellationToken cancellationToken = default)
            => Task.FromResult(new ReplicaConflictRowSnapshot(false, null, null));

        public Task<ReplicaConflictApplyResult> ApplyAsync(
            ReplicaConflictResolutionCommand command,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return Task.FromResult(Commands.Count == 1
                ? new ReplicaConflictApplyResult(
                    Applied: false,
                    FailureMessage: "commit outcome unknown",
                    CommitOutcomeUnknown: true)
                : new ReplicaConflictApplyResult(Applied: true, FailureMessage: null));
        }
    }

    /// <summary>Applier whose precondition caught an edit arriving just before the write transaction.</summary>
    private sealed class PreconditionFailingApplier : IReplicaConflictResolutionApplier
    {
        public Task<ReplicaConflictRowSnapshot> CaptureStateTokenAsync(
            int storageLayerId, long objectId, CancellationToken cancellationToken = default)
            => Task.FromResult(new ReplicaConflictRowSnapshot(true, "token-1", "{\"attributes\":{}}"));

        public int Calls { get; private set; }

        public Task<ReplicaConflictApplyResult> ApplyAsync(
            ReplicaConflictResolutionCommand command,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ReplicaConflictApplyResult(
                Applied: false, FailureMessage: "precondition failed", PreconditionFailed: true));
        }
    }

    /// <summary>Applier whose write may or may not have committed, as a lost commit ack reports.</summary>
    private sealed class IndeterminateApplier : IReplicaConflictResolutionApplier
    {
        public Task<ReplicaConflictRowSnapshot> CaptureStateTokenAsync(
            int storageLayerId, long objectId, CancellationToken cancellationToken = default)
            => Task.FromResult(new ReplicaConflictRowSnapshot(true, "token-1", "{\"attributes\":{}}"));

        public Task<ReplicaConflictApplyResult> ApplyAsync(
            ReplicaConflictResolutionCommand command,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ReplicaConflictApplyResult(
                Applied: false, FailureMessage: "commit outcome unknown", CommitOutcomeUnknown: true));
    }

    private sealed class ThrowingApplier : IReplicaConflictResolutionApplier
    {
        public Task<ReplicaConflictRowSnapshot> CaptureStateTokenAsync(
            int storageLayerId, long objectId, CancellationToken cancellationToken = default)
            => Task.FromResult(new ReplicaConflictRowSnapshot(true, "token-1", "{\"attributes\":{}}"));

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

        /// <summary>Simulates the claim being released or replaced while the write is in flight.</summary>
        public bool LoseClaimAfterWrite { get; init; }

        /// <summary>Simulates another recovery winning the takeover first.</summary>
        public bool TakeoverLoses { get; init; }

        /// <summary>Simulates a retry taking the claim over while this request's audit is running.</summary>
        public bool LoseClaimBeforeFinalMarker { get; init; }

        /// <summary>Simulates a retry taking the claim over while a re-applied write is running.</summary>
        public bool LoseClaimOnWriteMarker { get; init; }

        /// <summary>Simulates a retry taking the claim over during the capture/probe phase.</summary>
        public bool LoseClaimOnTokenPersist { get; init; }

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

        /// <summary>Detection-state corrections applied after the record was written.</summary>
        public List<ReplicaConflictDetectionUpdate> DetectionUpdates { get; } = [];

        public Task<bool> TryUpdateDetectionStateAsync(
            ReplicaConflictDetectionUpdate update,
            CancellationToken cancellationToken = default)
        {
            // Mirror the real guard: detection corrections only apply while the conflict is pending.
            if (Current.Status != ReplicaConflictStatus.Pending)
            {
                return Task.FromResult(false);
            }

            DetectionUpdates.Add(update);
            Current = Current with
            {
                ConflictType = update.ConflictType ?? Current.ConflictType,
                ServerStateJson = update.ClearServerState
                    ? null
                    : update.ServerStateJson ?? Current.ServerStateJson,
                ResolutionBaseGeneration = update.ResolutionBaseGeneration ?? Current.ResolutionBaseGeneration,
                ClientEditApplied = update.ClientEditApplied ?? Current.ClientEditApplied,
                ClientEditOutcomeUnknown = update.ClientEditOutcomeUnknown ?? Current.ClientEditOutcomeUnknown,
                ClientEditSuperseded = update.ClientEditSuperseded ?? Current.ClientEditSuperseded,
            };
            return Task.FromResult(true);
        }

        public Task<bool> TryTakeOverClaimAsync(
            string conflictId,
            string resolvedBy,
            ReplicaConflictResolutionAction action,
            DateTimeOffset expectedResolvedAt,
            DateTimeOffset newResolvedAt,
            CancellationToken cancellationToken = default)
        {
            // Mirror the real guard: single-winner takeover bound to the claim being replaced.
            if (TakeoverLoses ||
                !string.Equals(resolvedBy, Current.ResolvedBy, StringComparison.Ordinal) ||
                action != Current.ResolutionAction ||
                expectedResolvedAt != Current.ResolvedAt ||
                !Current.FinalizationPending)
            {
                return Task.FromResult(false);
            }

            Current = Current with { ResolvedAt = newResolvedAt };
            return Task.FromResult(true);
        }

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
                ResolutionInputHash = null,
                WriteCommitted = false,
                PreWriteStateToken = null,
                PreWriteRowAbsent = null,
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

            if (LoseClaimAfterWrite && update.WriteCommitted != true)
            {
                return Task.FromResult(false);
            }

            // Only the final marker fails, so the generation stamp and audit still run: that is the
            // state a claim taken over during a slow audit actually leaves behind.
            if (LoseClaimBeforeFinalMarker && update.Finalized == true)
            {
                return Task.FromResult(false);
            }

            if (LoseClaimOnWriteMarker && update.WriteCommitted == true)
            {
                return Task.FromResult(false);
            }

            if (LoseClaimOnTokenPersist &&
                (update.PreWriteStateToken is not null || update.PreWriteRowAbsent is not null))
            {
                return Task.FromResult(false);
            }

            // Mirror the real guard: progress only applies to the claim that produced it.
            if (!string.Equals(update.ResolvedBy, Current.ResolvedBy, StringComparison.Ordinal) ||
                update.Action != Current.ResolutionAction ||
                update.ResolvedAt != Current.ResolvedAt)
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
                PreWriteStateToken = update.PreWriteStateToken ?? Current.PreWriteStateToken,
                PreWriteRowAbsent = update.PreWriteRowAbsent ?? Current.PreWriteRowAbsent,
            };
            return Task.FromResult(true);
        }

        public Task<ReplicaConflictResolutionOutcome> ResolveAsync(
            ReplicaConflictResolution resolution,
            CancellationToken cancellationToken = default)
        {
            // Mirror the real guard: a claim only wins on a row that is not mid-flight.
            if (!ClaimSucceeds || Current.FinalizationPending)
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
                ResolutionInputHash = resolution.ResolutionInputHash,
                WriteCommitted = false,
                PreWriteStateToken = null,
                PreWriteRowAbsent = null,
                FinalizationPending = true,
            };
            return Task.FromResult(new ReplicaConflictResolutionOutcome(Current, Applied: true));
        }
    }
}

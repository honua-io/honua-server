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

    private static ReplicaConflictResolutionService CreateService(
        FakeConflictRepository repository,
        IReplicaConflictResolutionApplier applier)
        => new(
            repository,
            new FakeChangeTracker(),
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
        ClientEditApplied = clientEditApplied,
        ClientStateJson = """{"attributes":{"objectid":42,"name":"client"}}""",
        ServerStateJson = """{"attributes":{"objectid":42,"name":"server"}}""",
        DetectedAt = DateTimeOffset.UtcNow,
    };

    private sealed class FakeChangeTracker : IChangeTracker
    {
        public const long Generation = 77L;

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
            => Task.FromResult<IReadOnlyList<FeatureChange>>([]);
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
            };
            return Task.FromResult(new ReplicaConflictResolutionOutcome(Current, Applied: true));
        }
    }
}

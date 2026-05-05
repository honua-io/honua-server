// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Events.Outbox;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Events.Outbox;
using Honua.ServiceDefaults;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Events.Outbox;

[Protocol(TestProtocols.TestQuality)]
public sealed class OutboxDispatcherBackgroundServiceTests
{
    [UnitTest]
    public async Task ExecuteOnceAsync_DispatchesPendingRow_AndMarksDispatched()
    {
        // Happy path: dispatcher claims a pending row, decodes the canonical payload, publishes,
        // and marks the row dispatched. Verifies that the canonical event_payload field flows end-to-end
        // and that the strict publish path (#692) is the one driven by the dispatcher.
        var (repository, capability) = BuildRepository(includePendingRow: true, eventId: "evt-happy");
        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        FeatureChangeEventRequest? captured = null;
        publisher
            .When(p => p.PublishStrictAsync(Arg.Any<FeatureChangeEventRequest>(), Arg.Any<CancellationToken>()))
            .Do(call => captured = call.Arg<FeatureChangeEventRequest>());

        var dispatcher = BuildDispatcher(repository, capability, publisher);

        var dispatched = await dispatcher.ExecuteOnceAsync(CancellationToken.None);

        dispatched.Should().Be(1);
        captured.Should().NotBeNull();
        captured!.EventId.Should().Be("evt-happy");
        repository.Dispatched.Should().ContainSingle()
            .Which.OutboxId.Should().Be(repository.OnlyOutboxId);
        // The terminal update must use the same claim owner the row was claimed with so a
        // recovered/reclaimed lease can no-op stale workers (#692).
        repository.Dispatched[0].OwnerNodeId.Should().Be(repository.LastClaimNodeId);
        repository.Failed.Should().BeEmpty();
        // The dispatcher must not fall back to the best-effort publish path; otherwise a
        // failed durable append would be silently swapped for a retry-queue enqueue.
        await publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteOnceAsync_PublishFailure_MarksRowFailed_AndCountsRetry()
    {
        var (repository, capability) = BuildRepository(includePendingRow: true, eventId: "evt-fail");
        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        publisher
            .PublishStrictAsync(Arg.Any<FeatureChangeEventRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("downstream offline")));

        var dispatcher = BuildDispatcher(repository, capability, publisher);

        var dispatched = await dispatcher.ExecuteOnceAsync(CancellationToken.None);

        dispatched.Should().Be(0, "publish failure must not count as dispatched");
        repository.Failed.Should().ContainSingle()
            .Which.OutboxId.Should().Be(repository.OnlyOutboxId);
        repository.Failed[0].OwnerNodeId.Should().Be(repository.LastClaimNodeId);
        repository.Dispatched.Should().BeEmpty();
    }

    [UnitTest]
    public async Task ExecuteOnceAsync_CorruptPayload_MarksFailed_WithoutCallingPublisher()
    {
        var repository = new FakeOutboxRepository();
        var corruptEntry = BuildEntry(eventId: "evt-corrupt") with { EventPayload = "{not-json" };
        repository.Pending.Enqueue(corruptEntry);

        var capability = Substitute.For<IOutboxCapabilityProvider>();
        capability.SupportsTransactionalOutbox.Returns(true);

        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        var dispatcher = BuildDispatcher(repository, capability, publisher);

        var dispatched = await dispatcher.ExecuteOnceAsync(CancellationToken.None);

        dispatched.Should().Be(0);
        await publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default);
        await publisher.DidNotReceiveWithAnyArgs().PublishStrictAsync(default!, default);
        repository.Failed.Should().ContainSingle();
        repository.Failed[0].OutboxId.Should().Be(corruptEntry.OutboxId);
        repository.Failed[0].OwnerNodeId.Should().Be(repository.LastClaimNodeId);
        repository.Failed[0].Error.Should().Be("Outbox payload decode failed.");
    }

    [UnitTest]
    public async Task ExecuteOnceAsync_StaleClaimAfterPublish_DoesNotCountAsDispatched()
    {
        // Recovery race (#692): the worker publishes successfully but its claim was already
        // recovered (and possibly reclaimed by another node) by the time MarkDispatchedAsync
        // runs. The repository returns false; the dispatcher must not count the row as
        // dispatched and must not overwrite the new owner's eventual terminal state.
        var (repository, capability) = BuildRepository(includePendingRow: true, eventId: "evt-stale");
        var pendingEntry = repository.Pending.First();
        repository.StaleClaims.Add(pendingEntry.OutboxId);

        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        var dispatcher = BuildDispatcher(repository, capability, publisher, nodeId: "node-stale");

        var dispatched = await dispatcher.ExecuteOnceAsync(CancellationToken.None);

        dispatched.Should().Be(0, "a stale-claim outcome must not count as dispatched");
        repository.Dispatched.Should().BeEmpty("the row's terminal state belongs to the new owner now");
        repository.Failed.Should().BeEmpty("a stale claim is neither dispatched nor failed from this worker's perspective");
    }

    [UnitTest]
    public async Task ExecuteOnceAsync_WhenCapabilityProviderReportsFalse_DispatcherIsAbsent()
    {
        // This mirrors the real DI behavior: providers that do not support transactional outbox
        // do not register an IFeatureChangeOutboxRepository. The dispatcher must short-circuit
        // gracefully when the repository is missing (e.g., DuckDB-only deployment).
        var capability = Substitute.For<IOutboxCapabilityProvider>();
        capability.SupportsTransactionalOutbox.Returns(false);
        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        var dispatcher = BuildDispatcher(repository: null, capability, publisher);

        var dispatched = await dispatcher.ExecuteOnceAsync(CancellationToken.None);

        dispatched.Should().Be(0);
        await publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteOnceAsync_ClaimFailure_RecordsStorageFailureForReadinessProbe()
    {
        // Storage-poll guard (#692): a missing outbox table or permission issue causes
        // every storage call (recovery, claim, backlog) to throw on every pass. The
        // dispatcher must surface the failure on IOutboxHealth so the readiness probe
        // stops reporting Healthy ("awaiting first pass") indefinitely. With all three
        // factories set, no successful storage poll has been recorded so the failure
        // timestamp is "pending" relative to a null success timestamp.
        var (repository, capability) = BuildRepository(includePendingRow: true, eventId: "evt-claim-fail");
        var fault = () => (Exception)new InvalidOperationException("relation honua.feature_change_outbox does not exist");
        repository.RecoveryFailureFactory = fault;
        repository.ClaimFailureFactory = fault;
        repository.BacklogFailureFactory = fault;

        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        var dispatcher = BuildDispatcher(repository, capability, publisher);

        var dispatched = await dispatcher.ExecuteOnceAsync(CancellationToken.None);

        dispatched.Should().Be(0);
        AssertStorageFailureRecorded(dispatcher, expectFirstSuccessfulPoll: false);
    }

    [UnitTest]
    public async Task ExecuteOnceAsync_BacklogFailureAfterClaimSuccess_FlipsStorageFailureFlag()
    {
        // After a healthy pass, an intermittent backlog query failure (e.g., transient
        // DB hiccup) flips LastStorageFailureAt. LastSuccessfulPollAt remains set from
        // the prior claim success, so health reports Degraded with a stale snapshot
        // rather than Unhealthy.
        var (repository, capability) = BuildRepository(includePendingRow: true, eventId: "evt-backlog-fail");
        repository.BacklogFailureFactory = () => new InvalidOperationException("connection terminated");

        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        var dispatcher = BuildDispatcher(repository, capability, publisher);

        var dispatched = await dispatcher.ExecuteOnceAsync(CancellationToken.None);

        dispatched.Should().Be(1, "the row dispatched even though the post-pass backlog refresh failed");
        AssertStorageFailureRecorded(dispatcher, expectFirstSuccessfulPoll: true);
    }

    [UnitTest]
    public async Task ExecuteOnceAsync_ClaimFailureWithBacklogSuccess_StillReportsPendingClaimFailure()
    {
        // Per-kind tracking guard (#692): when ClaimPendingAsync throws, the dispatcher's
        // catch block still calls UpdateBacklogAsync to refresh the snapshot if backlog
        // reads still work. With a single shared timestamp pair, the backlog success
        // would post-date the claim failure and the probe would treat the failure as
        // resolved — even though the claim path is still broken and no rows are being
        // dispatched. Per-kind tracking keeps the claim-poll failure pending while the
        // backlog kind reports its own success.
        var (repository, capability) = BuildRepository(includePendingRow: false, eventId: "evt-claim-only");
        repository.ClaimFailureFactory = () => new InvalidOperationException("relation honua.feature_change_outbox does not exist");
        // RecoveryFailureFactory and BacklogFailureFactory remain null so those polls succeed.

        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        var dispatcher = BuildDispatcher(repository, capability, publisher);

        var dispatched = await dispatcher.ExecuteOnceAsync(CancellationToken.None);

        dispatched.Should().Be(0, "claim failed so no rows could be dispatched");
        AssertClaimFailureMaskedByBacklogSuccess(dispatcher);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1859:Use concrete types when possible for improved performance",
        Justification = "Verifies the IOutboxHealth contract that the health-check probe consumes.")]
    private static void AssertClaimFailureMaskedByBacklogSuccess(IOutboxHealth health)
    {
        health.IsStoragePollFailing.Should().BeTrue(
            "claim is still failing even though backlog succeeded after it");
        health.LastClaimPollFailureAt.Should().NotBeNull("the claim path threw");
        health.LastClaimPollSuccessAt.Should().BeNull("no claim has ever succeeded on this dispatcher");
        health.LastBacklogPollSuccessAt.Should().NotBeNull(
            "backlog read after the claim failure must still record its own success");
        health.LastBacklogPollFailureAt.Should().BeNull("backlog never threw in this test");
    }

    [UnitTest]
    public async Task ExecuteOnceAsync_StoragePollSuccess_ClearsPriorFailureFlag()
    {
        // Recovery guard: once a successful poll lands AFTER a failure, the readiness
        // probe stops reporting Degraded — the dispatcher keeps both timestamps so the
        // probe compares them, naturally resolving when the underlying storage problem
        // recovers without the dispatcher having to explicitly clear the failure marker
        // (which would mask persistent claim failures whose backlog reads still work).
        var (repository, capability) = BuildRepository(includePendingRow: false, eventId: "evt-resume");
        var fault = () => (Exception)new InvalidOperationException("transient");
        repository.RecoveryFailureFactory = fault;
        repository.ClaimFailureFactory = fault;
        repository.BacklogFailureFactory = fault;
        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        var dispatcher = BuildDispatcher(repository, capability, publisher);

        await dispatcher.ExecuteOnceAsync(CancellationToken.None);
        AssertPendingStorageFailure(dispatcher, expectPending: true);

        // Storage recovers — next pass succeeds and the success timestamp now post-dates
        // the failure so the probe sees no pending failure on inspection.
        repository.RecoveryFailureFactory = null;
        repository.ClaimFailureFactory = null;
        repository.BacklogFailureFactory = null;
        await Task.Delay(5).ConfigureAwait(false); // ensure success timestamp clearly > failure timestamp
        await dispatcher.ExecuteOnceAsync(CancellationToken.None);

        AssertPendingStorageFailure(dispatcher, expectPending: false);
    }

    [UnitTest]
    public async Task ExecuteOnceAsync_StaleClaimAfterPublishFailure_DoesNotIncrementFailureCounter()
    {
        // Counter source-of-truth (#692): when MarkFailedAsync returns false (the claim
        // was reset/reclaimed before we could record the terminal state), the dispatcher
        // must not bump honua.outbox.failed_total or honua.outbox.dead_lettered_total.
        // The new owner records its own terminal state and counter; double-counting here
        // would inflate retry/dead-letter rates and trigger spurious alerts.
        var (repository, capability) = BuildRepository(includePendingRow: true, eventId: "evt-stale-fail");
        var pendingEntry = repository.Pending.First();
        repository.StaleClaims.Add(pendingEntry.OutboxId);

        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        publisher
            .PublishStrictAsync(Arg.Any<FeatureChangeEventRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("downstream offline")));

        var dispatcher = BuildDispatcher(repository, capability, publisher);

        using var failedListener = MeterSampleCollector.Subscribe("honua.outbox.failed_total");
        using var deadLetteredListener = MeterSampleCollector.Subscribe("honua.outbox.dead_lettered_total");

        var dispatched = await dispatcher.ExecuteOnceAsync(CancellationToken.None);

        dispatched.Should().Be(0);
        repository.Failed.Should().BeEmpty(
            "MarkFailedAsync returns false on a stale claim, so no terminal state is recorded by this worker");
        failedListener.Total.Should().Be(0,
            "stale-claim outcome must not increment the failure counter");
        deadLetteredListener.Total.Should().Be(0,
            "stale-claim outcome must not increment the dead-letter counter");
    }

    [UnitTest]
    public async Task ExecuteOnceAsync_PayloadDecodeAtRetryExhaustion_RoutesToDeadLetterCounter()
    {
        // Symmetric counter routing (#692): the payload-decode failure path must go
        // through the same dead-letter calculation as the publish-failure path. Before
        // this fix, decode failures always incremented honua.outbox.failed_total even
        // when retry_count + 1 reached MaxRetries and the row transitioned to
        // dead_lettered, so honua.outbox.dead_lettered_total under-counted real
        // dead-letter transitions originating from corrupt payloads.
        var repository = new FakeOutboxRepository();
        var capability = Substitute.For<IOutboxCapabilityProvider>();
        capability.SupportsTransactionalOutbox.Returns(true);

        // BuildDispatcher uses MaxRetries=3, so a row at RetryCount=2 will dead-letter
        // on its next failure (retry_count + 1 = 3 >= maxRetries).
        var entry = BuildEntry("evt-decode-deadletter") with
        {
            EventPayload = "{not-json",
            RetryCount = 2,
        };
        repository.Pending.Enqueue(entry);

        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        var dispatcher = BuildDispatcher(repository, capability, publisher);

        using var failedListener = MeterSampleCollector.Subscribe("honua.outbox.failed_total");
        using var deadLetteredListener = MeterSampleCollector.Subscribe("honua.outbox.dead_lettered_total");

        await dispatcher.ExecuteOnceAsync(CancellationToken.None);

        repository.Failed.Should().ContainSingle(
            "the payload-decode path must still record a terminal failure regardless of counter routing");
        deadLetteredListener.Total.Should().Be(1,
            "payload decode at retry exhaustion must route to the dead-letter counter, matching the publish-failure path");
        failedListener.Total.Should().Be(0,
            "the failure counter should not double-count a dead-letter transition");
    }

    [UnitTest]
    public async Task ExecuteOnceAsync_RecoversExpiredClaims_OnFirstPass()
    {
        // Restart-recovery scenario: a previous worker exited mid-dispatch leaving a row in
        // 'claimed' with an expired lease. The dispatcher's recovery loop must reset it before
        // claiming so the row dispatches on the same pass.
        var (repository, capability) = BuildRepository(includePendingRow: true, eventId: "evt-recovered");
        repository.ExpiredClaimsToRecover = 3;

        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        var dispatcher = BuildDispatcher(repository, capability, publisher);

        var dispatched = await dispatcher.ExecuteOnceAsync(CancellationToken.None);

        repository.RecoverCallCount.Should().Be(1);
        dispatched.Should().Be(1);
    }

    [UnitTest]
    public async Task IOutboxHealth_TracksLastBacklogAndDispatchTimestamp()
    {
        // Health endpoint contract: after a dispatch pass, IOutboxHealth must report the most
        // recent backlog snapshot and a non-null LastDispatchAt so the readiness probe and
        // OutboxHealthCheck can reason about dispatcher liveness.
        var (repository, capability) = BuildRepository(includePendingRow: true, eventId: "evt-health");
        repository.Backlog = new OutboxBacklogMetrics
        {
            PendingCount = 5,
            DeadLetteredCount = 2,
            OldestPendingAgeSeconds = 12.5
        };

        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        var dispatcher = BuildDispatcher(repository, capability, publisher);

        await dispatcher.ExecuteOnceAsync(CancellationToken.None);

        // Cast through IOutboxHealth to mirror how the health-check probe consumes the snapshot.
        AssertHealthSnapshot(dispatcher);
    }

    [UnitTest]
    public async Task ExecuteOnceAsync_MultipleNodes_OnlyOneClaimsRow()
    {
        // Multi-node claim safety: simulate two dispatcher nodes hitting a single repository
        // that exposes one pending row. Only one node should observe a dispatch; the other
        // should see an empty claim batch on the same pass. Real Postgres semantics use
        // FOR UPDATE SKIP LOCKED; the fake repository emulates that with a single dequeue.
        var (repository, capability) = BuildRepository(includePendingRow: true, eventId: "evt-claim-once");
        var publisher = Substitute.For<IFeatureChangeEventPublisher>();

        var nodeA = BuildDispatcher(repository, capability, publisher, nodeId: "node-a");
        var nodeB = BuildDispatcher(repository, capability, publisher, nodeId: "node-b");

        var dispatchedA = await nodeA.ExecuteOnceAsync(CancellationToken.None);
        var dispatchedB = await nodeB.ExecuteOnceAsync(CancellationToken.None);

        (dispatchedA + dispatchedB).Should().Be(1, "the row should dispatch exactly once across nodes");
    }

    private static (FakeOutboxRepository repo, IOutboxCapabilityProvider capability) BuildRepository(
        bool includePendingRow,
        string eventId)
    {
        var repo = new FakeOutboxRepository();
        if (includePendingRow)
        {
            repo.Pending.Enqueue(BuildEntry(eventId));
        }
        var capability = Substitute.For<IOutboxCapabilityProvider>();
        capability.SupportsTransactionalOutbox.Returns(true);
        return (repo, capability);
    }

    private static FeatureChangeOutboxEntry BuildEntry(string eventId)
    {
        var request = new FeatureChangeEventRequest
        {
            EventId = eventId,
            ServiceId = "svc",
            LayerId = 1,
            ObjectId = 100,
            Operation = "create",
            Protocol = "Test",
            RequestId = "req",
        };
        var payload = JsonSerializer.Serialize(request, FeatureChangeEventsJsonContext.Default.FeatureChangeEventRequest);
        return new FeatureChangeOutboxEntry
        {
            OutboxId = Guid.NewGuid(),
            ServiceId = request.ServiceId,
            LayerId = request.LayerId,
            ObjectId = request.ObjectId,
            Operation = request.Operation,
            Protocol = request.Protocol,
            RequestId = request.RequestId,
            EventId = eventId,
            EventPayload = payload,
            Status = OutboxStatuses.Pending,
            RetryCount = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1859:Use concrete types when possible for improved performance",
        Justification = "Verifies the IOutboxHealth contract that the health-check probe consumes.")]
    private static void AssertHealthSnapshot(IOutboxHealth health)
    {
        health.LastBacklog.Should().NotBeNull();
        health.LastBacklog!.PendingCount.Should().Be(5);
        health.LastBacklog.DeadLetteredCount.Should().Be(2);
        health.LastDispatchAt.Should().NotBeNull();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1859:Use concrete types when possible for improved performance",
        Justification = "Verifies the IOutboxHealth contract that the health-check probe consumes.")]
    private static void AssertPendingStorageFailure(IOutboxHealth health, bool expectPending)
    {
        // The readiness probe relies on IsStoragePollFailing, which is true when ANY
        // poll kind (claim, recovery, backlog) has a failure timestamp newer than its
        // own most recent success. The aggregate timestamps cannot encode this because
        // a successful poll on one kind would mask a still-failing poll on another.
        health.IsStoragePollFailing.Should().Be(expectPending,
            expectPending
                ? "the readiness probe treats a failure newer than the latest success on any kind as a pending failure"
                : "all three poll kinds have a success at-or-after their last failure, so the probe sees no pending failure");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1859:Use concrete types when possible for improved performance",
        Justification = "Verifies the IOutboxHealth contract that the health-check probe consumes.")]
    private static void AssertStorageFailureRecorded(IOutboxHealth health, bool expectFirstSuccessfulPoll)
    {
        health.IsStoragePollFailing.Should().BeTrue(
            "the dispatcher must surface storage poll failures so the readiness probe stops reporting Healthy");
        if (expectFirstSuccessfulPoll)
        {
            health.LastSuccessfulPollAt.Should().NotBeNull(
                "a prior successful claim should leave its timestamp visible alongside the new failure marker");
        }
        else
        {
            health.LastSuccessfulPollAt.Should().BeNull(
                "no storage poll has succeeded yet on this dispatcher");
        }
    }

    private static OutboxDispatcherBackgroundService BuildDispatcher(
        IFeatureChangeOutboxRepository? repository,
        IOutboxCapabilityProvider capability,
        IFeatureChangeEventPublisher publisher,
        string? nodeId = null)
    {
        var services = new ServiceCollection();
        if (repository is not null)
        {
            services.AddSingleton(repository);
        }
        services.AddSingleton(publisher);
        var provider = services.BuildServiceProvider();

        var options = Options.Create(new OutboxDispatcherOptions
        {
            BatchSize = 32,
            ClaimTtlSeconds = 30,
            IdlePollIntervalMs = 1_000,
            RecoveryIntervalSeconds = 0,
            MaxRetries = 3,
        });
        return new OutboxDispatcherBackgroundService(
            provider,
            capability,
            options,
            NullLogger<OutboxDispatcherBackgroundService>.Instance);
    }

    /// <summary>
    /// Subscribes a <see cref="MeterListener"/> to a single named counter on the
    /// shared Honua meter and accumulates the measurements observed during the
    /// listener's lifetime. Tests use this to verify the dispatcher routes
    /// terminal outcomes (Failed, DeadLettered, StaleClaim, Errored) onto the
    /// expected counters rather than predicting from input shape.
    /// </summary>
    private sealed class MeterSampleCollector : IDisposable
    {
        private readonly MeterListener _listener;
        private long _total;

        public long Total => Interlocked.Read(ref _total);

        private MeterSampleCollector(string instrumentName)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == HonuaTelemetry.ServiceName
                        && instrument.Name == instrumentName)
                    {
                        l.EnableMeasurementEvents(instrument);
                    }
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, measurement, _, _) =>
                Interlocked.Add(ref _total, measurement));
            _listener.Start();
        }

        public static MeterSampleCollector Subscribe(string instrumentName) => new(instrumentName);

        public void Dispose() => _listener.Dispose();
    }

    private sealed class FakeOutboxRepository : IFeatureChangeOutboxRepository
    {
        public ConcurrentQueue<FeatureChangeOutboxEntry> Pending { get; } = new();
        public List<(Guid OutboxId, string OwnerNodeId)> Dispatched { get; } = new();
        public List<(Guid OutboxId, string OwnerNodeId, string Error)> Failed { get; } = new();
        public int RecoverCallCount { get; private set; }
        public int ExpiredClaimsToRecover { get; set; }
        /// <summary>
        /// The node id stamped on the most recent ClaimPendingAsync call. Used by tests to
        /// assert that the dispatcher threads the claim owner back through MarkDispatchedAsync
        /// and MarkFailedAsync, since the dispatcher's internal _nodeId derives from the host
        /// environment rather than a constructor parameter.
        /// </summary>
        public string? LastClaimNodeId { get; private set; }
        public OutboxBacklogMetrics Backlog { get; set; } = new()
        {
            PendingCount = 0,
            DeadLetteredCount = 0,
            OldestPendingAgeSeconds = 0,
        };

        /// <summary>
        /// Set of outbox ids whose claims should be considered stale (e.g. recovered or
        /// reclaimed by another node). MarkDispatchedAsync/MarkFailedAsync return false for
        /// these ids without recording the terminal update.
        /// </summary>
        public HashSet<Guid> StaleClaims { get; } = new();

        public List<Guid> DispatchedIds => Dispatched.Select(static x => x.OutboxId).ToList();
        public List<Guid> FailedIds => Failed.Select(static x => x.OutboxId).ToList();

        public Guid OnlyOutboxId => Pending.FirstOrDefault()?.OutboxId ?? Dispatched.Select(static x => x.OutboxId).Concat(Failed.Select(static x => x.OutboxId)).Single();

        public Task WriteOutboxRowAsync(DbConnection connection, DbTransaction transaction,
            FeatureChangeOutboxEntry entry, CancellationToken cancellationToken)
        {
            Pending.Enqueue(entry);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Set this to a thrown exception factory to simulate ClaimPendingAsync failure
        /// (missing table, permission issue, transient connectivity failure). When set,
        /// every call throws and no rows are dequeued.
        /// </summary>
        public Func<Exception>? ClaimFailureFactory { get; set; }

        /// <summary>
        /// Set this to simulate GetBacklogMetricsAsync failure. When set, every call throws.
        /// </summary>
        public Func<Exception>? BacklogFailureFactory { get; set; }

        /// <summary>
        /// Set this to simulate RecoverExpiredClaimsAsync failure. The dispatcher's
        /// RecoveryIntervalSeconds default in the test BuildDispatcher is 0 so recovery
        /// runs every pass; set this factory alongside ClaimFailureFactory to simulate a
        /// total storage outage (e.g. missing outbox table) where no query succeeds.
        /// </summary>
        public Func<Exception>? RecoveryFailureFactory { get; set; }

        public Task<IReadOnlyList<FeatureChangeOutboxEntry>> ClaimPendingAsync(
            string nodeId, int batchSize, TimeSpan claimTtl, CancellationToken cancellationToken)
        {
            LastClaimNodeId = nodeId;
            if (ClaimFailureFactory is not null)
            {
                throw ClaimFailureFactory();
            }
            var claimed = new List<FeatureChangeOutboxEntry>(batchSize);
            for (var i = 0; i < batchSize && Pending.TryDequeue(out var entry); i++)
            {
                claimed.Add(entry with
                {
                    Status = OutboxStatuses.Claimed,
                    ClaimNodeId = nodeId,
                    ClaimedAt = DateTimeOffset.UtcNow,
                    ClaimExpiresAt = DateTimeOffset.UtcNow.Add(claimTtl),
                });
            }
            return Task.FromResult<IReadOnlyList<FeatureChangeOutboxEntry>>(claimed);
        }

        public Task<bool> MarkDispatchedAsync(Guid outboxId, string ownerNodeId, CancellationToken cancellationToken)
        {
            if (StaleClaims.Contains(outboxId))
            {
                return Task.FromResult(false);
            }
            Dispatched.Add((outboxId, ownerNodeId));
            return Task.FromResult(true);
        }

        public Task<bool> MarkFailedAsync(Guid outboxId, string ownerNodeId, string errorMessage, int maxRetries, CancellationToken cancellationToken)
        {
            if (StaleClaims.Contains(outboxId))
            {
                return Task.FromResult(false);
            }
            Failed.Add((outboxId, ownerNodeId, errorMessage));
            return Task.FromResult(true);
        }

        public Task<int> RecoverExpiredClaimsAsync(CancellationToken cancellationToken)
        {
            RecoverCallCount++;
            if (RecoveryFailureFactory is not null)
            {
                throw RecoveryFailureFactory();
            }
            var count = ExpiredClaimsToRecover;
            ExpiredClaimsToRecover = 0;
            return Task.FromResult(count);
        }

        public Task<OutboxBacklogMetrics> GetBacklogMetricsAsync(CancellationToken cancellationToken)
        {
            if (BacklogFailureFactory is not null)
            {
                throw BacklogFailureFactory();
            }
            return Task.FromResult(Backlog);
        }
    }
}

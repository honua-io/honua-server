// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Events.Outbox;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Events.Outbox;
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
        // and marks the row dispatched. Verifies that the canonical event_payload field flows end-to-end.
        var (repository, capability) = BuildRepository(includePendingRow: true, eventId: "evt-happy");
        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        FeatureChangeEventRequest? captured = null;
        publisher
            .When(p => p.PublishAsync(Arg.Any<FeatureChangeEventRequest>(), Arg.Any<CancellationToken>()))
            .Do(call => captured = call.Arg<FeatureChangeEventRequest>());

        var dispatcher = BuildDispatcher(repository, capability, publisher);

        var dispatched = await dispatcher.ExecuteOnceAsync(CancellationToken.None);

        dispatched.Should().Be(1);
        captured.Should().NotBeNull();
        captured!.EventId.Should().Be("evt-happy");
        repository.DispatchedIds.Should().ContainSingle().Which.Should().Be(repository.OnlyOutboxId);
        repository.FailedIds.Should().BeEmpty();
    }

    [UnitTest]
    public async Task ExecuteOnceAsync_PublishFailure_MarksRowFailed_AndCountsRetry()
    {
        var (repository, capability) = BuildRepository(includePendingRow: true, eventId: "evt-fail");
        var publisher = Substitute.For<IFeatureChangeEventPublisher>();
        publisher
            .PublishAsync(Arg.Any<FeatureChangeEventRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("downstream offline")));

        var dispatcher = BuildDispatcher(repository, capability, publisher);

        var dispatched = await dispatcher.ExecuteOnceAsync(CancellationToken.None);

        dispatched.Should().Be(0, "publish failure must not count as dispatched");
        repository.FailedIds.Should().ContainSingle().Which.Should().Be(repository.OnlyOutboxId);
        repository.DispatchedIds.Should().BeEmpty();
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
        repository.FailedIds.Should().ContainSingle().Which.Should().Be(corruptEntry.OutboxId);
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

    private sealed class FakeOutboxRepository : IFeatureChangeOutboxRepository
    {
        public ConcurrentQueue<FeatureChangeOutboxEntry> Pending { get; } = new();
        public List<Guid> DispatchedIds { get; } = new();
        public List<Guid> FailedIds { get; } = new();
        public int RecoverCallCount { get; private set; }
        public int ExpiredClaimsToRecover { get; set; }
        public OutboxBacklogMetrics Backlog { get; set; } = new()
        {
            PendingCount = 0,
            DeadLetteredCount = 0,
            OldestPendingAgeSeconds = 0,
        };

        public Guid OnlyOutboxId => Pending.FirstOrDefault()?.OutboxId ?? DispatchedIds.Concat(FailedIds).Single();

        public Task WriteOutboxRowAsync(DbConnection connection, DbTransaction transaction,
            FeatureChangeOutboxEntry entry, CancellationToken cancellationToken)
        {
            Pending.Enqueue(entry);
            return Task.CompletedTask;
        }

        public Task WriteOutboxRowAsync(FeatureChangeOutboxEntry entry, CancellationToken cancellationToken)
        {
            Pending.Enqueue(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<FeatureChangeOutboxEntry>> ClaimPendingAsync(
            string nodeId, int batchSize, TimeSpan claimTtl, CancellationToken cancellationToken)
        {
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

        public Task MarkDispatchedAsync(Guid outboxId, CancellationToken cancellationToken)
        {
            DispatchedIds.Add(outboxId);
            return Task.CompletedTask;
        }

        public Task MarkFailedAsync(Guid outboxId, string errorMessage, int maxRetries, CancellationToken cancellationToken)
        {
            FailedIds.Add(outboxId);
            return Task.CompletedTask;
        }

        public Task<int> RecoverExpiredClaimsAsync(CancellationToken cancellationToken)
        {
            RecoverCallCount++;
            var count = ExpiredClaimsToRecover;
            ExpiredClaimsToRecover = 0;
            return Task.FromResult(count);
        }

        public Task<OutboxBacklogMetrics> GetBacklogMetricsAsync(CancellationToken cancellationToken)
            => Task.FromResult(Backlog);
    }
}

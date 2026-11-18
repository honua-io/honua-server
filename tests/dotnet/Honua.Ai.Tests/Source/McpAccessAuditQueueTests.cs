// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

// MCP access records go through a bounded queue drained by one background writer, so a call
// never waits on the audit chain lock; a full queue drops and counts instead of blocking.
[Protocol(TestProtocols.Mcp)]
public sealed class McpAccessAuditQueueTests
{
    [UnitTest]
    public async Task QueuedRecords_AreWrittenInOrderedBatches_AndDrainedOnStop()
    {
        var auditLog = new BatchRecordingAuditLog();
        using var queue = Queue(auditLog, capacity: 1000);
        var events = Events(600);

        foreach (var auditEvent in events)
        {
            queue.Enqueue(auditEvent);
        }

        await queue.StartAsync(CancellationToken.None);
        await queue.StopAsync(CancellationToken.None);

        auditLog.Batches.SelectMany(batch => batch).Should().Equal(events);
        auditLog.Batches.Should().OnlyContain(batch => batch.Count <= McpAccessAuditQueue.MaxBatchSize);
        queue.Enqueue(Events(1)[0]);
        queue.DroppedCount.Should().Be(0, "a record after stop is not an overload drop");
    }

    [UnitTest]
    public async Task FullQueue_DropsAndCountsRecords_WithoutWaiting()
    {
        var auditLog = new BatchRecordingAuditLog();
        using var queue = Queue(auditLog, capacity: 2);
        var events = Events(5);

        // The writer is not started, so the queue cannot drain while records arrive.
        foreach (var auditEvent in events)
        {
            queue.Enqueue(auditEvent);
        }

        queue.DroppedCount.Should().Be(3);
        await queue.StartAsync(CancellationToken.None);
        await queue.StopAsync(CancellationToken.None);
        auditLog.Batches.SelectMany(batch => batch).Should().Equal(events.Take(2));
    }

    [UnitTest]
    public async Task FailedBatch_IsLogged_AndLaterRecordsStillWritten()
    {
        var auditLog = new BatchRecordingAuditLog { FailNextBatch = true };
        using var queue = Queue(auditLog, capacity: 10);
        await queue.StartAsync(CancellationToken.None);

        queue.Enqueue(Events(1)[0]);
        (await auditLog.FirstAttempt.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        var later = Events(2);
        queue.Enqueue(later[0]);
        queue.Enqueue(later[1]);
        await queue.StopAsync(CancellationToken.None);

        auditLog.Batches.SelectMany(batch => batch).Should().Equal(later);
    }

    [UnitTest]
    public async Task RecordAsync_WithQueueRegistered_EnqueuesInsteadOfWritingInline()
    {
        var auditLog = new BatchRecordingAuditLog();
        using var queue = Queue(auditLog, capacity: 10);
        var context = McpTestFactory.AuthenticatedHttpContextWithServices(services => services
            .AddSingleton<IAuditLog>(auditLog)
            .AddSingleton(queue));

        var access = new McpAccess
        {
            Action = McpAccessAudit.ToolCallAction,
            ResourceId = "honua_list_layers",
            Details = "{}",
        };

        await McpAccessAudit.RecordAsync(context, access, NullLogger.Instance);

        auditLog.Attempts.Should().Be(0);
        await queue.StartAsync(CancellationToken.None);
        await queue.StopAsync(CancellationToken.None);
        auditLog.Batches.Should().ContainSingle().Which.Single().ResourceId.Should().Be("honua_list_layers");
    }

    private static McpAccessAuditQueue Queue(IAuditLog auditLog, int capacity) =>
        new(
            new ServiceCollection().AddSingleton(auditLog).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<McpAccessAuditQueue>.Instance,
            capacity);

    private static AuditEvent[] Events(int count)
    {
        var batchId = Guid.NewGuid().ToString("N");
        return [.. Enumerable.Range(0, count).Select(i => new AuditEvent
        {
            Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(i),
            EventType = AuditEventType.DataExport,
            Actor = "agent-a",
            ActorType = AuditActorType.ApiKey,
            ResourceType = McpAccessAudit.ResourceType,
            ResourceId = $"resource-{i}",
            Action = McpAccessAudit.ResourceReadAction,
            Outcome = AuditOutcome.Success,
            CorrelationId = $"{batchId}-{i}",
        })];
    }

    private sealed class BatchRecordingAuditLog : IAuditLog
    {
        private int _attempts;

        public ConcurrentQueue<IReadOnlyList<AuditEvent>> Batches { get; } = new();

        public bool FailNextBatch { get; set; }

        public int Attempts => Volatile.Read(ref _attempts);

        public TaskCompletionSource<bool> FirstAttempt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string?> RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the queue must write through RecordBatchAsync");

        public Task RecordBatchAsync(IReadOnlyList<AuditEvent> auditEvents, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _attempts);
            FirstAttempt.TrySetResult(true);
            if (FailNextBatch)
            {
                FailNextBatch = false;
                throw new InvalidOperationException("audit store unavailable");
            }

            Batches.Enqueue([.. auditEvents]);
            return Task.CompletedTask;
        }
    }
}

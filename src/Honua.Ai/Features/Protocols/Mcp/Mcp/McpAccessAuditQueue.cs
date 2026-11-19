// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Threading.Channels;
using Honua.Core.Features.AuditLog.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Ai.Protocols.Mcp;

// MCP reads are frequent and the audit sink serializes every insert on one chain lock, so a
// request hands its access record to this bounded queue and returns without waiting. One
// background writer drains the queue in batches (one chain lock per batch) and writes what is
// still queued when the host stops. A record that finds the queue full is dropped, counted and
// logged; the call it describes never waits for room.
internal sealed class McpAccessAuditQueue : BackgroundService
{
    internal const int DefaultCapacity = 4096;
    internal const int MaxBatchSize = 256;

    private readonly Channel<AuditEvent> _channel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<McpAccessAuditQueue> _logger;
    private readonly int _capacity;
    private long _dropped;
    private volatile bool _stopping;

    public McpAccessAuditQueue(IServiceScopeFactory scopeFactory, ILogger<McpAccessAuditQueue> logger)
        : this(scopeFactory, logger, DefaultCapacity)
    {
    }

    internal McpAccessAuditQueue(IServiceScopeFactory scopeFactory, ILogger<McpAccessAuditQueue> logger, int capacity)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _scopeFactory = scopeFactory;
        _logger = logger;
        _capacity = capacity;
        _channel = Channel.CreateBounded<AuditEvent>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public long DroppedCount => Interlocked.Read(ref _dropped);

    public void Enqueue(AuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        if (_channel.Writer.TryWrite(auditEvent))
        {
            return;
        }

        if (_stopping)
        {
            // The host is stopping and the writer is closed; this is not an overloaded queue.
            McpLog.AccessAuditAfterStop(_logger);
            return;
        }

        var dropped = Interlocked.Increment(ref _dropped);
        McpTelemetry.AccessAuditDroppedCount.Add(1);

        // The first drop and every doubling after it, so sustained overload stays visible
        // without one log line per dropped record.
        if ((dropped & (dropped - 1)) == 0)
        {
            McpLog.AccessAuditDropped(_logger, dropped, _capacity);
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        // Completing the writer ends the drain loop once the queue is empty, so records
        // accepted before shutdown are still written.
        _stopping = true;
        _channel.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    // Runs until StopAsync completes the writer, not until the stopping token fires, so the
    // queue is drained rather than abandoned; the host's shutdown timeout still bounds the wait.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = _channel.Reader;
        var batch = new List<AuditEvent>(MaxBatchSize);
        while (await reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            while (batch.Count < MaxBatchSize && reader.TryRead(out var auditEvent))
            {
                batch.Add(auditEvent);
            }

            await WriteAsync(batch.ToArray()).ConfigureAwait(false);
            batch.Clear();
        }
    }

    private async Task WriteAsync(AuditEvent[] batch)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<IAuditLog>() is { } auditLog)
            {
                await auditLog.RecordBatchAsync(batch, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            McpLog.AccessAuditBatchFailed(_logger, batch.Length, ex);
        }
    }
}

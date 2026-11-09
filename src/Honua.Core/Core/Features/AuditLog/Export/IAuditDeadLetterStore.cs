// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;

namespace Honua.Core.Features.AuditLog.Export;

// A failed forward must not drop events; the full batch is kept so an operator can replay it.
public interface IAuditDeadLetterStore
{
    Task StoreAsync(IReadOnlyList<AuditEvent> events, string sinkType, string error, CancellationToken ct);

    Task<IReadOnlyList<DeadLetteredAuditBatch>> ListAsync(CancellationToken ct);
}

public sealed record DeadLetteredAuditBatch
{
    public required IReadOnlyList<AuditEvent> Events { get; init; }

    public required string SinkType { get; init; }

    public required string Error { get; init; }

    public required DateTimeOffset FailedAt { get; init; }
}

public sealed class InMemoryAuditDeadLetterStore : IAuditDeadLetterStore
{
    private readonly object _gate = new();
    private readonly List<DeadLetteredAuditBatch> _batches = [];

    public Task StoreAsync(IReadOnlyList<AuditEvent> events, string sinkType, string error, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(sinkType);

        var batch = new DeadLetteredAuditBatch
        {
            Events = events.ToArray(),
            SinkType = sinkType,
            Error = error ?? string.Empty,
            FailedAt = DateTimeOffset.UtcNow,
        };

        lock (_gate)
        {
            _batches.Add(batch);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DeadLetteredAuditBatch>> ListAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<DeadLetteredAuditBatch> snapshot = _batches.ToArray();
            return Task.FromResult(snapshot);
        }
    }
}

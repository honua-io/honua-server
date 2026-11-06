// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;

namespace Honua.Core.Features.AuditLog.Export;

public interface IAuditRetentionPruner
{
    Task<int> PruneAsync(AuditRetentionPolicy policy, CancellationToken ct);
}

internal sealed class InMemoryAuditRetentionPruner : IAuditRetentionPruner
{
    private readonly List<AuditEvent> _events;
    private readonly Func<DateTimeOffset> _now;

    public InMemoryAuditRetentionPruner(IEnumerable<AuditEvent> events, Func<DateTimeOffset>? nowProvider = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        _events = [.. events];
        _now = nowProvider ?? (static () => DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<AuditEvent> Remaining => _events;

    public Task<int> PruneAsync(AuditRetentionPolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (!policy.IsBounded)
        {
            return Task.FromResult(0);
        }

        var now = _now();
        var removed = _events.RemoveAll(e => policy.IsExpired(e.Timestamp, now));
        return Task.FromResult(removed);
    }
}

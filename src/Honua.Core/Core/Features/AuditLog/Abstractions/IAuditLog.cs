// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.AuditLog.Abstractions;

// The sink is append-only, and a caller that needs durable evidence must treat a missing assigned identity as a failure.
public interface IAuditLog
{
    bool IsPersisted => true;

    Task<string?> RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);

    // Records the events in order. A sink that chains entries writes the whole batch under one
    // chain lock; the default writes them one at a time.
    async Task RecordBatchAsync(IReadOnlyList<AuditEvent> auditEvents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvents);
        foreach (var auditEvent in auditEvents)
        {
            await RecordAsync(auditEvent, cancellationToken).ConfigureAwait(false);
        }
    }
}

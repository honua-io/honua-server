// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;

namespace Honua.Core.Features.Operations.Services;

// Production composition must use the durable audit log.
public sealed class VolatileOperationAuditLog : IAuditLog
{
    public bool IsPersisted => false;

    public Task<string?> RecordAsync(
        AuditEvent auditEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>($"audit-dev-{Guid.NewGuid():N}");
    }
}

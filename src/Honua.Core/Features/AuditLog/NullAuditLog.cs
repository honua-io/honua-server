// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;

namespace Honua.Core.Features.AuditLog;

public sealed class NullAuditLog : IAuditLog
{
    public static readonly NullAuditLog Instance = new();

    public bool IsPersisted => false;

    public Task<string?> RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        return Task.FromResult<string?>(null);
    }
}

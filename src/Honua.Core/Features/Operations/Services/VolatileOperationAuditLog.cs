// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;

namespace Honua.Core.Features.Operations.Services;

/// <summary>
/// Non-persistent audit identity provider for explicit development and test composition.
/// Production composition must use the registered durable <see cref="IAuditLog"/>.
/// </summary>
public sealed class VolatileOperationAuditLog : IAuditLog
{
    /// <inheritdoc />
    public bool IsPersisted => false;

    /// <inheritdoc />
    public Task<string?> RecordAsync(
        AuditEvent auditEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>($"audit-dev-{Guid.NewGuid():N}");
    }
}

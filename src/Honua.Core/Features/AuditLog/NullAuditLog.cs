// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;

namespace Honua.Core.Features.AuditLog;

/// <summary>
/// No-op <see cref="IAuditLog"/> used when no durable sink is configured.
/// Records nothing and returns immediately.
/// </summary>
/// <remarks>
/// This is intentionally <c>sealed</c> and stateless so DI can hand out a
/// singleton without per-call allocation. Use this as a fallback in tests and
/// in environments that explicitly opt out of audit persistence; the
/// <see cref="IAuditLog"/> contract guarantees the call is best-effort.
/// </remarks>
public sealed class NullAuditLog : IAuditLog
{
    /// <summary>
    /// A shared, allocation-free instance.
    /// </summary>
    public static readonly NullAuditLog Instance = new();

    /// <inheritdoc />
    public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        return Task.CompletedTask;
    }
}

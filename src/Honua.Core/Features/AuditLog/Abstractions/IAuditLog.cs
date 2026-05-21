// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.AuditLog.Abstractions;

/// <summary>
/// Durable, append-only sink for security-relevant audit events.
/// </summary>
/// <remarks>
/// <para>
/// Implementations MUST treat the sink as append-only: there is no API for
/// update or delete. Implementations SHOULD swallow transient failures and log
/// them via <c>ILogger</c> rather than propagating to the caller, because an
/// audit failure must never block a security-relevant action — but they MUST
/// surface configuration errors (missing table, schema mismatch) so the
/// platform fails closed at startup.
/// </para>
/// <para>
/// A <see cref="NullAuditLog"/> fallback is provided for environments where no
/// durable sink is configured (unit tests, local dev). It records nothing.
/// </para>
/// </remarks>
public interface IAuditLog
{
    /// <summary>
    /// Record an audit event. Implementations should be best-effort and
    /// non-blocking with respect to the caller's critical path.
    /// </summary>
    /// <param name="auditEvent">The event to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);
}

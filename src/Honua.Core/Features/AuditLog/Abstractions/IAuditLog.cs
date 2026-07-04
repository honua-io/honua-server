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
    /// Whether this implementation durably persists recorded events. <c>false</c>
    /// for fallback/no-op sinks (e.g. <see cref="NullAuditLog"/>) so callers that
    /// need to know whether audit events are actually being retained (for example
    /// a compliance dependency gate) can ask the abstraction directly instead of
    /// inspecting the concrete implementation type. Defaults to <c>true</c> so
    /// existing durable implementations (and test doubles) do not need to opt in.
    /// </summary>
    bool IsPersisted => true;

    /// <summary>
    /// Record an audit event. Implementations should be best-effort and
    /// non-blocking with respect to the caller's critical path.
    /// </summary>
    /// <param name="auditEvent">The event to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);
}

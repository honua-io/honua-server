// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.AuditLog.Abstractions;

namespace Honua.Core.Features.Alerts.Abstractions;

/// <summary>
/// Commits an operator lifecycle transition and its successful domain audit atomically.
/// </summary>
public interface IAlertLifecycleMutationStore
{
    /// <summary>
    /// Applies an acknowledge, suppress, or resolve operation. The actor, correlation ID,
    /// action and event identify a retry; a retry with different details is rejected.
    /// No lifecycle state may become visible unless its audit is durably committed.
    /// </summary>
    /// <param name="eventId">Alert event identifier.</param>
    /// <param name="note">Operator note.</param>
    /// <param name="suppressUntil">Suppression expiry, for suppress operations only.</param>
    /// <param name="auditEvent">Successful domain audit with the operation timestamp and identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The committed transition, or null if the event does not exist.</returns>
    Task<AlertEventLifecycle?> MutateAsync(long eventId, string? note,
        DateTimeOffset? suppressUntil, AuditEvent auditEvent, CancellationToken cancellationToken = default);
}

/// <summary>Signals reuse of an alert operation identity with different request details.</summary>
public sealed class AlertLifecycleRetryConflictException : Exception
{
    /// <summary>Creates a conflicting-retry error.</summary>
    public AlertLifecycleRetryConflictException() : base("The alert operation identity was already used with different details.") { }
}

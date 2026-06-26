// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;

namespace Honua.Core.Features.AuditLog.Export;

/// <summary>
/// Declarative retention policy for the audit trail (#2157): audit records older
/// than <see cref="RetentionWindow"/> are eligible for pruning.
/// </summary>
/// <remarks>
/// A <see cref="RetentionWindow"/> of <see cref="TimeSpan.Zero"/> or less means
/// "retain forever" — nothing is ever considered expired. The policy is pure
/// (no I/O); an <see cref="IAuditRetentionPruner"/> applies it against a store.
/// </remarks>
public sealed record AuditRetentionPolicy
{
    /// <summary>
    /// How long an audit record is retained. Values &lt;= <see cref="TimeSpan.Zero"/>
    /// disable expiry (retain forever).
    /// </summary>
    public required TimeSpan RetentionWindow { get; init; }

    /// <summary>
    /// Whether expiry is enabled (a positive retention window is configured).
    /// </summary>
    public bool IsBounded => RetentionWindow > TimeSpan.Zero;

    /// <summary>
    /// The cutoff instant: records with a <see cref="AuditEvent.Timestamp"/>
    /// strictly before this instant are expired.
    /// </summary>
    /// <param name="now">The current instant (typically <see cref="DateTimeOffset.UtcNow"/>).</param>
    /// <returns>
    /// The UTC cutoff, or <see cref="DateTimeOffset.MinValue"/> when retention is
    /// unbounded (so nothing is ever before the cutoff).
    /// </returns>
    public DateTimeOffset CutoffUtc(DateTimeOffset now)
        => IsBounded ? now.ToUniversalTime() - RetentionWindow : DateTimeOffset.MinValue;

    /// <summary>
    /// Whether an event with the given timestamp has aged past the retention window.
    /// </summary>
    /// <param name="eventTimestamp">The event's timestamp.</param>
    /// <param name="now">The current instant.</param>
    /// <returns><c>true</c> when bounded and the event predates the cutoff; otherwise <c>false</c>.</returns>
    public bool IsExpired(DateTimeOffset eventTimestamp, DateTimeOffset now)
        => IsBounded && eventTimestamp.ToUniversalTime() < CutoffUtc(now);
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;

namespace Honua.Core.Features.AuditLog.Export;

/// <summary>
/// Applies an <see cref="AuditRetentionPolicy"/> against the audit store,
/// removing records that have aged past the retention window (#2157).
/// </summary>
/// <remarks>
/// <para>
/// The audit trail is append-only and tamper-evident (a Postgres
/// <c>DO INSTEAD NOTHING</c> rule blocks ordinary deletes). The production
/// pruner is therefore a <em>privileged</em> maintenance path that bypasses the
/// append-only rule under controlled conditions, and is deferred to the
/// Postgres provider wiring. This Core seam exists so the retention policy and
/// cutoff arithmetic can be unit-tested independently of a database.
/// </para>
/// </remarks>
public interface IAuditRetentionPruner
{
    /// <summary>
    /// Removes audit records older than the policy cutoff.
    /// </summary>
    /// <param name="policy">The retention policy to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of records pruned.</returns>
    Task<int> PruneAsync(AuditRetentionPolicy policy, CancellationToken ct);
}

/// <summary>
/// In-memory <see cref="IAuditRetentionPruner"/> over a mutable event list, used
/// to unit-test the retention/cutoff logic without a database. Retained events
/// keep their original relative order.
/// </summary>
internal sealed class InMemoryAuditRetentionPruner : IAuditRetentionPruner
{
    private readonly List<AuditEvent> _events;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>
    /// Initializes a new pruner over a copy of the given events.
    /// </summary>
    /// <param name="events">Seed events.</param>
    /// <param name="nowProvider">Clock; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public InMemoryAuditRetentionPruner(IEnumerable<AuditEvent> events, Func<DateTimeOffset>? nowProvider = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        _events = [.. events];
        _now = nowProvider ?? (static () => DateTimeOffset.UtcNow);
    }

    /// <summary>The events currently retained, in original relative order.</summary>
    public IReadOnlyList<AuditEvent> Remaining => _events;

    /// <inheritdoc />
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

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;

namespace Honua.Core.Features.AuditLog.Export;

/// <summary>
/// Durable holding area for audit batches that could not be delivered to a sink
/// after exhausting retries, or that failed permanently (#2157).
/// </summary>
/// <remarks>
/// The audit trail is tamper-evident and append-only, so a failed forward must
/// never silently drop events: the dispatcher preserves the <em>full</em> batch
/// here so an operator can replay it once the sink recovers, keeping the SIEM
/// feed complete and the hash chain accountable.
/// </remarks>
public interface IAuditDeadLetterStore
{
    /// <summary>
    /// Stores a failed batch, preserving every event in the batch.
    /// </summary>
    /// <param name="events">The undelivered events (the complete batch).</param>
    /// <param name="sinkType">The <see cref="IAuditSink.SinkType"/> that failed.</param>
    /// <param name="error">A short, non-sensitive failure description.</param>
    /// <param name="ct">Cancellation token.</param>
    Task StoreAsync(IReadOnlyList<AuditEvent> events, string sinkType, string error, CancellationToken ct);

    /// <summary>
    /// Lists the currently dead-lettered batches, oldest-first.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The dead-lettered batches.</returns>
    Task<IReadOnlyList<DeadLetteredAuditBatch>> ListAsync(CancellationToken ct);
}

/// <summary>
/// A batch of audit events that failed delivery to a sink.
/// </summary>
public sealed record DeadLetteredAuditBatch
{
    /// <summary>The complete set of undelivered events.</summary>
    public required IReadOnlyList<AuditEvent> Events { get; init; }

    /// <summary>The <see cref="IAuditSink.SinkType"/> that failed delivery.</summary>
    public required string SinkType { get; init; }

    /// <summary>A short, non-sensitive failure description.</summary>
    public required string Error { get; init; }

    /// <summary>When the batch was dead-lettered (UTC).</summary>
    public required DateTimeOffset FailedAt { get; init; }
}

/// <summary>
/// Thread-safe in-memory <see cref="IAuditDeadLetterStore"/> used as the default
/// implementation and for unit tests.
/// </summary>
/// <remarks>
/// In-memory state is lost on restart; production deployments should register a
/// durable implementation (database/object-store backed) ahead of calling the
/// export registration so undelivered audit batches survive a process bounce.
/// Public (not internal) so the server DI layer, which does not have access to
/// this assembly's internals, can register it as the default.
/// </remarks>
public sealed class InMemoryAuditDeadLetterStore : IAuditDeadLetterStore
{
    private readonly object _gate = new();
    private readonly List<DeadLetteredAuditBatch> _batches = [];

    /// <inheritdoc />
    public Task StoreAsync(IReadOnlyList<AuditEvent> events, string sinkType, string error, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(sinkType);

        var batch = new DeadLetteredAuditBatch
        {
            Events = events.ToArray(),
            SinkType = sinkType,
            Error = error ?? string.Empty,
            FailedAt = DateTimeOffset.UtcNow,
        };

        lock (_gate)
        {
            _batches.Add(batch);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DeadLetteredAuditBatch>> ListAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<DeadLetteredAuditBatch> snapshot = _batches.ToArray();
            return Task.FromResult(snapshot);
        }
    }
}

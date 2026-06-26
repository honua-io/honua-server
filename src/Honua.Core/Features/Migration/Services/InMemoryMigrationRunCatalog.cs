// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// In-memory <see cref="IMigrationRunCatalog"/> used by tests and as the deterministic
/// fallback when no Postgres-backed catalog has been registered (#1598). The implementation
/// is thread-safe for single-process use and honors the idempotency/sticky-terminal
/// semantics documented on the abstraction.
/// </summary>
public sealed class InMemoryMigrationRunCatalog : IMigrationRunCatalog
{
    private const int MaxPageLimit = 100;
    private const int DefaultPageLimit = 25;

    private readonly object _gate = new();
    private readonly Dictionary<string, StoredRun> _runs = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<MigrationRunRecord> RecordStartedAsync(
        MigrationRunRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.SourceKind);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_runs.TryGetValue(record.RunId, out var existing))
            {
                return Task.FromResult(existing.Record);
            }

            _runs[record.RunId] = new StoredRun(record, null, null);
            return Task.FromResult(record);
        }
    }

    /// <inheritdoc />
    public Task<MigrationRunRecord?> RecordCompletedAsync(
        string runId,
        MigrationRunStatus status,
        DateTimeOffset completedAt,
        string? evidencePackRef,
        string? evidencePackFingerprint,
        string? evidencePackBody,
        string? statusNote,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var existing))
            {
                return Task.FromResult<MigrationRunRecord?>(null);
            }

            if (existing.Record.Status != MigrationRunStatus.Running)
            {
                // Terminal state is sticky: a late completion notification must not
                // overwrite an earlier cancel/failure.
                return Task.FromResult<MigrationRunRecord?>(existing.Record);
            }

            var updated = existing.Record with
            {
                Status = status,
                CompletedAt = completedAt,
                EvidencePackRef = evidencePackRef ?? existing.Record.EvidencePackRef,
                EvidencePackFingerprint = evidencePackFingerprint ?? existing.Record.EvidencePackFingerprint,
                StatusNote = statusNote ?? existing.Record.StatusNote
            };
            _runs[runId] = existing with
            {
                Record = updated,
                EvidencePackBody = evidencePackBody ?? existing.EvidencePackBody
            };
            return Task.FromResult<MigrationRunRecord?>(updated);
        }
    }

    /// <inheritdoc />
    public Task<MigrationRunRecord?> CancelAsync(
        string runId,
        DateTimeOffset cancelledAt,
        string? statusNote,
        CancellationToken cancellationToken = default)
        => RecordCompletedAsync(
            runId,
            MigrationRunStatus.Cancelled,
            cancelledAt,
            evidencePackRef: null,
            evidencePackFingerprint: null,
            evidencePackBody: null,
            statusNote,
            cancellationToken);

    /// <inheritdoc />
    public Task<MigrationRunRecord?> GetAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult(_runs.TryGetValue(runId, out var run) ? run.Record : null);
        }
    }

    /// <inheritdoc />
    public Task<MigrationRunListPage> ListAsync(
        MigrationRunListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var limit = Math.Clamp(query.Limit <= 0 ? DefaultPageLimit : query.Limit, 1, MaxPageLimit);
        var offset = Math.Max(0, query.Offset);

        MigrationRunRecord[] all;
        lock (_gate)
        {
            all = _runs.Values.Select(static r => r.Record).ToArray();
        }

        var filtered = all
            .Where(r => string.IsNullOrWhiteSpace(query.SourceKind)
                || string.Equals(r.SourceKind, query.SourceKind, StringComparison.OrdinalIgnoreCase))
            .Where(r => query.Status is null || r.Status == query.Status)
            .OrderByDescending(static r => r.StartedAt)
            .ThenBy(static r => r.RunId, StringComparer.Ordinal)
            .ToArray();

        return Task.FromResult(new MigrationRunListPage
        {
            Items = filtered.Skip(offset).Take(limit).ToArray(),
            TotalCount = filtered.Length,
            Limit = limit,
            Offset = offset
        });
    }

    /// <inheritdoc />
    public Task<string?> GetEvidencePackAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult(_runs.TryGetValue(runId, out var run) ? run.EvidencePackBody : null);
        }
    }

    /// <inheritdoc />
    public Task<MigrationRunRecord?> RecordScorecardAsync(
        string runId,
        string scorecardFingerprint,
        string scorecardBody,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scorecardFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(scorecardBody);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var existing))
            {
                return Task.FromResult<MigrationRunRecord?>(null);
            }

            var updated = existing.Record with
            {
                ReconciliationScorecardFingerprint = scorecardFingerprint
            };
            _runs[runId] = existing with { Record = updated, ScorecardBody = scorecardBody };
            return Task.FromResult<MigrationRunRecord?>(updated);
        }
    }

    /// <inheritdoc />
    public Task<string?> GetScorecardAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult(_runs.TryGetValue(runId, out var run) ? run.ScorecardBody : null);
        }
    }

    private sealed record StoredRun(
        MigrationRunRecord Record,
        string? EvidencePackBody,
        string? ScorecardBody);
}

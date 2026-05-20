// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Services;

/// <summary>
/// In-memory implementation of <see cref="IMigrationPerformanceEvidenceStore"/> used by
/// tests, the dev profile, and any deployment that has not opted into a durable store
/// (issue #1033 slice 5).
/// </summary>
/// <remarks>
/// State is process-local: records do not survive a restart. The implementation is
/// safe to use concurrently because mutations take a private lock and reads project
/// into a snapshot list.
/// </remarks>
public sealed class InMemoryMigrationPerformanceEvidenceStore : IMigrationPerformanceEvidenceStore
{
    private const int MaxLimit = 200;
    private static readonly StringComparer _ordinal = StringComparer.Ordinal;
    private static readonly StringComparer _ordinalIgnoreCase = StringComparer.OrdinalIgnoreCase;

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, MigrationPerformanceEvidenceRecord> _byId = new(_ordinal);

    /// <inheritdoc />
    public ValueTask SaveAsync(MigrationPerformanceEvidenceRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.EvidenceId);

        lock (_gate)
        {
            _byId[record.EvidenceId] = record;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<MigrationPerformanceEvidenceRecord?> GetLatestPassingAsync(CancellationToken cancellationToken = default)
    {
        MigrationPerformanceEvidenceRecord? result;
        lock (_gate)
        {
            result = _byId.Values
                .Where(r => string.Equals(r.Status, "Pass", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.GeneratedAt)
                .ThenBy(r => r.EvidenceId, _ordinal)
                .FirstOrDefault();
        }

        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public ValueTask<MigrationPerformanceEvidenceRecord?> GetByIdAsync(
        string evidenceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceId);

        _byId.TryGetValue(evidenceId, out var record);
        return ValueTask.FromResult(record);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<MigrationPerformanceEvidenceRecord>> GetHistoryAsync(
        string? sourceFamily,
        string? fixtureSize,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var clamped = limit <= 0 ? 25 : Math.Min(limit, MaxLimit);

        List<MigrationPerformanceEvidenceRecord> snapshot;
        lock (_gate)
        {
            IEnumerable<MigrationPerformanceEvidenceRecord> query = _byId.Values;

            if (!string.IsNullOrWhiteSpace(sourceFamily))
            {
                query = query.Where(r => _ordinalIgnoreCase.Equals(r.SourceFamily, sourceFamily));
            }

            if (!string.IsNullOrWhiteSpace(fixtureSize))
            {
                query = query.Where(r => _ordinalIgnoreCase.Equals(r.FixtureSize, fixtureSize));
            }

            snapshot = query
                .OrderByDescending(r => r.GeneratedAt)
                .ThenBy(r => r.EvidenceId, _ordinal)
                .Take(clamped)
                .ToList();
        }

        return ValueTask.FromResult<IReadOnlyList<MigrationPerformanceEvidenceRecord>>(snapshot);
    }
}

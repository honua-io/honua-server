// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Services;

public sealed class InMemoryMigrationPerformanceEvidenceStore : IMigrationPerformanceEvidenceStore
{
    private const int MaxLimit = 200;
    private static readonly StringComparer _ordinal = StringComparer.Ordinal;
    private static readonly StringComparer _ordinalIgnoreCase = StringComparer.OrdinalIgnoreCase;

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, MigrationPerformanceEvidenceRecord> _byId = new(_ordinal);

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

    public ValueTask<MigrationPerformanceEvidenceRecord?> GetByIdAsync(
        string evidenceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceId);

        _byId.TryGetValue(evidenceId, out var record);
        return ValueTask.FromResult(record);
    }

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

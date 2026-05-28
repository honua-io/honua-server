// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.FileImport.Services.FileGdb;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// In-memory <see cref="IArcGisMigrationEvidenceStore"/> used by tests and as the deterministic
/// fallback when no Postgres-backed store has been registered. The implementation is thread-safe
/// for single-process use.
/// </summary>
public sealed class InMemoryArcGisMigrationEvidenceStore : IArcGisMigrationEvidenceStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, StoredRun> _runs = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task SaveManifestAsync(
        ArcGisMigrationRunRecord record,
        MigrationManifestArtifact manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.RunId);
        ArgumentNullException.ThrowIfNull(manifest);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_runs.TryGetValue(record.RunId, out var existing))
            {
                _runs[record.RunId] = existing with { Record = record, Manifest = manifest };
            }
            else
            {
                _runs[record.RunId] = new StoredRun(record, manifest, null);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SaveParityAsync(
        string runId,
        ArcGisMigrationParityArtifact parity,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(parity);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var existing))
            {
                throw new InvalidOperationException(
                    $"Cannot persist parity for run '{runId}' because no manifest record exists yet.");
            }

            _runs[runId] = existing with { Parity = parity };
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ArcGisMigrationRunListResult> ListAsync(
        ArcGisMigrationRunFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        cancellationToken.ThrowIfCancellationRequested();

        var page = Math.Max(0, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize <= 0 ? 25 : filter.PageSize, 1, 100);

        StoredRun[] all;
        lock (_gate)
        {
            all = _runs.Values.ToArray();
        }

        var filtered = all
            .Where(r => MatchesSourceUrl(r.Record.SourceUrl, filter.SourceUrl))
            .Where(r => MatchesStatus(StatusOf(r), filter.Status))
            .OrderByDescending(r => r.Record.CreatedAt)
            .ThenBy(r => r.Record.RunId, StringComparer.Ordinal)
            .ToArray();

        var items = filtered
            .Skip(page * pageSize)
            .Take(pageSize)
            .Select(ToSummary)
            .ToArray();

        return Task.FromResult(new ArcGisMigrationRunListResult
        {
            Items = items,
            TotalCount = filtered.Length,
            Page = page,
            PageSize = pageSize
        });
    }

    /// <inheritdoc />
    public Task<MigrationManifestArtifact?> GetManifestAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult(_runs.TryGetValue(runId, out var run) ? run.Manifest : null);
        }
    }

    /// <inheritdoc />
    public Task<ArcGisMigrationParityArtifact?> GetParityAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult(_runs.TryGetValue(runId, out var run) ? run.Parity : null);
        }
    }

    private static ArcGisMigrationRunSummary ToSummary(StoredRun run)
    {
        return new ArcGisMigrationRunSummary
        {
            RunId = run.Record.RunId,
            SourceUrl = run.Record.SourceUrl,
            SourceDisplayName = run.Record.SourceDisplayName,
            SourceVersion = run.Record.SourceVersion,
            CreatedAt = run.Record.CreatedAt,
            Actor = run.Record.Actor,
            Status = StatusOf(run),
            HasParity = run.Parity is not null,
            TargetResourceCount = run.Manifest?.TargetResources.Length ?? 0,
        };
    }

    private static string StatusOf(StoredRun run)
    {
        if (run.Parity is null)
        {
            return ArcGisMigrationRunStatuses.ManifestOnly;
        }

        return run.Parity.Classification switch
        {
            ArcGisMigrationParityClassifications.Pass => ArcGisMigrationRunStatuses.Pass,
            ArcGisMigrationParityClassifications.Warn => ArcGisMigrationRunStatuses.Warn,
            ArcGisMigrationParityClassifications.Fail => ArcGisMigrationRunStatuses.Fail,
            _ => ArcGisMigrationRunStatuses.ManifestOnly,
        };
    }

    private static bool MatchesSourceUrl(string sourceUrl, string? filter)
        => string.IsNullOrWhiteSpace(filter)
            || sourceUrl.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesStatus(string status, string? filter)
        => string.IsNullOrWhiteSpace(filter)
            || string.Equals(status, filter, StringComparison.OrdinalIgnoreCase);

    private sealed record StoredRun(
        ArcGisMigrationRunRecord Record,
        MigrationManifestArtifact Manifest,
        ArcGisMigrationParityArtifact? Parity);
}

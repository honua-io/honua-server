// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Share.Abstractions;
using Honua.Core.Features.Share.Domain;

namespace Honua.Server.Features.Admin.Share;

internal sealed class UnsupportedShareExportDestinationResolver : IShareExportDestinationResolver
{
    public ShareExportDestinationStatus Resolve(ShareExportDestinationType destinationType)
        => ShareExportDestinationStatus.Unsupported;
}

internal sealed class InMemoryShareExportStore : IShareExportStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ShareExportDefinition> _definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ShareExportRun>> _runsByExport = new(StringComparer.Ordinal);

    public Task<ShareExportDefinitionPage> ListDefinitionsAsync(
        ShareExportDefinitionQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var limit = Math.Clamp(query.Limit, 1, ShareAdminEndpoints.MaxPageSize);
        lock (_gate)
        {
            var items = _definitions.Values
                .Where(definition => string.IsNullOrWhiteSpace(query.ServiceName)
                    || string.Equals(definition.ServiceName, query.ServiceName, StringComparison.OrdinalIgnoreCase))
                .Where(definition => string.IsNullOrWhiteSpace(query.ResourceId)
                    || string.Equals(definition.ResourceId, query.ResourceId, StringComparison.Ordinal))
                .Where(definition => !query.LayerId.HasValue || definition.LayerId == query.LayerId.Value)
                .Where(definition => !query.DestinationType.HasValue || definition.DestinationType == query.DestinationType.Value)
                .Where(definition => !query.ScheduleState.HasValue || definition.ScheduleState == query.ScheduleState.Value);

            if (ShareCursor.TryDecode(query.Cursor, out var cursorTimestamp, out var cursorId))
            {
                items = items.Where(definition =>
                    definition.UpdatedAt < cursorTimestamp
                    || (definition.UpdatedAt == cursorTimestamp
                        && string.CompareOrdinal(definition.ExportId, cursorId) < 0));
            }

            var page = items
                .OrderByDescending(definition => definition.UpdatedAt)
                .ThenByDescending(definition => definition.ExportId, StringComparer.Ordinal)
                .Take(limit + 1)
                .ToArray();

            var visible = page.Take(limit).ToArray();
            var nextCursor = page.Length > limit
                ? ShareCursor.Encode(visible[^1].UpdatedAt, visible[^1].ExportId)
                : null;

            return Task.FromResult(new ShareExportDefinitionPage
            {
                Items = visible,
                NextCursor = nextCursor
            });
        }
    }

    public Task<ShareExportDefinition?> GetDefinitionAsync(
        string exportId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _definitions.TryGetValue(exportId, out var definition);
            return Task.FromResult(definition);
        }
    }

    public Task<ShareExportDefinition> CreateDefinitionAsync(
        ShareExportDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_definitions.ContainsKey(definition.ExportId))
            {
                throw new ShareExportStoreConflictException("A Share export definition with this identifier already exists.");
            }

            _definitions.Add(definition.ExportId, definition);
            _runsByExport.TryAdd(definition.ExportId, []);
            return Task.FromResult(definition);
        }
    }

    public Task<ShareExportDefinition?> UpdateDefinitionAsync(
        ShareExportDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_definitions.ContainsKey(definition.ExportId))
            {
                return Task.FromResult<ShareExportDefinition?>(null);
            }

            _definitions[definition.ExportId] = definition;
            return Task.FromResult<ShareExportDefinition?>(definition);
        }
    }

    public Task<bool> DeleteDefinitionAsync(
        string exportId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var removed = _definitions.Remove(exportId);
            _runsByExport.Remove(exportId);
            return Task.FromResult(removed);
        }
    }

    public Task<ShareExportRun> AppendRunAsync(
        ShareExportRun run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_definitions.TryGetValue(run.ExportId, out var definition))
            {
                throw new KeyNotFoundException("Share export definition was not found.");
            }

            var runs = _runsByExport.GetValueOrDefault(run.ExportId);
            if (runs is null)
            {
                runs = [];
                _runsByExport[run.ExportId] = runs;
            }

            if (runs.Any(candidate => string.Equals(candidate.RunId, run.RunId, StringComparison.Ordinal)))
            {
                throw new ShareExportStoreConflictException("A Share export run with this identifier already exists.");
            }

            runs.Add(run);
            var lastRunAt = definition.LastRunAt.HasValue && definition.LastRunAt.Value > run.TriggeredAt
                ? definition.LastRunAt
                : run.TriggeredAt;
            _definitions[run.ExportId] = definition with
            {
                LastRunAt = lastRunAt,
                UpdatedAt = definition.UpdatedAt > run.TriggeredAt
                    ? definition.UpdatedAt
                    : run.TriggeredAt
            };

            return Task.FromResult(run);
        }
    }

    public Task<ShareExportRunPage> ListRunsAsync(
        string exportId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var effectiveLimit = Math.Clamp(limit, 1, ShareAdminEndpoints.MaxPageSize);
        lock (_gate)
        {
            var items = _runsByExport.TryGetValue(exportId, out var runs)
                ? runs.AsEnumerable()
                : Enumerable.Empty<ShareExportRun>();

            if (ShareCursor.TryDecode(cursor, out var cursorTimestamp, out var cursorId))
            {
                items = items.Where(run =>
                    run.TriggeredAt < cursorTimestamp
                    || (run.TriggeredAt == cursorTimestamp
                        && string.CompareOrdinal(run.RunId, cursorId) < 0));
            }

            var page = items
                .OrderByDescending(run => run.TriggeredAt)
                .ThenByDescending(run => run.RunId, StringComparer.Ordinal)
                .Take(effectiveLimit + 1)
                .ToArray();

            var visible = page.Take(effectiveLimit).ToArray();
            var nextCursor = page.Length > effectiveLimit
                ? ShareCursor.Encode(visible[^1].TriggeredAt, visible[^1].RunId)
                : null;

            return Task.FromResult(new ShareExportRunPage
            {
                Items = visible,
                NextCursor = nextCursor
            });
        }
    }

    public Task<ShareExportRun?> GetRunAsync(
        string exportId,
        string runId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var run = _runsByExport.TryGetValue(exportId, out var runs)
                ? runs.FirstOrDefault(candidate => string.Equals(candidate.RunId, runId, StringComparison.Ordinal))
                : null;
            return Task.FromResult(run);
        }
    }
}

internal sealed class InMemoryShareTrafficStore : IShareTrafficStore
{
    private readonly Lock _gate = new();
    private readonly List<SeededShareTrafficBucket> _buckets = [];

    public void AddBucket(ShareItemRef? itemRef, DateTimeOffset bucketStart, ShareTrafficCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);

        lock (_gate)
        {
            _buckets.Add(new SeededShareTrafficBucket(itemRef, bucketStart, counts));
        }
    }

    public Task<ShareTrafficSummary> GetSummaryAsync(
        ShareTrafficQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var counts = MatchingBuckets(query)
                .Aggregate(new ShareTrafficCounts(), Add);
            return Task.FromResult(new ShareTrafficSummary
            {
                ItemRef = query.ItemRef,
                PeriodStart = query.PeriodStart,
                PeriodEnd = query.PeriodEnd,
                ByInteractionType = counts,
                TotalRequests = Total(counts)
            });
        }
    }

    public Task<ShareTrafficSeries> GetSeriesAsync(
        ShareTrafficQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var seeded = MatchingBuckets(query)
                .GroupBy(bucket => AlignBucket(bucket.BucketStart, query.PeriodStart, query.BucketDuration))
                .ToDictionary(
                    group => group.Key,
                    group => group.Aggregate(new ShareTrafficCounts(), (acc, bucket) => Add(acc, bucket.Counts)));

            var buckets = new List<ShareTrafficBucket>();
            for (var cursor = query.PeriodStart; cursor < query.PeriodEnd; cursor = cursor.Add(query.BucketDuration))
            {
                var counts = seeded.GetValueOrDefault(cursor) ?? new ShareTrafficCounts();
                buckets.Add(new ShareTrafficBucket
                {
                    BucketStart = cursor,
                    ByInteractionType = counts,
                    Total = Total(counts)
                });
            }

            return Task.FromResult(new ShareTrafficSeries
            {
                ItemRef = query.ItemRef,
                PeriodStart = query.PeriodStart,
                PeriodEnd = query.PeriodEnd,
                BucketDuration = query.BucketDuration,
                Buckets = buckets
            });
        }
    }

    private IEnumerable<SeededShareTrafficBucket> MatchingBuckets(ShareTrafficQuery query)
        => _buckets.Where(bucket =>
            bucket.BucketStart >= query.PeriodStart
            && bucket.BucketStart < query.PeriodEnd
            && ItemMatches(query.ItemRef, bucket.ItemRef));

    private static bool ItemMatches(ShareItemRef? requested, ShareItemRef? actual)
    {
        if (requested is null)
        {
            return true;
        }

        return actual is not null
            && string.Equals(requested.ResourceId, actual.ResourceId, StringComparison.Ordinal)
            && string.Equals(requested.ServiceName, actual.ServiceName, StringComparison.OrdinalIgnoreCase)
            && requested.LayerId == actual.LayerId;
    }

    private static DateTimeOffset AlignBucket(DateTimeOffset bucketStart, DateTimeOffset periodStart, TimeSpan duration)
    {
        if (bucketStart <= periodStart)
        {
            return periodStart;
        }

        var offsetTicks = bucketStart.Ticks - periodStart.Ticks;
        var bucketOffset = offsetTicks / duration.Ticks;
        return periodStart.AddTicks(bucketOffset * duration.Ticks);
    }

    private static ShareTrafficCounts Add(ShareTrafficCounts left, SeededShareTrafficBucket right)
        => Add(left, right.Counts);

    private static ShareTrafficCounts Add(ShareTrafficCounts left, ShareTrafficCounts right)
        => new()
        {
            Public = left.Public + right.Public,
            PublicLink = left.PublicLink + right.PublicLink,
            Embed = left.Embed + right.Embed,
            OpenData = left.OpenData + right.OpenData,
            Dcat = left.Dcat + right.Dcat,
            Stac = left.Stac + right.Stac,
            Export = left.Export + right.Export
        };

    private static long Total(ShareTrafficCounts counts)
        => counts.Public
            + counts.PublicLink
            + counts.Embed
            + counts.OpenData
            + counts.Dcat
            + counts.Stac
            + counts.Export;

    private sealed record SeededShareTrafficBucket(
        ShareItemRef? ItemRef,
        DateTimeOffset BucketStart,
        ShareTrafficCounts Counts);
}

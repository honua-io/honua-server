// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.EmbedGovernance.Abstractions;
using Honua.Core.Features.EmbedGovernance.Domain;

namespace Honua.Core.Features.EmbedGovernance;

/// <summary>
/// In-memory <see cref="IEmbedAnalyticsStore"/>. Retains a bounded ring of recent
/// events for operator reporting; a durable provider can replace it later.
/// </summary>
public sealed class InMemoryEmbedAnalyticsStore : IEmbedAnalyticsStore
{
    private const string Unknown = "(unknown)";
    private readonly int _capacity;
    private readonly ConcurrentQueue<EmbedAnalyticsEvent> _events = new();

    /// <summary>
    /// Creates the store.
    /// </summary>
    /// <param name="capacity">Maximum retained events before oldest are dropped.</param>
    public InMemoryEmbedAnalyticsStore(int capacity = 50_000)
    {
        _capacity = capacity > 0 ? capacity : 50_000;
    }

    /// <inheritdoc />
    public Task IngestAsync(EmbedAnalyticsEvent analyticsEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(analyticsEvent);

        _events.Enqueue(analyticsEvent);
        while (_events.Count > _capacity && _events.TryDequeue(out _))
        {
            // Drop oldest to keep the buffer bounded.
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<EmbedUsageReport> QueryAsync(EmbedUsageQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(query);

        var matching = _events
            .Where(e => Matches(e, query))
            .ToList();

        var aggregates = matching
            .GroupBy(e => DimensionKey(e, query.GroupBy))
            .Select(g => new EmbedUsageAggregate { Key = g.Key, Count = g.LongCount() })
            .OrderByDescending(a => a.Count)
            .ThenBy(a => a.Key, StringComparer.Ordinal)
            .ToList()
            .AsReadOnly();

        var report = new EmbedUsageReport
        {
            GroupBy = query.GroupBy,
            Total = matching.Count,
            Aggregates = aggregates,
        };

        return Task.FromResult(report);
    }

    private static bool Matches(EmbedAnalyticsEvent e, EmbedUsageQuery query)
    {
        if (query.EventType.HasValue && e.EventType != query.EventType.Value)
        {
            return false;
        }

        if (!FilterMatches(query.IntegrationId, e.IntegrationId)
            || !FilterMatches(query.TenantId, e.TenantId)
            || !FilterMatches(query.Origin, e.Origin)
            || !FilterMatches(query.ServiceId, e.ServiceId)
            || !FilterMatches(query.LayerId, e.LayerId))
        {
            return false;
        }

        if (query.From.HasValue && e.OccurredAt < query.From.Value)
        {
            return false;
        }

        if (query.To.HasValue && e.OccurredAt >= query.To.Value)
        {
            return false;
        }

        return true;
    }

    private static bool FilterMatches(string? filter, string? value)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        return string.Equals(filter, value, StringComparison.OrdinalIgnoreCase);
    }

    private static string DimensionKey(EmbedAnalyticsEvent e, EmbedUsageDimension dimension) => dimension switch
    {
        EmbedUsageDimension.Integration => e.IntegrationId ?? Unknown,
        EmbedUsageDimension.Tenant => e.TenantId ?? Unknown,
        EmbedUsageDimension.Origin => e.Origin ?? Unknown,
        EmbedUsageDimension.Service => e.ServiceId ?? Unknown,
        EmbedUsageDimension.Layer => e.LayerId ?? Unknown,
        EmbedUsageDimension.EventType => e.EventType.ToString(),
        _ => e.EventType.ToString(),
    };
}

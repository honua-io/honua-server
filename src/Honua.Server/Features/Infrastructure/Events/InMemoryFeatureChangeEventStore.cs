// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Events;

/// <summary>
/// In-memory feature-change event store with bounded retention.
/// </summary>
internal sealed class InMemoryFeatureChangeEventStore(
    IOptions<FeatureChangeEventOptions> options) : IFeatureChangeEventStore
{
    private readonly object _sync = new();
    private readonly List<FeatureChangeEvent> _events = [];
    private readonly int _maxRetained = Math.Max(100, options.Value.MaxRetainedEvents);
    private long _nextCursor = 1;

    public Task<FeatureChangeEvent> AppendAsync(
        FeatureChangeEventRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedOperation = NormalizeOperation(request.Operation);
        var normalizedProtocol = string.IsNullOrWhiteSpace(request.Protocol)
            ? "unknown"
            : request.Protocol.Trim();
        var normalizedServiceId = string.IsNullOrWhiteSpace(request.ServiceId)
            ? "unknown"
            : request.ServiceId.Trim();
        var normalizedRequestId = string.IsNullOrWhiteSpace(request.RequestId)
            ? "unknown"
            : request.RequestId.Trim();

        FeatureChangeEvent created;
        lock (_sync)
        {
            created = new FeatureChangeEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                Cursor = _nextCursor++,
                Timestamp = request.Timestamp ?? DateTimeOffset.UtcNow,
                ServiceId = normalizedServiceId,
                LayerId = request.LayerId,
                ObjectId = request.ObjectId,
                Operation = normalizedOperation,
                Protocol = normalizedProtocol,
                RequestId = normalizedRequestId
            };

            _events.Add(created);
            TrimIfNeeded();
        }

        return Task.FromResult(created);
    }

    public Task<IReadOnlyList<FeatureChangeEvent>> QueryAsync(
        long? cursor,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var effectiveLimit = Math.Clamp(limit, 1, 5_000);

        List<FeatureChangeEvent> snapshot;
        lock (_sync)
        {
            snapshot = _events.ToList();
        }

        var filtered = snapshot
            .Where(e => !cursor.HasValue || e.Cursor > cursor.Value)
            .Where(e => !from.HasValue || e.Timestamp >= from.Value)
            .Where(e => !to.HasValue || e.Timestamp <= to.Value)
            .OrderBy(e => e.Cursor)
            .Take(effectiveLimit)
            .ToArray();

        return Task.FromResult<IReadOnlyList<FeatureChangeEvent>>(filtered);
    }

    private void TrimIfNeeded()
    {
        if (_events.Count <= _maxRetained)
        {
            return;
        }

        var removeCount = _events.Count - _maxRetained;
        _events.RemoveRange(0, removeCount);
    }

    private static string NormalizeOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation))
        {
            return "update";
        }

        var normalized = operation.Trim().ToLowerInvariant();
        return normalized switch
        {
            "create" or "update" or "delete" => normalized,
            _ => "update"
        };
    }
}


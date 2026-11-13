// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Events;

namespace Honua.Server.Tests.Features.Streaming;

// An in-process change log for unit tests. Cursors increase like change-log generations, with an
// optional gap before each append; trimming past MaxRetained moves the resumable window forward.
internal sealed class FakeFeatureChangeEventStore(int maxRetained = int.MaxValue)
    : IFeatureChangeEventStore, IFeatureChangeEventStoreHealth
{
    private readonly Lock _gate = new();
    private readonly List<FeatureChangeEvent> _events = [];
    private FeatureChangeRetentionWindow? _fixedWindow;
    private long _head;
    private long _lastTrimmedCursor;

    public Action? QueryCompleted { get; set; }

    public void SetRetentionWindow(FeatureChangeRetentionWindow window)
    {
        lock (_gate)
        {
            _fixedWindow = window;
            _head = window.CurrentCursor;
        }
    }

    public bool CanPersistEvents => true;

    public bool IsUsingInMemoryFallback => false;

    public static FakeFeatureChangeEventStore WithRetentionWindow(FeatureChangeRetentionWindow window)
        => new() { _fixedWindow = window, _head = window.CurrentCursor };

    public FeatureChangeEvent Append(FeatureChangeEventRequest request, long cursorGap = 1)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(cursorGap, 1);
        lock (_gate)
        {
            _head += cursorGap;
            var appended = ToEvent(request, _head);
            _events.Add(appended);
            Trim();
            return appended;
        }
    }

    public Task<long> GetCurrentCursorAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_head);
        }
    }

    public Task<long> GetOldestRetainedCursorAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_events.Count == 0 && _lastTrimmedCursor == 0 ? 0L : _lastTrimmedCursor + 1);
        }
    }

    public async Task<FeatureChangeRetentionWindow> GetRetentionWindowAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_fixedWindow is { } window)
            {
                return window;
            }
        }

        IFeatureChangeEventStore store = this;
        var current = await store.GetCurrentCursorAsync(cancellationToken).ConfigureAwait(false);
        var oldest = await store.GetOldestRetainedCursorAsync(cancellationToken).ConfigureAwait(false);
        return oldest == 0
            ? FeatureChangeRetentionWindow.KnownEmpty(current)
            : FeatureChangeRetentionWindow.Retained(current, oldest);
    }

    public Task<IReadOnlyList<FeatureChangeEvent>> QueryAsync(
        long? cursor,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<FeatureChangeEvent> matches = _events
                .Where(e => e.Cursor > (cursor ?? 0))
                .Where(e => (from is null || e.Timestamp >= from) && (to is null || e.Timestamp <= to))
                .Take(limit)
                .ToArray();
            QueryCompleted?.Invoke();
            return Task.FromResult(matches);
        }
    }

    private void Trim()
    {
        while (_events.Count > maxRetained)
        {
            _lastTrimmedCursor = _events[0].Cursor;
            _events.RemoveAt(0);
        }
    }

    private static FeatureChangeEvent ToEvent(FeatureChangeEventRequest request, long cursor)
        => new()
        {
            EventId = string.IsNullOrWhiteSpace(request.EventId) ? Guid.NewGuid().ToString("N") : request.EventId,
            Cursor = cursor,
            Timestamp = request.Timestamp ?? DateTimeOffset.UtcNow,
            SourceId = request.SourceId ?? request.ServiceId,
            ServiceId = request.ServiceId,
            LayerId = request.LayerId,
            ObjectId = request.ObjectId,
            Operation = string.Equals(request.Operation, "create", StringComparison.OrdinalIgnoreCase) ? "insert" : request.Operation,
            Protocol = request.Protocol,
            RequestId = request.RequestId,
            OperationInstanceId = request.OperationInstanceId,
            CorrelationId = request.CorrelationId,
            AuditId = request.AuditId,
            ProposalId = request.ProposalId,
            ChangedAttributes = request.ChangedAttributes,
            GeometryChanged = request.GeometryChanged,
            GeometryEnvelope = request.GeometryEnvelope,
            PropertiesJson = request.PropertiesJson,
            GeometryJson = request.GeometryJson,
            GeometrySrid = request.GeometrySrid
        };
}

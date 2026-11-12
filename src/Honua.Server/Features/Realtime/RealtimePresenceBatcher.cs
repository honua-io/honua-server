// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Realtime;

namespace Honua.Server.Features.Realtime;

// Best-effort events (presence) kept latest-wins per family, scope and coalesce key, and appended
// in one statement per family per flush (specification section 5.4).
internal sealed class RealtimePresenceBatcher(PostgresRealtimeEventLog log)
{
    // A host that never flushes (no dispatch loop) must not grow without bound.
    internal const int MaxQueued = 10_000;

    private readonly Lock _gate = new();
    private Dictionary<(string Family, string Scope, string Key), RealtimeEventDraft> _queued = [];

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _queued.Count;
            }
        }
    }

    public void Enqueue(RealtimeEventDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var key = (draft.Family.Name, draft.ScopeKey, draft.CoalesceKey ?? draft.Kind);
        lock (_gate)
        {
            if (_queued.Count < MaxQueued || _queued.ContainsKey(key))
            {
                _queued[key] = draft;
            }
        }
    }

    // Appends everything queued; returns the number of events appended.
    public async Task<int> FlushAsync(CancellationToken cancellationToken)
    {
        Dictionary<(string Family, string Scope, string Key), RealtimeEventDraft> batch;
        lock (_gate)
        {
            if (_queued.Count == 0)
            {
                return 0;
            }

            batch = _queued;
            _queued = [];
        }

        var appended = 0;
        foreach (var family in batch.Values.GroupBy(draft => draft.Family.Name, StringComparer.Ordinal))
        {
            var rows = family.Select(draft => new RealtimeLogRow(draft.ScopeKey, draft.Kind, draft.Payload)).ToArray();
            await log.AppendAsync(family.Key, rows, null, cancellationToken).ConfigureAwait(false);
            appended += rows.Length;
        }

        return appended;
    }
}

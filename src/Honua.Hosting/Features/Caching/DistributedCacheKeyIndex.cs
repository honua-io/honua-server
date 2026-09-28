// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace Honua.Infrastructure.Caching;

/// <summary>
/// Tracks generic distributed-cache membership without extending expired payloads'
/// index lifetime. IDistributedCache cannot enumerate scopes or expose remaining TTLs.
/// </summary>
internal sealed partial class DistributedCacheKeyIndex : IAsyncDisposable
{
    internal static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LegacyIndexLifetime = TimeSpan.FromDays(30);
    private const int MaxVisitedIndexes = 1024;
    private const int IndexesPerTick = 4;
    private const int LegacyProbesPerIndex = 32;
    private readonly IDistributedCache _cache;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    // Backend-scoped transactions must outlive individual service owners. Weak keys
    // allow an unused backend and its managed semaphore to be collected together.
    private static readonly ConditionalWeakTable<IDistributedCache, SemaphoreSlim> BackendGates = new();
    private readonly SemaphoreSlim _gate;
    private readonly bool _processLocal;
    private readonly LinkedList<IndexState> _visited = new();
    private readonly Dictionary<string, LinkedListNode<IndexState>> _nodes = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _maintenance;
    private readonly object _lifetime = new();
    private int _operations;
    private bool _disposing;
    private Task? _disposeTask;
    private TaskCompletionSource? _drained;

    public DistributedCacheKeyIndex(IDistributedCache cache, ILogger logger, TimeProvider clock, bool processLocal)
    {
        _cache = cache;
        _logger = logger;
        _clock = clock;
        _processLocal = processLocal;
        _gate = BackendGates.GetValue(cache, static _ => new SemaphoreSlim(1, 1));
        if (!processLocal)
        {
            // Arbitrary distributed providers expose no CAS or distributed lock.
            // Preserve their foreground-only, best-effort legacy index semantics.
            _maintenance = Task.CompletedTask;
            return;
        }
        if (ExecutionContext.IsFlowSuppressed())
        {
            _maintenance = MaintainAsync();
        }
        else
        {
            using (ExecutionContext.SuppressFlow())
            {
                _maintenance = MaintainAsync();
            }
        }
    }

    public async Task TrackAsync(string indexKey, string key, TimeSpan ttl, CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadAsync(indexKey, cancellationToken).ConfigureAwait(false);
            var now = _clock.GetUtcNow().UtcTicks;
            var deadline = now + Math.Min(ttl.Ticks, DateTimeOffset.MaxValue.UtcTicks - now);
            // Payload writes precede index tracking. A delayed older Track must not
            // shorten a newer renewal's deadline; conservative retention is safe.
            if (entries.TryGetValue(key, out var previous) && previous is long previousDeadline)
            {
                deadline = Math.Max(deadline, previousDeadline);
            }
            entries[key] = _processLocal ? deadline : null;
            await PruneAsync(entries, Visit(indexKey), 4, cancellationToken).ConfigureAwait(false);
            await SaveAsync(indexKey, entries, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Exit();
        }
    }

    public async Task<IReadOnlyList<string>> ReadAsync(string indexKey, CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken, acquireGate: _processLocal).ConfigureAwait(false);
        try
        {
            var entries = await LoadAsync(indexKey, cancellationToken).ConfigureAwait(false);
            if (!_processLocal)
            {
                return entries.Keys.ToArray();
            }
            if (await PruneAsync(entries, Visit(indexKey), 4, cancellationToken).ConfigureAwait(false))
            {
                await SaveAsync(indexKey, entries, cancellationToken).ConfigureAwait(false);
            }
            if (entries.Count == 0)
            {
                Forget(indexKey);
            }
            return entries.Keys.ToArray();
        }
        finally
        {
            Exit(releaseGate: _processLocal);
        }
    }

    public async Task RemoveAsync(string indexKey, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadAsync(indexKey, cancellationToken).ConfigureAwait(false);
            foreach (var key in keys)
            {
                entries.Remove(key);
            }
            await PruneAsync(entries, Visit(indexKey), 4, cancellationToken).ConfigureAwait(false);
            await SaveAsync(indexKey, entries, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Exit();
        }
    }

    private async Task<Dictionary<string, long?>> LoadAsync(string indexKey, CancellationToken cancellationToken)
    {
        var data = await _cache.GetAsync(indexKey, cancellationToken).ConfigureAwait(false);
        var document = data is null ? null : JsonSerializer.Deserialize(data, CacheJsonContext.Default.CachedCacheKeyIndex);
        var entries = new Dictionary<string, long?>(StringComparer.Ordinal);
        if (document?.Keys is { } keys)
        {
            foreach (var key in keys)
            {
                entries[key] = document.ExpiresAtUtcTicks?.TryGetValue(key, out var deadline) == true ? deadline : null;
            }
        }
        return entries;
    }

    private async Task<bool> PruneAsync(
        Dictionary<string, long?> entries, IndexState state, int legacyBudget, CancellationToken cancellationToken)
    {
        if (!_processLocal)
        {
            return false;
        }
        var now = _clock.GetUtcNow().UtcTicks;
        var changed = false;
        List<string>? legacy = null;
        foreach (var (key, deadline) in entries)
        {
            if (deadline is null)
            {
                (legacy ??= []).Add(key);
            }
            else if (deadline <= now)
            {
                // Remove membership only. A concurrent value write will add its new
                // membership under this gate before that SetAsync completes.
                changed |= entries.Remove(key);
            }
        }
        if (legacy is not null)
        {
            var probes = Math.Min(legacyBudget, legacy.Count);
            for (var i = 0; i < probes; i++)
            {
                var key = legacy[(state.LegacyCursor + i) % legacy.Count];
                if (await _cache.GetAsync(key, cancellationToken).ConfigureAwait(false) is null)
                {
                    changed |= entries.Remove(key);
                }
            }
            state.LegacyCursor = (state.LegacyCursor + probes) % legacy.Count;
        }
        return changed;
    }

    private async Task SaveAsync(string indexKey, Dictionary<string, long?> entries, CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            await _cache.RemoveAsync(indexKey, cancellationToken).ConfigureAwait(false);
            Forget(indexKey);
            return;
        }
        var now = _clock.GetUtcNow();
        var expiry = now.UtcTicks + 1;
        var deadlines = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (key, deadline) in entries)
        {
            if (_processLocal && deadline is long known)
            {
                deadlines.Add(key, known);
                expiry = Math.Max(expiry, known);
            }
            else
            {
                // Older documents contain only Keys. Keep their previous TTL policy
                // until bounded existence checks can prove a legacy value expired.
                expiry = Math.Max(expiry, now.Add(LegacyIndexLifetime).UtcTicks);
            }
        }
        var data = JsonSerializer.SerializeToUtf8Bytes(
            new CachedCacheKeyIndex(entries.Keys.ToArray(), deadlines), CacheJsonContext.Default.CachedCacheKeyIndex);
        await _cache.SetAsync(indexKey, data,
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromTicks(
                    Math.Max(TimeSpan.TicksPerMillisecond, expiry - _clock.GetUtcNow().UtcTicks))
            },
            cancellationToken).ConfigureAwait(false);
    }

    private IndexState Visit(string indexKey)
    {
        if (_nodes.TryGetValue(indexKey, out var existing))
        {
            return existing.Value;
        }
        if (_visited.Count == MaxVisitedIndexes)
        {
            Forget(_visited.First!.Value.Key);
        }
        var state = new IndexState(indexKey);
        _nodes.Add(indexKey, _visited.AddLast(state));
        return state;
    }

    private void Forget(string indexKey)
    {
        if (_nodes.Remove(indexKey, out var node))
        {
            _visited.Remove(node);
        }
    }

    private async Task MaintainAsync()
    {
        using var timer = new PeriodicTimer(MaintenanceInterval, _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
            {
                try
                {
                    await _gate.WaitAsync(_stopping.Token).ConfigureAwait(false);
                    try
                    {
                        var count = Math.Min(IndexesPerTick, _visited.Count);
                        for (var i = 0; i < count; i++)
                        {
                            var node = _visited.First!;
                            _visited.RemoveFirst();
                            _visited.AddLast(node);
                            var entries = await LoadAsync(node.Value.Key, _stopping.Token).ConfigureAwait(false);
                            if (await PruneAsync(entries, node.Value, LegacyProbesPerIndex, _stopping.Token).ConfigureAwait(false))
                            {
                                await SaveAsync(node.Value.Key, entries, _stopping.Token).ConfigureAwait(false);
                            }
                            if (entries.Count == 0)
                            {
                                Forget(node.Value.Key);
                            }
                        }
                    }
                    finally
                    {
                        _gate.Release();
                    }
                }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    Log.MaintenanceFailed(_logger, exception);
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Normal owner shutdown.
        }
    }

    private async Task EnterAsync(CancellationToken cancellationToken, bool acquireGate = true)
    {
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(_disposing, this);
            _operations++;
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (acquireGate)
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            EndOperation();
            throw;
        }
    }

    private void Exit(bool releaseGate = true)
    {
        if (releaseGate)
        {
            _gate.Release();
        }
        EndOperation();
    }

    private void EndOperation()
    {
        lock (_lifetime)
        {
            if (--_operations == 0 && _disposing)
            {
                _drained!.TrySetResult();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifetime)
        {
            if (_disposeTask is null)
            {
                _disposing = true;
                _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_operations == 0)
                {
                    _drained.SetResult();
                }
                _disposeTask = DisposeCoreAsync(_drained.Task);
            }
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(Task foregroundDrained)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _maintenance.ConfigureAwait(false);
        // Reservations include gate waiters. Prevent new admission, then let every
        // already-admitted caller release ownership before disposing shared state.
        await foregroundDrained.ConfigureAwait(false);
        _stopping.Dispose();
        _visited.Clear();
        _nodes.Clear();
        // Other owners may still be using the backend gate. It has no native wait
        // handle and is reclaimed with the backend, not with this service owner.
    }

    private sealed class IndexState(string key)
    {
        public string Key { get; } = key;
        public int LegacyCursor { get; set; }
    }

    private static partial class Log
    {
        [LoggerMessage(1, LogLevel.Warning, "Distributed cache index maintenance failed; retrying on the next tick")]
        public static partial void MaintenanceFailed(ILogger logger, Exception exception);
    }
}

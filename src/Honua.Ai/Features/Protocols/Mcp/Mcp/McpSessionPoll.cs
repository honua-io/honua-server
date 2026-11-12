// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;

namespace Honua.Ai.Protocols.Mcp;

// One poll a second, while this node runs requests or holds streams, finds the sessions removed
// or expired elsewhere and the requests cancelled elsewhere, and ends their local work.
internal sealed partial class McpSessionManager : IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan PollBudget = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<McpRequestLease, byte> _runningRequests = new();
    private readonly Dictionary<string, DateTimeOffset> _removedSessions = new(StringComparer.Ordinal);
    private readonly object _pollGate = new();
    private readonly CancellationTokenSource _pollLifetime = new();
    private Task _pollTask = Task.CompletedTask;
    private bool _polling;
    private bool _stopping;
    private int _disposed;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _pollLifetime.Dispose();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task polling;
        lock (_capacityGate)
        {
            lock (_pollGate)
            {
                _stopping = true;
                polling = _pollTask;
            }
        }

        await _pollLifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await polling.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            StopAll(_runningRequests.Keys);
            EndSessions(_sessions.Keys.ToArray());
        }
    }

    internal async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var leases = _runningRequests.Keys.ToArray();
        var request = new McpPollRequest(WatchedSessionIds(), leases.Select(static lease => lease.Request).ToArray());
        if (request.SessionIds.Count == 0 && request.Requests.Count == 0)
        {
            return;
        }

        McpPollResult result;
        try
        {
            result = await PollWithinBudgetAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A store that cannot answer must end running work, never silently disable cancellation.
            LogStoreFailure(ex, "request-observe");
            StopAll(leases);
            return;
        }

        EndSessions(result.EndedSessions);
        var ended = result.EndedRequests.ToHashSet();
        StopAll(leases.Where(lease => ended.Contains(lease.Request)));
    }

    private async Task<McpPollResult> PollWithinBudgetAsync(McpPollRequest request, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(PollBudget);
        var polling = PollStoreAsync(request, budget.Token);
        try
        {
            return await polling.WaitAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Budget expiry also needs a join: cancellation cleanup may outlive the timeout.
            await polling.ConfigureAwait(false);
            throw;
        }
    }

    private Task<McpPollResult> PollStoreAsync(McpPollRequest request, CancellationToken cancellationToken)
        => _store is not null ? _store.PollAsync(request, cancellationToken) : PollLocalRequestsAsync(request, cancellationToken);

    private async Task<McpPollResult> PollLocalRequestsAsync(McpPollRequest request, CancellationToken cancellationToken)
        => new([], await McpRequestPoll.ReadEachAsync(RequestStore, request.Requests, cancellationToken).ConfigureAwait(false));

    // Only a shared store can remove a session behind this node's back.
    private string[] WatchedSessionIds()
        => _store is null
            ? []
            : _sessions.Where(static pair => pair.Value.Watched).Select(static pair => pair.Key).ToArray();

    private void EndSessions(IEnumerable<string> sessionIds)
    {
        foreach (var sessionId in sessionIds)
        {
            // Remembered even when not adopted yet: a read already in flight could otherwise install it.
            RememberRemoval(sessionId);
            try
            {
                Terminate(sessionId);
            }
            catch (AggregateException ex)
            {
                LogStoreFailure(ex, "session-end-callback");
            }
        }
    }

    private static void StopAll(IEnumerable<McpRequestLease> leases)
    {
        foreach (var lease in leases)
        {
            lease.Stop();
        }
    }

    private void WatchSession(McpSession session)
    {
        if (_store is null || session.Watched)
        {
            return;
        }

        session.Watched = true;
        EnsurePolling();
    }

    private void WatchRequest(McpRequestLease lease)
    {
        lock (_pollGate)
        {
            if (!_stopping)
            {
                _runningRequests.TryAdd(lease, 0);
                EnsurePolling();
                return;
            }
        }

        lease.Stop();
    }

    private void UnwatchRequest(McpRequestLease lease) => _runningRequests.TryRemove(lease, out _);

    private void EnsurePolling()
    {
        lock (_pollGate)
        {
            if (_polling || _stopping)
            {
                return;
            }

            _polling = true;
            _pollTask = Task.Run(PollWhileWatchedAsync);
        }
    }

    private async Task PollWhileWatchedAsync()
    {
        while (KeepPolling())
        {
            try
            {
                await Task.Delay(McpRequestBounds.PollInterval, _timeProvider, _pollLifetime.Token).ConfigureAwait(false);
                await PollOnceAsync(_pollLifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_pollLifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogStoreFailure(ex, "poll");
            }
        }
    }

    // Decided under the gate so a watch added while the loop stops starts a new loop.
    private bool KeepPolling()
    {
        lock (_pollGate)
        {
            _polling = !_stopping && (!_runningRequests.IsEmpty || (_store is not null && _sessions.Values.Any(static session => session.Watched)));
            return _polling;
        }
    }

    private void RememberRemoval(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        lock (_capacityGate)
        {
            _removedSessions[sessionId] = now;
            if (_removedSessions.Count > _maxSessions)
            {
                ForgetRemovalsBefore(now - _idleTimeout);
            }
        }
    }

    // Caller holds _capacityGate.
    private void ForgetRemovalsBefore(DateTimeOffset cutoff)
    {
        foreach (var stale in _removedSessions.Where(pair => pair.Value < cutoff).Select(pair => pair.Key).ToArray())
        {
            _removedSessions.Remove(stale);
        }
    }

    // Caller holds _capacityGate.
    private bool WasRemoved(string sessionId, DateTimeOffset now)
    {
        if (!_removedSessions.TryGetValue(sessionId, out var removedAt))
        {
            return false;
        }

        if (removedAt < now - _idleTimeout)
        {
            _removedSessions.Remove(sessionId);
            return false;
        }

        return true;
    }
}

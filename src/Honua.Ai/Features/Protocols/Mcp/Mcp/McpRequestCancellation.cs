// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Honua.Ai.Protocols.Mcp;

internal sealed partial class McpSessionManager
{
    private McpMemoryRequestStore? _memoryRequests;
    private int _activeRequestCount;

    internal int ActiveRequestCount => Volatile.Read(ref _activeRequestCount);

    private IMcpRequestStore RequestStore
    {
        get
        {
            if (_store is not null)
            {
                return _store;
            }

            lock (_capacityGate)
            {
                return _memoryRequests ??= new McpMemoryRequestStore(
                    (session, principal) => _sessions.TryGetValue(session, out var entry)
                        && string.Equals(entry.PrincipalKey, principal, StringComparison.Ordinal)
                        && !IsExpired(entry, _timeProvider.GetUtcNow()), _timeProvider);
            }
        }
    }

    /// <summary>
    /// Admits one session-bound request. Stateless/no-session requests keep their
    /// ordinary abort/deadline semantics without inventing cancellation authority.
    /// </summary>
    internal async Task<(McpRequestLease? Lease, McpRequestState State)> StartRequestAsync(
        string sessionId, string principalKey, JsonElement id, CancellationToken cancellationToken)
    {
        if (Mode == McpSessionMode.Stateless || string.IsNullOrEmpty(sessionId))
        {
            return (null, McpRequestState.Active);
        }

        if (!TryRequestKey(id, out var requestKey))
        {
            return (null, McpRequestState.Missing);
        }

        if (Interlocked.Increment(ref _activeRequestCount) > McpRequestBounds.PerInstance)
        {
            Interlocked.Decrement(ref _activeRequestCount);
            return (null, McpRequestState.Capacity);
        }

        var generation = Guid.NewGuid().ToString("N");
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(2));
            var state = await UpdateAsync(McpRequestOperation.Register, budget.Token).WaitAsync(budget.Token).ConfigureAwait(false);
            if (state != McpRequestState.Active)
            {
                Interlocked.Decrement(ref _activeRequestCount);
                return (null, state);
            }

            return (new McpRequestLease(this, sessionId, principalKey, requestKey, generation,
                cancellationToken, GetLifetimeToken(sessionId)), state);
        }
        catch
        {
            Interlocked.Decrement(ref _activeRequestCount);
            // A timed-out registration may have reached Redis even if its reply
            // did not. Remove only our known generation; expiry remains a fallback.
            try
            {
                using var cleanupBudget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await UpdateAsync(McpRequestOperation.Finish, cleanupBudget.Token)
                    .WaitAsync(cleanupBudget.Token).ConfigureAwait(false);
            }
            catch (Exception cleanupError) when (IsStoreFailure(cleanupError) || cleanupError is OperationCanceledException)
            {
                if (cleanupError is not OperationCanceledException)
                {
                    LogStoreFailure(cleanupError, "request-register-cleanup");
                }
            }

            throw;
        }

        Task<McpRequestState> UpdateAsync(McpRequestOperation operation, CancellationToken token) =>
            RequestStore.UpdateRequestAsync(sessionId, principalKey, requestKey, generation, operation, token);
    }

    /// <summary>Unknown, repeated, expired and stateless cancellations create no work.</summary>
    internal async Task CancelRequestAsync(
        string sessionId, string principalKey, JsonElement id, CancellationToken cancellationToken)
    {
        if (Mode == McpSessionMode.Stateless || string.IsNullOrEmpty(sessionId) || !TryRequestKey(id, out var key))
        {
            return;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(2));
        await RequestStore.UpdateRequestAsync(sessionId, principalKey, key, string.Empty,
            McpRequestOperation.Cancel, budget.Token).WaitAsync(budget.Token).ConfigureAwait(false);
    }

    /// <summary>Separates string/integer ids without retaining attacker-sized identifiers in memory or Redis.</summary>
    internal static bool TryRequestKey(JsonElement id, out string key)
    {
        string value;
        if (id.ValueKind == JsonValueKind.String)
        {
            value = "s:" + id.GetString();
        }
        else if (id.ValueKind == JsonValueKind.Number && McpEndpointExtensions.IsIntegerNumberToken(id))
        {
            var number = id.GetRawText();
            // JSON integer spelling is unique except -0, which is the same
            // integer identity as 0. Strings remain a separate namespace.
            value = "n:" + (number == "-0" ? "0" : number);
        }
        else
        {
            key = string.Empty;
            return false;
        }

        key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        return true;
    }

    /// <summary>
    /// Owns the linked handler token and bounded authoritative polling. Completion
    /// atomically removes its generation before a response can claim success.
    /// Pub/sub delivery is not required for correctness or session-expiry cleanup.
    /// </summary>
    internal sealed class McpRequestLease : IAsyncDisposable
    {
        private readonly McpSessionManager _owner;
        private readonly string _session;
        private readonly string _principal;
        private readonly string _key;
        private readonly string _generation;
        private readonly CancellationTokenSource _work;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _monitor;
        private bool _finished;

        internal McpRequestLease(McpSessionManager owner, string session, string principal, string key,
            string generation, CancellationToken requestToken, CancellationToken sessionToken)
        {
            _owner = owner;
            _session = session;
            _principal = principal;
            _key = key;
            _generation = generation;
            _work = CancellationTokenSource.CreateLinkedTokenSource(requestToken, sessionToken);
            _work.CancelAfter(McpRequestBounds.Lifetime);
            _monitor = MonitorAsync();
        }

        internal CancellationToken Token => _work.Token;

        private async Task<McpRequestState> UpdateAsync(McpRequestOperation operation)
        {
            // Every store observation/cleanup has its own bounded network budget.
            // A broken shared store must end running work, never silently disable
            // cancellation or hold disposal indefinitely.
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            return await _owner.RequestStore.UpdateRequestAsync(_session, _principal, _key, _generation,
                operation, budget.Token).WaitAsync(budget.Token).ConfigureAwait(false);
        }

        private async Task MonitorAsync()
        {
            try
            {
                while (!_work.IsCancellationRequested)
                {
                    await Task.Delay(McpRequestBounds.PollInterval, _owner._timeProvider, _stop.Token).ConfigureAwait(false);
                    if (await UpdateAsync(McpRequestOperation.Read).ConfigureAwait(false) != McpRequestState.Active)
                    {
                        await _work.CancelAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _owner.LogStoreFailure(ex, "request-observe");
                await _work.CancelAsync().ConfigureAwait(false);
            }
        }

        internal async Task<bool> CompleteAsync()
        {
            if (_finished)
            {
                return false;
            }

            _finished = true;
            var healthy = true;
            var state = McpRequestState.Missing;
            try
            {
                await _stop.CancelAsync().ConfigureAwait(false);
                await _monitor.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A provider cancellation callback may throw after its token
                // was cancelled. It cannot skip owned cleanup or the local cap.
                _owner.LogStoreFailure(ex, "request-monitor-complete");
                healthy = false;
            }
            finally
            {
                try
                {
                    state = await UpdateAsync(McpRequestOperation.Finish).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _owner.LogStoreFailure(ex, "request-finish");
                    healthy = false;
                }
                finally
                {
                    Interlocked.Decrement(ref _owner._activeRequestCount);
                }
            }

            return healthy && state == McpRequestState.Active && !_work.IsCancellationRequested;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_finished)
                {
                    await CompleteAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _work.Dispose();
                _stop.Dispose();
            }
        }
    }
}

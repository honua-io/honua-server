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

        var request = new McpActiveRequest(sessionId, principalKey, requestKey, Guid.NewGuid().ToString("N"));
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(2));
            var state = await UpdateAsync(request, McpRequestOperation.Register, budget.Token).ConfigureAwait(false);
            if (state != McpRequestState.Active)
            {
                Interlocked.Decrement(ref _activeRequestCount);
                return (null, state);
            }

            return (new McpRequestLease(this, request, cancellationToken), state);
        }
        catch
        {
            Interlocked.Decrement(ref _activeRequestCount);
            await ForgetFailedRegistrationAsync(request).ConfigureAwait(false);
            throw;
        }
    }

    // A timed-out registration may have reached the store even if its reply did not. Remove only
    // this generation; expiry remains a fallback.
    private async Task ForgetFailedRegistrationAsync(McpActiveRequest request)
    {
        try
        {
            using var cleanupBudget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await UpdateAsync(request, McpRequestOperation.Finish, cleanupBudget.Token).ConfigureAwait(false);
        }
        catch (Exception cleanupError) when (IsStoreFailure(cleanupError) || cleanupError is OperationCanceledException)
        {
            if (cleanupError is not OperationCanceledException)
            {
                LogStoreFailure(cleanupError, "request-register-cleanup");
            }
        }
    }

    private Task<McpRequestState> UpdateAsync(McpActiveRequest request, McpRequestOperation operation, CancellationToken cancellationToken)
        => RequestStore.UpdateRequestAsync(request, operation, cancellationToken).WaitAsync(cancellationToken);

    internal async Task CancelRequestAsync(
        string sessionId, string principalKey, JsonElement id, CancellationToken cancellationToken)
    {
        if (Mode == McpSessionMode.Stateless || string.IsNullOrEmpty(sessionId) || !TryRequestKey(id, out var key))
        {
            return;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(2));
        await RequestStore.UpdateRequestAsync(new McpActiveRequest(sessionId, principalKey, key, string.Empty),
            McpRequestOperation.Cancel, budget.Token).WaitAsync(budget.Token).ConfigureAwait(false);
    }

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

    // A request this node runs. The node's poll stops it when it is cancelled or its session ends
    // anywhere; completing it removes the store entry.
    internal sealed class McpRequestLease : IAsyncDisposable
    {
        private readonly McpSessionManager _owner;
        private readonly CancellationTokenSource _work;
        private bool _finished;

        internal McpRequestLease(McpSessionManager owner, McpActiveRequest request, CancellationToken requestToken)
        {
            _owner = owner;
            Request = request;
            _work = CancellationTokenSource.CreateLinkedTokenSource(requestToken, owner.SessionLifetimeOf(request.SessionId));
            _work.CancelAfter(McpRequestBounds.Lifetime);
            owner.WatchRequest(this);
        }

        internal McpActiveRequest Request { get; }

        internal CancellationToken Token => _work.Token;

        internal void Stop()
        {
            try
            {
                _work.Cancel();
            }
            catch (AggregateException ex)
            {
                // A provider cancellation callback threw after the token was cancelled.
                _owner.LogStoreFailure(ex, "request-cancel-callback");
            }
            catch (ObjectDisposedException)
            {
                // Completed between the poll and this stop.
            }
        }

        internal async Task<bool> CompleteAsync()
        {
            if (_finished)
            {
                return false;
            }

            _finished = true;
            _owner.UnwatchRequest(this);
            var healthy = true;
            var state = McpRequestState.Missing;
            try
            {
                using var budget = new CancellationTokenSource(PollBudget);
                state = await _owner.UpdateAsync(Request, McpRequestOperation.Finish, budget.Token).ConfigureAwait(false);
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
            }
        }
    }
}

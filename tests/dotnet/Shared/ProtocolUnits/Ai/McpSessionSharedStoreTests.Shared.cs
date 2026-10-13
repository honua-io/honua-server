// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class McpSessionSharedStoreTests
{
    /// <summary>
    /// Shared in-process stand-in for Redis: one instance is handed to several
    /// managers, each playing a server replica. Honours TTLs against the test clock.
    /// </summary>
    internal sealed class FakeSharedSessionStore(TimeProvider time) : IMcpSessionStore
    {
        private readonly ConcurrentDictionary<string, (McpSessionRecord Record, DateTimeOffset ExpiresAt)> _records =
            new(StringComparer.Ordinal);

        private readonly ConcurrentBag<Action<string>> _subscribers = [];

        private int _reads;

        public bool Fail { get; set; }

        public bool FailTouch { get; set; }

        public int Reads => _reads;

        public Action<string>? OnRead { get; set; }

        public bool Contains(string sessionId) => TryGetLive(sessionId, out _);

        public DateTimeOffset ExpiresAt(string sessionId) => _records[sessionId].ExpiresAt;

        public Task SaveAsync(McpSessionRecord record, TimeSpan idleTimeout, CancellationToken cancellationToken)
        {
            ThrowIfFailing();
            _records[record.SessionId] = (record, time.GetUtcNow() + idleTimeout);
            return Task.CompletedTask;
        }

        public Task<McpSessionRecord?> GetAndTouchAsync(string sessionId, TimeSpan idleTimeout, CancellationToken cancellationToken)
        {
            ThrowIfFailing();
            Interlocked.Increment(ref _reads);
            if (!TryGetLive(sessionId, out var record))
            {
                return Task.FromResult<McpSessionRecord?>(null);
            }

            var snapshot = record;
            _records[sessionId] = (record, time.GetUtcNow() + idleTimeout);
            OnRead?.Invoke(sessionId);
            return Task.FromResult<McpSessionRecord?>(snapshot);
        }

        public Task<McpSessionRecord?> GetAsync(string sessionId, CancellationToken cancellationToken)
        {
            ThrowIfFailing();
            Interlocked.Increment(ref _reads);
            return Task.FromResult(TryGetLive(sessionId, out var record) ? record : null);
        }

        public Task<bool> TouchAsync(string sessionId, TimeSpan idleTimeout, CancellationToken cancellationToken)
        {
            ThrowIfFailing();
            if (FailTouch)
            {
                throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "simulated touch outage");
            }

            if (!TryGetLive(sessionId, out var record))
            {
                return Task.FromResult(false);
            }

            _records[sessionId] = (record, time.GetUtcNow() + idleTimeout);
            // The read path touches only after the principal matches. A delete that
            // races that touch has already extended the TTL, then publishes, and
            // Adopt must still refuse the snapshot.
            OnRead?.Invoke(sessionId);
            return Task.FromResult(true);
        }

        public Task<bool> ExistsAsync(string sessionId, CancellationToken cancellationToken)
        {
            ThrowIfFailing();
            return Task.FromResult(TryGetLive(sessionId, out _));
        }

        public Task RemoveAsync(string sessionId, CancellationToken cancellationToken)
        {
            ThrowIfFailing();
            _records.TryRemove(sessionId, out _);
            foreach (var subscriber in _subscribers.ToArray())
            {
                subscriber(sessionId);
            }

            return Task.CompletedTask;
        }

        public IDisposable? SubscribeToRemovals(Action<string> onRemoved)
        {
            _subscribers.Add(onRemoved);
            return null;
        }

        public void RemoveSilently(string sessionId) => _records.TryRemove(sessionId, out _);

        private bool TryGetLive(string sessionId, out McpSessionRecord record)
        {
            if (_records.TryGetValue(sessionId, out var entry) && entry.ExpiresAt > time.GetUtcNow())
            {
                record = entry.Record;
                return true;
            }

            _records.TryRemove(sessionId, out _);
            record = null!;
            return false;
        }

        private void ThrowIfFailing()
        {
            if (Fail)
            {
                throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "simulated outage");
            }
        }
    }

}

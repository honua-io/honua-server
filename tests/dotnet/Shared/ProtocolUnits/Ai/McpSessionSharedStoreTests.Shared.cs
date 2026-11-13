// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Data.Common;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class McpSessionSharedStoreTests
{
    // Shared in-process stand-in for the system database: one instance is handed to several
    // managers, each playing a server replica. Honours TTLs against the test clock.
    internal sealed class FakeSharedSessionStore(TimeProvider time) : IMcpSessionStore
    {
        private readonly ConcurrentDictionary<string, (McpSessionRecord Record, DateTimeOffset ExpiresAt)> _records =
            new(StringComparer.Ordinal);

        private int _reads;
        private int _polls;
        private McpMemoryRequestStore? _requests;

        public Exception? RequestReadException { get; set; }

        public Func<CancellationToken, Task>? BeforeRegister { get; set; }

        public Func<CancellationToken, Task>? AfterFinish { get; set; }

        public async Task<McpRequestState> UpdateRequestAsync(
            McpActiveRequest request, McpRequestOperation operation, CancellationToken cancellationToken)
        {
            ThrowIfFailing();
            if (operation == McpRequestOperation.Register && BeforeRegister is { } beforeRegister)
            {
                await beforeRegister(cancellationToken);
            }

            if (operation == McpRequestOperation.Read && RequestReadException is { } failure)
            {
                throw failure;
            }

            var requests = LazyInitializer.EnsureInitialized(ref _requests, () => new McpMemoryRequestStore(
                (id, principal) => TryGetLive(id, out var record) && record.PrincipalKey == principal, time));
            var state = await requests.UpdateRequestAsync(request, operation, cancellationToken);
            if (operation == McpRequestOperation.Finish && AfterFinish is { } afterFinish)
            {
                await afterFinish(cancellationToken);
            }

            return state;
        }


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
                throw new SimulatedStoreOutageException("simulated touch outage");
            }

            if (!TryGetLive(sessionId, out var record))
            {
                return Task.FromResult(false);
            }

            _records[sessionId] = (record, time.GetUtcNow() + idleTimeout);
            // The read path touches only after the principal matches. A delete can race
            // that touch after it has extended the TTL.
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
            return Task.CompletedTask;
        }

        public async Task<McpPollResult> PollAsync(McpPollRequest request, CancellationToken cancellationToken)
        {
            ThrowIfFailing();
            if (RequestReadException is { } failure)
            {
                throw failure;
            }

            if (BeforePoll is { } beforePoll)
            {
                await beforePoll(cancellationToken);
            }

            Interlocked.Increment(ref _polls);
            var endedSessions = request.SessionIds.Where(id => !TryGetLive(id, out _)).ToArray();
            var endedRequests = await McpRequestPoll.ReadEachAsync(this, request.Requests, cancellationToken);
            return new McpPollResult(endedSessions, endedRequests);
        }

        public Func<CancellationToken, Task>? BeforePoll { get; set; }

        public int Polls => _polls;

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
                throw new SimulatedStoreOutageException("simulated outage");
            }
        }
    }

    internal sealed class SimulatedStoreOutageException(string message) : DbException(message);

}

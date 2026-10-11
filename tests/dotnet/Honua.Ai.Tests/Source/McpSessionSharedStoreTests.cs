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

/// <summary>
/// Cross-instance MCP session semantics (honua-release#531, journey stage 1.5):
/// two <see cref="McpSessionManager"/> instances sharing one
/// <see cref="IMcpSessionStore"/> behave as two server replicas behind a
/// non-sticky router. The invariant under test: a session negotiated on any
/// instance is honoured by every instance, or refused (HTTP 404 at the
/// transport), never silently downgraded.
/// </summary>
public sealed class McpSessionSharedStoreTests
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    [UnitTest]
    public async Task SessionMintedOnA_IsHonouredOnB_WithTheSameNegotiatedState()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new FakeSharedSessionStore(time);
        var a = NewManager(store, time);
        var b = NewManager(store, time);

        var id = await a.TryCreateSessionAsync("sub:alice", elicitationSupported: true, "setup", CancellationToken.None);
        id.Should().NotBeNull();

        // B has never seen the id; it reads it through from the shared store.
        b.GetWorkflowView(id!).Should().BeNull("B has not served the session yet");
        (await b.ValidateAccessAsync(id!, "sub:alice", CancellationToken.None))
            .Should().Be(McpSessionValidation.Valid);
        b.GetWorkflowView(id!).Should().Be("setup", "the view negotiated on A must survive the hop to B");
        b.SupportsElicitation(id!).Should().BeTrue();
    }

    [UnitTest]
    public async Task SessionMintedOnA_IsBoundToItsPrincipal_OnB()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new FakeSharedSessionStore(time);
        var a = NewManager(store, time);
        var b = NewManager(store, time);

        var id = await a.TryCreateSessionAsync("sub:alice", false, "setup", CancellationToken.None);

        (await b.ValidateAccessAsync(id!, "sub:mallory", CancellationToken.None))
            .Should().Be(McpSessionValidation.PrincipalMismatch);
        (await b.ValidateAccessAsync(id!, McpSessionManager.AnonymousPrincipalKey, CancellationToken.None))
            .Should().Be(McpSessionValidation.PrincipalMismatch);
    }

    [UnitTest]
    public async Task UnknownId_IsUnknownOnEveryInstance()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new FakeSharedSessionStore(time);
        var b = NewManager(store, time);

        (await b.ValidateAccessAsync(new string('a', 64), "sub:alice", CancellationToken.None))
            .Should().Be(McpSessionValidation.Unknown);
        (await b.ValidateAccessAsync("dead beef", "sub:alice", CancellationToken.None))
            .Should().Be(McpSessionValidation.Unknown);
        store.Reads.Should().Be(1, "a malformed id is refused without a store round trip");
    }

    [UnitTest]
    public async Task DeleteOnB_EndsTheSessionOnA()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new FakeSharedSessionStore(time);
        var a = NewManager(store, time);
        var b = NewManager(store, time);

        var id = await a.TryCreateSessionAsync("sub:alice", false, "setup", CancellationToken.None);
        (await b.ValidateAccessAsync(id!, "sub:alice", CancellationToken.None)).Should().Be(McpSessionValidation.Valid);

        var lifetimeOnA = a.GetLifetimeToken(id!);
        lifetimeOnA.IsCancellationRequested.Should().BeFalse();

        (await b.TerminateAsync(id!, CancellationToken.None)).Should().Be(McpSessionTermination.Terminated);

        // The removal broadcast ends A's node-local session work (SSE channel,
        // progress bridges) at once, not on A's next request.
        lifetimeOnA.IsCancellationRequested.Should().BeTrue();
        (await a.ValidateAccessAsync(id!, "sub:alice", CancellationToken.None))
            .Should().Be(McpSessionValidation.Unknown);
        a.GetWorkflowView(id!).Should().BeNull();
    }

    [UnitTest]
    public async Task EveryAcceptedRequest_SlidesTheSharedExpiry_OnAnyInstance()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new FakeSharedSessionStore(time);
        var a = NewManager(store, time);
        var b = NewManager(store, time);

        var id = await a.TryCreateSessionAsync("sub:alice", false, "setup", CancellationToken.None);
        store.ExpiresAt(id!).Should().Be(time.GetUtcNow() + IdleTimeout, "the record TTL is the idle timeout");

        // Traffic only on B for longer than the idle window: B's requests keep the
        // shared record alive, so A (whose local copy went idle) reads it back.
        for (var i = 0; i < 4; i++)
        {
            time.Advance(TimeSpan.FromMinutes(20));
            (await b.ValidateAccessAsync(id!, "sub:alice", CancellationToken.None))
                .Should().Be(McpSessionValidation.Valid);
            store.ExpiresAt(id!).Should().Be(time.GetUtcNow() + IdleTimeout);
        }

        (await a.ValidateAccessAsync(id!, "sub:alice", CancellationToken.None))
            .Should().Be(McpSessionValidation.Valid);
        a.GetWorkflowView(id!).Should().Be("setup");

        // Then a full idle window with no traffic anywhere expires it everywhere.
        time.Advance(IdleTimeout + TimeSpan.FromSeconds(1));
        (await a.ValidateAccessAsync(id!, "sub:alice", CancellationToken.None))
            .Should().Be(McpSessionValidation.Unknown);
        (await b.ValidateAccessAsync(id!, "sub:alice", CancellationToken.None))
            .Should().Be(McpSessionValidation.Unknown);
    }

    [UnitTest]
    public async Task StoreOutage_KeepsLocalSessions_AndRefusesUnknownOnes()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new FakeSharedSessionStore(time);
        var a = NewManager(store, time);
        var b = NewManager(store, time);
        var id = await a.TryCreateSessionAsync("sub:alice", false, "setup", CancellationToken.None);

        store.Fail = true;

        (await a.ValidateAccessAsync(id!, "sub:alice", CancellationToken.None))
            .Should().Be(McpSessionValidation.Valid, "a session this instance holds keeps working through a store outage");
        (await b.ValidateAccessAsync(id!, "sub:alice", CancellationToken.None))
            .Should().Be(McpSessionValidation.Unknown, "an instance that cannot read the session refuses it rather than downgrading it");
        (await a.TryCreateSessionAsync("sub:bob", false, null, CancellationToken.None))
            .Should().NotBeNull("a failed persist leaves a session only the minting instance holds");
    }

    [UnitTest]
    public async Task WithoutAStore_SessionsStaySingleInstance()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var a = new McpSessionManager(idleTimeout: IdleTimeout, timeProvider: time);
        var b = new McpSessionManager(idleTimeout: IdleTimeout, timeProvider: time);

        a.HasSharedStore.Should().BeFalse();
        var id = await a.TryCreateSessionAsync("sub:alice", false, "setup", CancellationToken.None);

        (await a.ValidateAccessAsync(id!, "sub:alice", CancellationToken.None)).Should().Be(McpSessionValidation.Valid);
        a.GetWorkflowView(id!).Should().Be("setup");
        (await b.ValidateAccessAsync(id!, "sub:alice", CancellationToken.None)).Should().Be(McpSessionValidation.Unknown);
        (await a.TerminateAsync(id!, CancellationToken.None)).Should().Be(McpSessionTermination.Terminated);
        (await a.ValidateAccessAsync(id!, "sub:alice", CancellationToken.None)).Should().Be(McpSessionValidation.Unknown);
    }

    [UnitTest]
    public async Task CapacityEviction_OnlyDropsTheLocalCacheEntry()
    {
        // The local LRU order reflects only this replica's traffic, so evicting to
        // admit a new session must not end a session that may be active elsewhere.
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new FakeSharedSessionStore(time);
        var a = new McpSessionManager(maxSessions: 1, idleTimeout: IdleTimeout, timeProvider: time, store: store);
        var b = NewManager(store, time);

        var first = await a.TryCreateSessionAsync("sub:alice", false, "setup", CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
        var second = await a.TryCreateSessionAsync("sub:bob", false, null, CancellationToken.None);

        second.Should().NotBeNull();
        store.Contains(first!).Should().BeTrue();
        (await b.ValidateAccessAsync(first!, "sub:alice", CancellationToken.None)).Should().Be(McpSessionValidation.Valid);
        b.GetWorkflowView(first!).Should().Be("setup");
        (await a.ValidateAccessAsync(first!, "sub:alice", CancellationToken.None)).Should().Be(McpSessionValidation.Valid);
    }

    [UnitTest]
    public async Task LocallyIdleCopy_OfASessionActiveElsewhere_KeepsItsNodeLocalState()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new FakeSharedSessionStore(time);
        var a = NewManager(store, time);
        var b = NewManager(store, time);

        var id = await a.TryCreateSessionAsync("sub:alice", false, "setup", CancellationToken.None);
        var lifetimeOnA = a.GetLifetimeToken(id!);
        for (var i = 0; i < 3; i++)
        {
            time.Advance(TimeSpan.FromMinutes(20));
            (await b.ValidateAccessAsync(id!, "sub:alice", CancellationToken.None)).Should().Be(McpSessionValidation.Valid);
        }

        // A's copy is older than the idle window, but the shared record is live:
        // A confirms with the store first and keeps its SSE channel and lifetime.
        (await a.ValidateAccessAsync(id!, "sub:alice", CancellationToken.None)).Should().Be(McpSessionValidation.Valid);
        lifetimeOnA.IsCancellationRequested.Should().BeFalse();
        a.GetLifetimeToken(id!).Should().Be(lifetimeOnA);
    }

    [UnitTest]
    public async Task PrincipalMismatch_OnASessionGoneElsewhere_IsUnknown_AndDoesNotSlideTheExpiry()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new FakeSharedSessionStore(time);
        var a = NewManager(store, time);

        var id = await a.TryCreateSessionAsync("sub:alice", false, "setup", CancellationToken.None);
        var expiry = store.ExpiresAt(id!);
        time.Advance(TimeSpan.FromMinutes(5));
        (await a.ValidateAccessAsync(id!, "sub:mallory", CancellationToken.None))
            .Should().Be(McpSessionValidation.PrincipalMismatch);
        store.ExpiresAt(id!).Should().Be(expiry, "another identity must not keep the session alive");

        // Removed elsewhere without a broadcast reaching A (a missed message).
        store.RemoveSilently(id!);
        (await a.ValidateAccessAsync(id!, "sub:mallory", CancellationToken.None))
            .Should().Be(McpSessionValidation.Unknown, "a gone session must not leak that it was bound to someone else");
    }

    [UnitTest]
    public async Task Delete_WhenTheStoreIsUnavailable_IsNotAcknowledged()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new FakeSharedSessionStore(time);
        var a = NewManager(store, time);
        var id = await a.TryCreateSessionAsync("sub:alice", false, "setup", CancellationToken.None);

        store.Fail = true;
        (await a.TerminateAsync(id!, CancellationToken.None)).Should().Be(McpSessionTermination.StoreUnavailable);

        store.Fail = false;
        store.Contains(id!).Should().BeTrue("the shared record survived, so the delete was not acknowledged");
    }

    [UnitTest]
    public void StatelessSessionFallback_DefaultsOnOnlyInDevelopment()
    {
        ResolveOptions(Environments.Development).StatelessSessionFallback.Should().BeTrue();
        ResolveOptions(Environments.Production).StatelessSessionFallback.Should().BeFalse();
        ResolveOptions(Environments.Staging).StatelessSessionFallback.Should().BeFalse();
        ResolveOptions("Test").StatelessSessionFallback.Should().BeFalse();
        ResolveOptions(environment: null).StatelessSessionFallback.Should().BeFalse();
    }

    [UnitTest]
    public void StatelessSessionFallback_ExplicitSettingOverridesTheEnvironmentDefault()
    {
        ResolveOptions(Environments.Production, explicitValue: "true").StatelessSessionFallback.Should().BeTrue();
        ResolveOptions(Environments.Development, explicitValue: "false").StatelessSessionFallback.Should().BeFalse();
    }

    [UnitTest]
    public void SessionManager_SharesSessions_OnlyWhenRedisOrAStoreIsComposed()
    {
        Compose(services => { }).HasSharedStore.Should().BeFalse("Redis-off keeps the single-instance registry");
        Compose(services => services.AddSingleton(Substitute.For<IConnectionMultiplexer>()))
            .HasSharedStore.Should().BeTrue("a composed multiplexer backs sessions with RedisMcpSessionStore");
        Compose(services => services.AddSingleton<IMcpSessionStore>(new FakeSharedSessionStore(TimeProvider.System)))
            .HasSharedStore.Should().BeTrue();
    }

    private static McpSessionManager Compose(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddMcpOptions(new ConfigurationBuilder().AddInMemoryCollection().Build());
        configure(services);
        services.AddMcpSessionManager();
        return services.BuildServiceProvider().GetRequiredService<McpSessionManager>();
    }

    private static McpOptions ResolveOptions(string? environment, string? explicitValue = null)
    {
        var values = new Dictionary<string, string?>();
        if (explicitValue is not null)
        {
            values["Mcp:StatelessSessionFallback"] = explicitValue;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        if (environment is not null)
        {
            services.AddSingleton<IHostEnvironment>(new StubHostEnvironment(environment));
        }

        services.AddMcpOptions(configuration);
        return services.BuildServiceProvider().GetRequiredService<IOptions<McpOptions>>().Value;
    }

    private static McpSessionManager NewManager(IMcpSessionStore store, TimeProvider time) =>
        new(idleTimeout: IdleTimeout, timeProvider: time, store: store);

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Honua.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

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

        public int Reads => _reads;

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

            _records[sessionId] = (record, time.GetUtcNow() + idleTimeout);
            return Task.FromResult<McpSessionRecord?>(record);
        }

        public Task<bool> TouchAsync(string sessionId, TimeSpan idleTimeout, CancellationToken cancellationToken)
        {
            ThrowIfFailing();
            if (!TryGetLive(sessionId, out var record))
            {
                return Task.FromResult(false);
            }

            _records[sessionId] = (record, time.GetUtcNow() + idleTimeout);
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

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}

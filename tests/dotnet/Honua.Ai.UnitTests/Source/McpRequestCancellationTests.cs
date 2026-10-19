// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Ai.Protocols.Mcp;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed class McpRequestCancellationTests
{
    private const string Alice = "actor:alice|tenant:a";
    private const string Bob = "actor:bob|tenant:a";

    [UnitTest]
    public async Task CancellationOnAnotherReplica_StopsTheOriginalTokenWithoutAnyBroadcast()
    {
        var store = new McpSessionSharedStoreTests.FakeSharedSessionStore(TimeProvider.System);
        var a = new McpSessionManager(store: store);
        var b = new McpSessionManager(store: store);
        var session = (await a.TryCreateSessionAsync(Alice, false, null, CancellationToken.None))!;
        var (lease, state) = await a.StartRequestAsync(session, Alice, Id("7"), CancellationToken.None);
        Assert.Equal(McpRequestState.Active, state);
        await using var request = Assert.IsType<McpSessionManager.McpRequestLease>(lease);
        var stopped = Task.Delay(Timeout.Infinite, request.Token);

        await b.CancelRequestAsync(session, Alice, Id("7"), CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopped.WaitAsync(TimeSpan.FromSeconds(4)));
        Assert.False(await request.CompleteAsync());
    }

    [UnitTheory]
    [InlineData(Bob, "7")]
    [InlineData("actor:alice|tenant:b", "7")]
    [InlineData(Alice, "\"7\"")]
    [InlineData(Alice, "8")]
    public async Task DifferentPrincipalTenantOrTypedId_DoesNotCancel(string principal, string target)
    {
        var manager = new McpSessionManager();
        manager.TryCreateSession(Alice, false, out var session);
        var (lease, _) = await manager.StartRequestAsync(session!, Alice, Id("7"), CancellationToken.None);
        await using var request = Assert.IsType<McpSessionManager.McpRequestLease>(lease);

        await manager.CancelRequestAsync(session!, principal, Id(target), CancellationToken.None);

        Assert.True(await request.CompleteAsync()); // Atomic state, independent of timer scheduling.
        Assert.False(request.Token.IsCancellationRequested);
    }

    [UnitTest]
    public async Task SamePrincipalDifferentSession_CannotCancelOriginalRequest()
    {
        var manager = new McpSessionManager();
        manager.TryCreateSession(Alice, false, out var first);
        manager.TryCreateSession(Alice, false, out var second);
        var (lease, _) = await manager.StartRequestAsync(first!, Alice, Id("1"), CancellationToken.None);
        await using var request = Assert.IsType<McpSessionManager.McpRequestLease>(lease);
        await manager.CancelRequestAsync(second!, Alice, Id("1"), CancellationToken.None);
        Assert.True(await request.CompleteAsync());
    }

    [UnitTest]
    public async Task DuplicateAndRepeatedCancellation_DoNotReplaceRegistration_CompletionSeesCancelBeforePoll()
    {
        var manager = new McpSessionManager();
        manager.TryCreateSession(Alice, false, out var session);
        var (lease, _) = await manager.StartRequestAsync(session!, Alice, Id("1"), CancellationToken.None);
        await using var request = Assert.IsType<McpSessionManager.McpRequestLease>(lease);
        var duplicate = await manager.StartRequestAsync(session!, Alice, Id("1"), CancellationToken.None);
        Assert.Null(duplicate.Lease);
        Assert.Equal(McpRequestState.Duplicate, duplicate.State);

        await manager.CancelRequestAsync(session!, Alice, Id("1"), CancellationToken.None);
        await manager.CancelRequestAsync(session!, Alice, Id("1"), CancellationToken.None);
        Assert.False(await request.CompleteAsync());

        var next = await manager.StartRequestAsync(session!, Alice, Id("1"), CancellationToken.None);
        await using var reused = Assert.IsType<McpSessionManager.McpRequestLease>(next.Lease);
        Assert.True(await reused.CompleteAsync());
    }

    [UnitTest]
    public async Task UnknownCancellation_DoesNotPoisonARequestAdmittedLater()
    {
        var manager = new McpSessionManager();
        manager.TryCreateSession(Alice, false, out var session);
        await manager.CancelRequestAsync(session!, Alice, Id("1"), CancellationToken.None);
        var admitted = await manager.StartRequestAsync(session!, Alice, Id("1"), CancellationToken.None);
        await using var request = Assert.IsType<McpSessionManager.McpRequestLease>(admitted.Lease);
        Assert.True(await request.CompleteAsync());
    }

    [UnitTest]
    public async Task SessionAndPrincipalBounds_AreIndependentAndCompletionReleasesCapacity()
    {
        var store = new McpMemoryRequestStore((_, _) => true, TimeProvider.System);
        Task<McpRequestState> Update(string session, string principal, string id, McpRequestOperation operation) =>
            store.UpdateRequestAsync(session, principal, id, "generation", operation, CancellationToken.None);
        for (var i = 0; i < McpRequestBounds.PerPrincipal; i++)
        {
            Assert.Equal(McpRequestState.Active,
                await Update("s" + (i / McpRequestBounds.PerSession), Alice, "r" + i, McpRequestOperation.Register));
        }

        Assert.Equal(McpRequestState.Capacity, await Update("s0", Alice, "extra", McpRequestOperation.Register));
        Assert.Equal(McpRequestState.Capacity, await Update("new-session", Alice, "extra", McpRequestOperation.Register));
        Assert.Equal(McpRequestState.Active, await Update("other", Bob, "extra", McpRequestOperation.Register));
        Assert.Equal(McpRequestState.Active, await Update("s0", Alice, "r0", McpRequestOperation.Finish));
        Assert.Equal(McpRequestState.Active, await Update("s0", Alice, "extra", McpRequestOperation.Register));
    }

    [UnitTest]
    public async Task ReadsAndCancellation_DoNotExtendRequestLifetime_ExpiryReleasesCapacity()
    {
        var time = new MutableTimeProvider();
        var store = new McpMemoryRequestStore((_, _) => true, time);
        Task<McpRequestState> Update(McpRequestOperation operation) =>
            store.UpdateRequestAsync("session", Alice, "id", "generation", operation, CancellationToken.None);
        Assert.Equal(McpRequestState.Active, await Update(McpRequestOperation.Register));
        time.Advance(McpRequestBounds.Lifetime - TimeSpan.FromSeconds(1));
        Assert.Equal(McpRequestState.Active, await Update(McpRequestOperation.Read));
        Assert.Equal(McpRequestState.Cancelled, await Update(McpRequestOperation.Cancel));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(McpRequestState.Missing, await Update(McpRequestOperation.Read));
        Assert.Equal(McpRequestState.Missing, await Update(McpRequestOperation.Cancel));
        Assert.Equal(McpRequestState.Active, await Update(McpRequestOperation.Register));
    }

    [UnitTest]
    public async Task DelayedReadAndFinish_CannotAffectAReplacementGeneration()
    {
        var store = new McpMemoryRequestStore((s, p) => s == "session" && p == Alice, TimeProvider.System);
        async Task<McpRequestState> Update(string generation, McpRequestOperation operation) =>
            await store.UpdateRequestAsync("session", Alice, "typed-id", generation, operation, CancellationToken.None);

        Assert.Equal(McpRequestState.Active, await Update("old", McpRequestOperation.Register));
        Assert.Equal(McpRequestState.Active, await Update("old", McpRequestOperation.Finish));
        Assert.Equal(McpRequestState.Active, await Update("new", McpRequestOperation.Register));
        Assert.Equal(McpRequestState.Missing, await Update("old", McpRequestOperation.Finish));
        Assert.Equal(McpRequestState.Missing, await Update("old", McpRequestOperation.Read));
        Assert.Equal(McpRequestState.Active, await Update("new", McpRequestOperation.Read));
        Assert.Equal(McpRequestState.Missing, await store.UpdateRequestAsync("session", Bob, "typed-id", "new",
            McpRequestOperation.Finish, CancellationToken.None));
        Assert.Equal(McpRequestState.Active, await Update("new", McpRequestOperation.Read));
    }

    [UnitTest]
    public async Task SharedStoreFailure_StopsRunningWorkAndRefusesNewWork()
    {
        var store = new McpSessionSharedStoreTests.FakeSharedSessionStore(TimeProvider.System);
        var manager = new McpSessionManager(store: store);
        var session = (await manager.TryCreateSessionAsync(Alice, false, null, CancellationToken.None))!;
        var (lease, _) = await manager.StartRequestAsync(session, Alice, Id("1"), CancellationToken.None);
        await using var request = Assert.IsType<McpSessionManager.McpRequestLease>(lease);
        var stopped = Task.Delay(Timeout.Infinite, request.Token);
        store.Fail = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopped.WaitAsync(TimeSpan.FromSeconds(4)));
        Assert.False(await request.CompleteAsync());
        await Assert.ThrowsAsync<StackExchange.Redis.RedisConnectionException>(async () =>
            await manager.StartRequestAsync(session, Alice, Id("2"), CancellationToken.None));
    }

    [UnitTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnexpectedStoreOrCancellationCallbackFailure_StillStopsWorkCleansGenerationAndReleasesLocalCount(bool callbackFailure)
    {
        var store = new McpSessionSharedStoreTests.FakeSharedSessionStore(TimeProvider.System);
        var manager = new McpSessionManager(store: store);
        var session = (await manager.TryCreateSessionAsync(Alice, false, null, CancellationToken.None))!;
        var (lease, _) = await manager.StartRequestAsync(session, Alice, Id("1"), CancellationToken.None);
        await using var request = Assert.IsType<McpSessionManager.McpRequestLease>(lease);
        Assert.Equal(1, manager.ActiveRequestCount);
        using var callback = callbackFailure
            ? request.Token.Register(() => throw new InvalidOperationException("provider cancellation callback failed"))
            : default;
        var stopped = Task.Delay(Timeout.Infinite, request.Token);
        if (callbackFailure)
        {
            await manager.CancelRequestAsync(session, Alice, Id("1"), CancellationToken.None);
        }
        else
        {
            store.RequestReadException = new InvalidOperationException("unexpected store implementation failure");
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopped.WaitAsync(TimeSpan.FromSeconds(4)));
        Assert.False(await request.CompleteAsync());
        Assert.Equal(0, manager.ActiveRequestCount);
        store.RequestReadException = null;
        var next = await manager.StartRequestAsync(session, Alice, Id("1"), CancellationToken.None);
        await using var reused = Assert.IsType<McpSessionManager.McpRequestLease>(next.Lease);
        Assert.True(await reused.CompleteAsync());
    }

    [UnitTest]
    public async Task MissedRemovalBroadcast_EndsWorkWhenAuthoritativeSessionDisappears()
    {
        var store = new McpSessionSharedStoreTests.FakeSharedSessionStore(TimeProvider.System);
        var manager = new McpSessionManager(store: store);
        var session = (await manager.TryCreateSessionAsync(Alice, false, null, CancellationToken.None))!;
        var (lease, _) = await manager.StartRequestAsync(session, Alice, Id("1"), CancellationToken.None);
        await using var request = Assert.IsType<McpSessionManager.McpRequestLease>(lease);
        var stopped = Task.Delay(Timeout.Infinite, request.Token);
        store.RemoveSilently(session);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopped.WaitAsync(TimeSpan.FromSeconds(4)));
        Assert.False(await request.CompleteAsync());
    }

    [UnitTest]
    public async Task SessionIdleExpiry_StopsWorkWithoutTheMonitorRefreshingItsTtl()
    {
        var time = new MutableTimeProvider();
        var store = new McpSessionSharedStoreTests.FakeSharedSessionStore(time);
        var manager = new McpSessionManager(store: store, timeProvider: time, idleTimeout: TimeSpan.FromSeconds(1));
        var session = (await manager.TryCreateSessionAsync(Alice, false, null, CancellationToken.None))!;
        var (lease, _) = await manager.StartRequestAsync(session, Alice, Id("1"), CancellationToken.None);
        await using var request = Assert.IsType<McpSessionManager.McpRequestLease>(lease);
        var stopped = Task.Delay(Timeout.Infinite, request.Token);
        time.Advance(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopped.WaitAsync(TimeSpan.FromSeconds(4)));
        Assert.False(await request.CompleteAsync());
        Assert.Equal(0, manager.ActiveRequestCount);
    }

    [UnitTest]
    public async Task ThrowingProviderCallback_OnSessionTermination_PropagatesFailureButDisposesLifetimeAndCleansRequest()
    {
        var manager = new McpSessionManager();
        manager.TryCreateSession(Alice, false, out var session);
        var lifetime = manager.GetLifetimeToken(session!);
        _ = lifetime.WaitHandle; // Materialize a session-owned native resource.
        var (lease, _) = await manager.StartRequestAsync(session!, Alice, Id("1"), CancellationToken.None);
        await using var request = Assert.IsType<McpSessionManager.McpRequestLease>(lease);
        using var callback = request.Token.Register(() => throw new InvalidOperationException("provider cancellation failed"));
        var stopped = Task.Delay(Timeout.Infinite, request.Token);

        Assert.Throws<AggregateException>(() => manager.Terminate(session!));

        Assert.False(manager.IsValid(session!));
        Assert.True(request.Token.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => lifetime.WaitHandle);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopped.WaitAsync(TimeSpan.FromSeconds(4)));
        Assert.False(await request.CompleteAsync());
        Assert.Equal(0, manager.ActiveRequestCount);
        manager.TryCreateSession(Alice, false, out var replacement);
        var next = await manager.StartRequestAsync(replacement!, Alice, Id("1"), CancellationToken.None);
        await using var reused = Assert.IsType<McpSessionManager.McpRequestLease>(next.Lease);
        Assert.True(await reused.CompleteAsync());
    }

    [UnitTest]
    public async Task DisposalAndOriginalAbort_ReleaseSlotsWithoutCreatingAJob()
    {
        var manager = new McpSessionManager();
        manager.TryCreateSession(Alice, false, out var session);
        using var abort = new CancellationTokenSource();
        var (lease, _) = await manager.StartRequestAsync(session!, Alice, Id("1"), abort.Token);
        Assert.NotNull(lease);
        abort.Cancel();
        Assert.True(lease.Token.IsCancellationRequested);
        await lease.DisposeAsync();

        var next = await manager.StartRequestAsync(session!, Alice, Id("1"), CancellationToken.None);
        await using var request = Assert.IsType<McpSessionManager.McpRequestLease>(next.Lease);
        Assert.True(await request.CompleteAsync());
        Assert.False(manager.TryGetJobSession("1", out _));
    }

    [UnitTheory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("1.1")]
    [InlineData("1e0")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void InvalidTargetIds_HaveNoCancellationAuthority(string json) =>
        Assert.False(McpSessionManager.TryRequestKey(Id(json), out _));

    [UnitTest]
    public async Task SessionlessAndStatelessRequests_KeepOrdinaryExecutionWithoutCrossClientAuthority()
    {
        var manager = new McpSessionManager();
        var admission = await manager.StartRequestAsync(string.Empty, Alice, Id("1"), CancellationToken.None);
        Assert.Null(admission.Lease);
        Assert.Equal(McpRequestState.Active, admission.State);
        var stateless = new McpSessionManager(statelessTokens: new McpStatelessSessionTokens(
            new string('k', 64), TimeSpan.FromHours(1), TimeProvider.System));
        var session = (await stateless.TryCreateSessionAsync(Alice, false, null, CancellationToken.None))!;
        Assert.Null((await stateless.StartRequestAsync(session, Alice, Id("1"), CancellationToken.None)).Lease);
        await stateless.CancelRequestAsync(session, Alice, Id("1"), CancellationToken.None);
    }

    private static JsonElement Id(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Ai.Protocols.Mcp;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class McpRequestCancellationTests
{
    [UnitTest]
    public async Task CompleteAsync_PollSnapshotsRequestBeforeFinish_DoesNotCancelSuccessfulCompletion()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new McpSessionSharedStoreTests.FakeSharedSessionStore(TimeProvider.System)
        {
            BeforePoll = async token =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        };
        await using var manager = new McpSessionManager(store: store);
        var session = (await manager.TryCreateSessionAsync(Alice, false, null, CancellationToken.None))!;
        var (lease, _) = await manager.StartRequestAsync(session, Alice, Id("7"), CancellationToken.None);
        await using var request = Assert.IsType<McpSessionManager.McpRequestLease>(lease);
        var polling = manager.PollOnceAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
        store.AfterFinish = async token =>
        {
            release.TrySetResult();
            await polling.WaitAsync(token);
        };

        try
        {
            Assert.True(await request.CompleteAsync());
            Assert.False(request.Token.IsCancellationRequested);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [UnitTest]
    public async Task SessionAccess_WithoutActiveStreamOrBridge_DoesNotStartPolling()
    {
        var store = new McpSessionSharedStoreTests.FakeSharedSessionStore(TimeProvider.System);
        await using var manager = new McpSessionManager(store: store);
        var session = (await manager.TryCreateSessionAsync(Alice, false, null, CancellationToken.None))!;
        Assert.True(manager.TryGetReader(session, out _));
        var lifetime = manager.GetLifetimeToken(session);

        await manager.PollOnceAsync(CancellationToken.None);

        Assert.Equal(0, store.Polls);
        Assert.False(lifetime.IsCancellationRequested);
        Assert.Equal(McpSessionValidation.Valid, await manager.ValidateAccessAsync(session, Alice, CancellationToken.None));
    }

    [UnitTest]
    public async Task SessionWatch_LastOwnerLeaves_StopsPollingWithoutTerminatingSession()
    {
        var store = new McpSessionSharedStoreTests.FakeSharedSessionStore(TimeProvider.System);
        await using var manager = new McpSessionManager(store: store);
        var session = (await manager.TryCreateSessionAsync(Alice, false, null, CancellationToken.None))!;
        var lifetime = manager.GetLifetimeToken(session);
        using var first = manager.AcquireSessionWatch(session);
        using var second = manager.AcquireSessionWatch(session);
        first.Dispose();
        await manager.PollOnceAsync(CancellationToken.None);
        Assert.True(store.Polls >= 1);

        second.Dispose();
        second.Dispose();
        var polls = store.Polls;
        await manager.PollOnceAsync(CancellationToken.None);
        Assert.Equal(polls, store.Polls);
        Assert.False(lifetime.IsCancellationRequested);
        Assert.True(manager.TryGetReader(session, out _));
        Assert.Equal(McpSessionValidation.Valid, await manager.ValidateAccessAsync(session, Alice, CancellationToken.None));

        using var reconnect = manager.AcquireSessionWatch(session);
        await manager.PollOnceAsync(CancellationToken.None);
        Assert.True(store.Polls > polls);
    }
}

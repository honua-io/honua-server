// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Ai.Protocols.Mcp;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class McpRequestCancellationTests
{
    [UnitTest]
    public async Task CompleteAsync_StalePollObservesFinishedRequest_PreservesSuccessfulResult()
    {
        var pollEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new McpSessionSharedStoreTests.FakeSharedSessionStore(TimeProvider.System)
        {
            BeforePoll = async token =>
            {
                pollEntered.TrySetResult();
                await releasePoll.Task.WaitAsync(token);
            },
            AfterFinish = async token =>
            {
                finishEntered.TrySetResult();
                await releaseFinish.Task.WaitAsync(token);
            }
        };
        await using var manager = new McpSessionManager(store: store);
        var session = (await manager.TryCreateSessionAsync(Alice, false, null, CancellationToken.None))!;
        var (lease, _) = await manager.StartRequestAsync(session, Alice, Id("1"), CancellationToken.None);
        await using var request = Assert.IsType<McpSessionManager.McpRequestLease>(lease);
        var polling = manager.PollOnceAsync(CancellationToken.None);
        await pollEntered.Task.WaitAsync(TimeSpan.FromSeconds(4));
        var completion = request.CompleteAsync();
        await finishEntered.Task.WaitAsync(TimeSpan.FromSeconds(4));

        releasePoll.SetResult();
        await polling.WaitAsync(TimeSpan.FromSeconds(4));
        releaseFinish.SetResult();

        Assert.True(await completion);
        Assert.False(request.Token.IsCancellationRequested);
    }

    [UnitTest]
    public async Task WatchSession_LastOwnerDisposes_StopsPollingWithoutEndingSession()
    {
        var store = new McpSessionSharedStoreTests.FakeSharedSessionStore(TimeProvider.System);
        await using var manager = new McpSessionManager(store: store);
        var session = (await manager.TryCreateSessionAsync(Alice, false, null, CancellationToken.None))!;
        using var first = Assert.IsAssignableFrom<IDisposable>(manager.AcquireSessionWatch(session));
        using var second = Assert.IsAssignableFrom<IDisposable>(manager.AcquireSessionWatch(session));
        first.Dispose();
        first.Dispose();

        await manager.PollOnceAsync(CancellationToken.None);
        Assert.True(store.Polls > 0);
        second.Dispose();
        var polls = store.Polls;
        await manager.PollOnceAsync(CancellationToken.None);

        Assert.Equal(polls, store.Polls);
        Assert.True(manager.IsValid(session));
        using var resumed = manager.AcquireSessionWatch(session);
        await manager.PollOnceAsync(CancellationToken.None);
        Assert.True(store.Polls > polls);
    }
}

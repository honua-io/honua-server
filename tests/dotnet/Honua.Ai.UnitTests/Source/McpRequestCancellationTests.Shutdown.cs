// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Ai.Protocols.Mcp;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class McpRequestCancellationTests
{
    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_AfterPollBudgetExpires_JoinsDelayedStoreCleanup(bool dispose)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new McpSessionSharedStoreTests.FakeSharedSessionStore(TimeProvider.System)
        {
            BeforePoll = async token =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                finally
                {
                    cancelled.TrySetResult();
                    await release.Task;
                    exited.TrySetResult();
                }
            }
        };
        await using var manager = new McpSessionManager(store: store);
        var session = (await manager.TryCreateSessionAsync(Alice, false, null, CancellationToken.None))!;
        manager.GetLifetimeToken(session);
        var shutdown = Task.CompletedTask;
        try
        {
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(6));
            shutdown = dispose ? manager.DisposeAsync().AsTask() : manager.StopAsync(CancellationToken.None);
            Assert.False(shutdown.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        await shutdown.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.True(exited.Task.IsCompletedSuccessfully);
        Assert.True(store.Contains(session));
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registration_ResumingAfterShutdown_ReturnsAnAlreadyCancelledLease(bool dispose)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new McpSessionSharedStoreTests.FakeSharedSessionStore(TimeProvider.System)
        {
            BeforeRegister = async token =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
            }
        };
        await using var manager = new McpSessionManager(store: store);
        var session = (await manager.TryCreateSessionAsync(Alice, false, null, CancellationToken.None))!;
        var admission = manager.StartRequestAsync(session, Alice, Id("7"), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        try
        {
            if (dispose)
            {
                await manager.DisposeAsync();
            }
            else
            {
                await manager.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            release.TrySetResult();
        }

        var (lease, state) = await admission.WaitAsync(TimeSpan.FromSeconds(1));
        await using var request = Assert.IsType<McpSessionManager.McpRequestLease>(lease);
        Assert.Equal(McpRequestState.Active, state);
        Assert.True(request.Token.IsCancellationRequested);
        Assert.False(await request.CompleteAsync());
        Assert.Equal(0, manager.ActiveRequestCount);
        Assert.True(store.Contains(session));
    }
}

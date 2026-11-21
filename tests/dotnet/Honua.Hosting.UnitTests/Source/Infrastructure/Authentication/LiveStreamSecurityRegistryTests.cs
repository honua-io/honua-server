// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;

namespace Honua.Hosting.UnitTests.Source.Infrastructure.Authentication;

public sealed class LiveStreamSecurityRegistryTests
{
    [UnitTest]
    public async Task PrincipalAndCredentialInvalidationsOnlyRefreshAffectedSubscribers()
    {
        using var registry = new LiveStreamSecurityRegistry();
        var alice = 0;
        var bob = 0;
        using var first = registry.Register(new("alice", new("PortalToken", "a")), _ =>
        {
            Interlocked.Increment(ref alice);
            return Task.CompletedTask;
        });
        using var second = registry.Register(new("bob", new("AdminSession", "a")), _ =>
        {
            Interlocked.Increment(ref bob);
            return Task.CompletedTask;
        });

        await registry.RefreshAsync(new("principal-changed", "alice"), CancellationToken.None);
        Assert.Equal((1, 0), (alice, bob));
        await registry.RefreshAsync(new("credential-revoked", "a", "AdminSession"), CancellationToken.None);
        Assert.Equal((1, 1), (alice, bob));
        await registry.RefreshAsync(new("policy-changed", "*"), CancellationToken.None);
        Assert.Equal((2, 2), (alice, bob));
    }

    [UnitTest]
    public async Task BackingCredentialRevocationClosesDerivedTokenOnly()
    {
        using var registry = new LiveStreamSecurityRegistry();
        var revoked = 0;
        using var derived = registry.Register(new("alice", new("PortalToken", "token"), new("AdminApiKey", "key")),
            _ => Task.CompletedTask, _ => { revoked++; return Task.CompletedTask; });
        using var unrelated = registry.Register(new("bob", new("PortalToken", "other"), new("AdminApiKey", "other-key")),
            _ => Task.CompletedTask, _ => throw new InvalidOperationException("Unrelated stream was revoked."));

        await registry.RefreshAsync(new("credential-revoked", "key", "AdminApiKey"), CancellationToken.None);

        Assert.Equal(1, revoked);
    }

    [UnitTest]
    public async Task RefreshBarrierWaitsForPolicyReplacementAndSerializesBackstop()
    {
        using var registry = new LiveStreamSecurityRegistry();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        using var subscription = registry.Register(new("alice", null), async token =>
        {
            maximum = Math.Max(maximum, Interlocked.Increment(ref active));
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            Interlocked.Decrement(ref active);
        });

        var invalidation = registry.RefreshAsync(new("policy-changed", "*"), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var backstop = registry.RefreshAsync(null, CancellationToken.None);
        Assert.False(invalidation.IsCompleted);
        release.SetResult();
        await Task.WhenAll(invalidation, backstop).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, maximum);
    }

    [UnitTest]
    public async Task DisposedSubscriberIsNotRefreshedAndAnonymousSubscriberReceivesPolicyChanges()
    {
        using var registry = new LiveStreamSecurityRegistry();
        var count = 0;
        var subscription = registry.Register(new(null, null), _ =>
        {
            count++;
            return Task.CompletedTask;
        });
        await registry.RefreshAsync(new("policy-changed", "*"), CancellationToken.None);
        Assert.Equal(1, count);
        subscription.Dispose();
        await registry.RefreshAsync(null, CancellationToken.None);
        Assert.Equal(1, count);
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Threading.Channels;
using Honua.Core.Features.Realtime;
using Honua.Server.Features.Realtime;
using Honua.Infrastructure.Authentication;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Realtime;

public sealed class RealtimeSecurityDispatchTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task EveryTaskConsumesSecurityWithoutPublicSubscribersAndRefreshesCatalog()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        using var firstRegistry = new LiveStreamSecurityRegistry();
        using var secondRegistry = new LiveStreamSecurityRegistry();
        var first = harness.CreateDispatcher(security: firstRegistry);
        var second = harness.CreateDispatcher(security: secondRegistry);
        await first.InitializeAsync(CancellationToken.None);
        await second.InitializeAsync(CancellationToken.None);
        var firstCount = 0;
        var secondCount = 0;
        using var firstStream = firstRegistry.Register(new("alice", null), _ =>
        {
            Interlocked.Increment(ref firstCount);
            return Task.CompletedTask;
        });
        using var secondStream = secondRegistry.Register(new("alice", null), _ =>
        {
            Interlocked.Increment(ref secondCount);
            return Task.CompletedTask;
        });

        await harness.Log.AppendAsync(RealtimeLogFamilies.Security, [new RealtimeLogRow("*", "principal-changed",
            RealtimeLogHarness.Payload(new { kind = "principal-changed", subject = "alice" }))], null, CancellationToken.None);
        await first.DispatchOnceAsync(CancellationToken.None);
        await second.DispatchOnceAsync(CancellationToken.None);
        Assert.Equal((1, 1), (firstCount, secondCount));
        await harness.Log.AppendAsync(RealtimeLogFamilies.Catalog, [new RealtimeLogRow("*", "changed",
            RealtimeLogHarness.Payload(new { }))], null, CancellationToken.None);
        await first.DispatchOnceAsync(CancellationToken.None);
        await second.DispatchOnceAsync(CancellationToken.None);
        Assert.Equal((2, 2), (firstCount, secondCount));
    }

    [IntegrationTest]
    public async Task RecoveryReplayWaitsForCurrentSecurityBeforePublishing()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        using var registry = new LiveStreamSecurityRegistry();
        var dispatcher = harness.CreateDispatcher(security: registry);
        await dispatcher.InitializeAsync(CancellationToken.None);
        await harness.Log.AppendAsync(RealtimeLogFamilies.Admin,
            [new RealtimeLogRow("proposals", "changed", RealtimeLogHarness.Payload(new { }))], null, CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stream = registry.Register(new("alice", null), async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        });
        var channel = Channel.CreateBounded<RealtimeDelivery>(16);
        await using var subscription = (RealtimeSubscription)dispatcher.Subscribe(
            new RealtimeSubscriptionRequest(RealtimeFamily.Admin, [], 0, channel.Writer));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            Assert.False(channel.Reader.TryRead(out _));
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.IsType<RealtimeDelivery.Event>(await channel.Reader.ReadAsync(deadline.Token));
    }

    [IntegrationTest]
    public async Task PublicDispatchWaitsForEarlierSecurityPolicyReplacement()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        using var registry = new LiveStreamSecurityRegistry();
        var dispatcher = harness.CreateDispatcher(security: registry);
        await dispatcher.InitializeAsync(CancellationToken.None);
        var channel = Channel.CreateBounded<RealtimeDelivery>(16);
        await using var subscription = await SubscribeLiveAsync(dispatcher, RealtimeFamily.Admin, channel.Writer);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stream = registry.Register(new("alice", null), async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        });
        await AppendAdminChangeAsync(harness, deadline.Token);
        await harness.Log.AppendAsync(RealtimeLogFamilies.Security, [new RealtimeLogRow("*", "policy-changed",
            RealtimeLogHarness.Payload(new { kind = "policy-changed", subject = "*" }))], null, deadline.Token);
        var dispatch = dispatcher.DispatchOnceAsync(deadline.Token);
        await entered.Task.WaitAsync(deadline.Token);
        try
        {
            Assert.False(dispatch.IsCompleted);
            Assert.False(channel.Reader.TryRead(out _));
        }
        finally
        {
            await AppendAdminChangeAsync(harness, deadline.Token);
            release.TrySetResult();
            await dispatch;
        }
        Assert.IsType<RealtimeDelivery.Event>(await channel.Reader.ReadAsync(deadline.Token));
        Assert.False(channel.Reader.TryRead(out _), "events appended after the captured head belong to the next security barrier");
        await dispatcher.DispatchOnceAsync(deadline.Token);
        Assert.IsType<RealtimeDelivery.Event>(await channel.Reader.ReadAsync(deadline.Token));
    }

    [IntegrationTest]
    public async Task RevokedCredentialClosesDirectlyWithoutWaitingForCredentialStoreVisibility()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        using var registry = new LiveStreamSecurityRegistry();
        var dispatcher = harness.CreateDispatcher(security: registry);
        await dispatcher.InitializeAsync(CancellationToken.None);
        var revoked = false;
        using var stream = registry.Register(new("alice", new("AdminApiKey", "key-id")),
            _ => throw new InvalidOperationException("Revocation must close directly."), _ =>
            {
                revoked = true;
                return Task.CompletedTask;
            });
        await harness.Log.AppendAsync(RealtimeLogFamilies.Security, [new RealtimeLogRow("*", "credential-revoked",
            RealtimeLogHarness.Payload(new { kind = "credential-revoked", subject = "key-id", credentialKind = "AdminApiKey" }))], null, CancellationToken.None);
        await dispatcher.DispatchOnceAsync(CancellationToken.None);
        Assert.True(revoked);
    }

    [IntegrationTest]
    public async Task Dispatch_SourceHeadCommitsRevocation_RefreshesBeforeData()
    {
        await using var harness = await RealtimeLogHarness.CreateAsync(database);
        using var registry = new LiveStreamSecurityRegistry();
        var source = CreateEmptyFeatureSource();
        var dispatcher = new RealtimeDispatcher(harness.Log, [source], Options.Create(new RealtimeOptions()),
            NullLogger<RealtimeDispatcher>.Instance) { Security = registry };
        await dispatcher.InitializeAsync(CancellationToken.None);
        var refreshed = false;
        using var stream = registry.Register(new("alice", null), _ =>
        {
            refreshed = true;
            return Task.CompletedTask;
        });
        source.ReadHeadAsync(Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await harness.Log.AppendAsync(RealtimeLogFamilies.Security, [new RealtimeLogRow("*", "principal-changed",
                RealtimeLogHarness.Payload(new { kind = "principal-changed", subject = "alice" }))], null, CancellationToken.None);
            return 1L;
        });
        // Keep a data subscription present so the dispatcher reads the source.
        var channel = Channel.CreateBounded<RealtimeDelivery>(16);
        await using var subscription = await SubscribeLiveAsync(dispatcher, RealtimeFamily.Features, channel.Writer);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        source.ReadAfterAsync(Arg.Any<long>(), Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Assert.True(refreshed, "source data must follow revocations committed while capturing its head");
            return Task.FromResult<IReadOnlyList<RealtimeEvent>>([]);
        });
        source.ClearReceivedCalls();

        await dispatcher.DispatchOnceAsync(deadline.Token);

        Assert.True(refreshed);
        await source.Received().ReadAfterAsync(Arg.Any<long>(), Arg.Any<IReadOnlyCollection<string>?>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    private static async Task<RealtimeSubscription> SubscribeLiveAsync(
        RealtimeDispatcher dispatcher, RealtimeFamily family, ChannelWriter<RealtimeDelivery> writer)
    {
        var subscription = (RealtimeSubscription)dispatcher.Subscribe(new RealtimeSubscriptionRequest(family, [], null, writer));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!subscription.IsLive)
        {
            await Task.Delay(10, deadline.Token);
        }

        return subscription;
    }

    private static Task AppendAdminChangeAsync(RealtimeLogHarness harness, CancellationToken cancellationToken)
        => harness.Log.AppendAsync(RealtimeLogFamilies.Admin,
            [new RealtimeLogRow("proposals", "changed", RealtimeLogHarness.Payload(new { }))], null, cancellationToken);

    private static IRealtimeFamilySource CreateEmptyFeatureSource()
    {
        var source = Substitute.For<IRealtimeFamilySource>();
        source.Family.Returns(RealtimeFamily.Features);
        source.ReadHeadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(0L));
        source.ReadWindowAsync(Arg.Any<CancellationToken>()).Returns(
            Task.FromResult(new RealtimeLogWindow(RealtimeFamily.Features, 0, 0)));
        return source;
    }
}

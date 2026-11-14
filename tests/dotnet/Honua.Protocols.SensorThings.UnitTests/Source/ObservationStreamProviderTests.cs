// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using System.Text.Json;
using Honua.Core.Features.Realtime;
using Honua.Protocols.SensorThings.Streaming;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

public sealed class ObservationStreamProviderTests
{
    [UnitTest]
    public async Task Subscribe_PreservesFamilyCursorDatastreamAndExactScope()
    {
        var provider = Substitute.For<IRealtimeFanoutProvider>();
        var subscription = Substitute.For<IRealtimeSubscription>();
        RealtimeSubscriptionRequest? request = null;
        provider.Subscribe(Arg.Do<RealtimeSubscriptionRequest>(value => request = value)).Returns(subscription);
        await using var manager = new ObservationStreamSessionManager(
            NullLogger<ObservationStreamSessionManager>.Instance, provider);
        await using var session = manager.TryCreateSession(new ObservationStreamRequest(
            "SSE", 7, new("tenant-a", null), "alice", 42), out _);

        Assert.NotNull(session);
        Assert.NotNull(request);
        Assert.Equal(RealtimeFamily.Observations, request.Family);
        Assert.Equal(42, request.After);
        Assert.Equal(["7"], request.ScopeKeys);
        Assert.NotNull(request.Filter);
        Assert.True(request.Filter(Event("tenant-a", null)));
        Assert.False(request.Filter(Event("tenant-b", null)));
        Assert.False(request.Filter(Event("tenant-a", "public")));
        Assert.False(request.Filter(Event(null, null)));
    }

    [UnitTheory]
    [InlineData("SSE")]
    [InlineData("WebSocket")]
    public async Task CreateSession_WaitsForInitialWindowAndPinsReplayBeforeHandshake(string transport)
    {
        var window = new TaskCompletionSource<RealtimeLogWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new DelayedWindowProvider(window.Task);
        await using var manager = new ObservationStreamSessionManager(
            NullLogger<ObservationStreamSessionManager>.Instance, provider);
        var opening = manager.CreateSessionAsync(new(transport, 7, new("a", null), "alice", null),
            CancellationToken.None).AsTask();
        Assert.False(opening.IsCompleted);
        Assert.Null(provider.Subscribed);
        window.SetResult(new(RealtimeFamily.Observations, 1, 42));
        var (session, rejectedBy) = await opening.WaitAsync(TimeSpan.FromSeconds(5));
        await using var lease = session;
        Assert.NotNull(session);
        Assert.Equal(ObservationStreamAdmissionLimit.None, rejectedBy);
        Assert.NotNull(provider.Subscribed);
        Assert.Equal(42, provider.Subscribed.After);
    }

    [UnitTest]
    public async Task Replay_WhenBufferFills_WaitsForReaderWithoutDisconnecting()
    {
        var provider = Substitute.For<IRealtimeFanoutProvider>();
        RealtimeSubscriptionRequest? request = null;
        provider.Subscribe(Arg.Do<RealtimeSubscriptionRequest>(value => request = value))
            .Returns(Substitute.For<IRealtimeSubscription>());
        await using var manager = new ObservationStreamSessionManager(
            NullLogger<ObservationStreamSessionManager>.Instance, provider,
            new ObservationStreamOptions { MaxBufferPerConnection = 1 });
        await using var session = manager.TryCreateSession(new("SSE", 7, new("a", null), "alice", 0), out _)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = new RealtimeDelivery.Event(Event("a", null));
        var second = new RealtimeDelivery.Event(Event("a", null) with { Sequence = 44 });
        await request!.Writer.WriteAsync(first, timeout.Token);
        var waiting = request.Writer.WriteAsync(second, timeout.Token).AsTask();
        Assert.False(waiting.IsCompleted);
        Assert.False(session.DisconnectToken.IsCancellationRequested);
        Assert.Same(first, await session.Reader.ReadAsync(timeout.Token));
        await waiting;
        Assert.Same(second, await session.Reader.ReadAsync(timeout.Token));
        Assert.Equal(0, manager.SlowConsumerDrops);
        Assert.False(session.DisconnectToken.IsCancellationRequested);
    }

    [UnitTest]
    public async Task Dispose_ReleasesSubscriptionAndAdmissionSlotExactlyOnce()
    {
        var provider = Substitute.For<IRealtimeFanoutProvider>();
        var subscription = Substitute.For<IRealtimeSubscription>();
        provider.Subscribe(Arg.Any<RealtimeSubscriptionRequest>()).Returns(subscription);
        await using var manager = new ObservationStreamSessionManager(
            NullLogger<ObservationStreamSessionManager>.Instance, provider);
        var session = manager.TryCreateSession(new ObservationStreamRequest(
            "SSE", null, new("tenant-a", null), "alice", null), out _)!;
        await session.DisposeAsync();
        await session.DisposeAsync();
        Assert.Equal(0, manager.SessionCount);
        await subscription.Received(1).DisposeAsync();
    }

    [UnitTest]
    public async Task Dispose_WhenOneSubscriptionFails_StillReleasesEverySession()
    {
        var provider = Substitute.For<IRealtimeFanoutProvider>();
        var failing = new FailingSubscription();
        var healthy = Substitute.For<IRealtimeSubscription>();
        provider.Subscribe(Arg.Any<RealtimeSubscriptionRequest>()).Returns(failing, healthy);
        var manager = new ObservationStreamSessionManager(
            NullLogger<ObservationStreamSessionManager>.Instance, provider);
        manager.TryCreateSession(new("SSE", null, new("tenant-a", null), "alice", null), out _);
        manager.TryCreateSession(new("SSE", null, new("tenant-b", null), "bob", null), out _);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.DisposeAsync().AsTask());

        Assert.Equal(0, manager.SessionCount);
        await healthy.Received(1).DisposeAsync();
    }

    [UnitTest]
    public void BatchJson_UsesPublicNamesAndOmitsInternalScope()
    {
        var batch = new ObservationStreamBatch("observations:42", 7,
            JsonSerializer.SerializeToElement(new[] { new { id = 1, result = 2 } }));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(batch,
            ObservationStreamJsonContext.Default.ObservationStreamBatch));
        Assert.Equal("observations:42", json.RootElement.GetProperty("cursor").GetString());
        Assert.Equal(7, json.RootElement.GetProperty("datastreamId").GetInt64());
        Assert.Equal(1, json.RootElement.GetProperty("observations").GetArrayLength());
        Assert.False(json.RootElement.TryGetProperty("scope", out _));
    }

    private sealed class DelayedWindowProvider(Task<RealtimeLogWindow> window) : IRealtimeFanoutProvider
    {
        public RealtimeSubscriptionRequest? Subscribed { get; private set; }

        public RealtimeProviderCapabilities Capabilities => new(true, true, true);

        public ValueTask<RealtimeLogWindow> GetWindowAsync(RealtimeFamily family, CancellationToken cancellationToken)
            => new(window.WaitAsync(cancellationToken));

        public IRealtimeSubscription Subscribe(RealtimeSubscriptionRequest request)
        {
            Subscribed = request;
            return Substitute.For<IRealtimeSubscription>();
        }

        public ValueTask<RealtimePosition> AppendAsync(RealtimeEventDraft draft, DbTransaction? transaction, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public void Enqueue(RealtimeEventDraft draft) => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<RealtimeEvent>> ReadPresencePageAsync(string scopeKey, long after, long through, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<IRealtimeStreamLease?> TryAcquireMcpStreamAsync(string sessionId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FailingSubscription : IRealtimeSubscription
    {
        public RealtimeFamily Family => RealtimeFamily.Observations;

        public long Position => 0;

        public ValueTask DisposeAsync() => ValueTask.FromException(new InvalidOperationException("dispose failed"));
    }

    private static RealtimeEvent Event(string? tenant, string? schema)
    {
        var payload = JsonSerializer.SerializeToElement(new { scope = new { tenantId = tenant, schema } });
        return new(RealtimeFamily.Observations, 43, "7", "observation.created", payload, DateTimeOffset.UtcNow);
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Events;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Streaming;

/// <summary>
/// Unit tests for the broker-agnostic feature-change event sink broadcaster (#357),
/// verifying fan-out, the disabled/no-sink defaults, and per-sink failure isolation.
/// </summary>
public sealed class FeatureChangeEventSinkBroadcasterTests
{
    private static FeatureChangeEvent SampleEvent(string eventId = "evt-1") => new()
    {
        EventId = eventId,
        Cursor = 1,
        Timestamp = DateTimeOffset.UtcNow,
        ServiceId = "svc",
        LayerId = 0,
        ObjectId = 7,
        Operation = "insert",
        Protocol = "rest",
        RequestId = "req-1"
    };

    private static FeatureChangeEventSinkBroadcaster Create(
        bool enabled,
        params IFeatureChangeEventSink[] sinks)
        => new(
            sinks,
            Options.Create(new FeatureChangeEventSinkOptions { Enabled = enabled }),
            NullLogger<FeatureChangeEventSinkBroadcaster>.Instance);

    [UnitTest]
    public async Task DispatchAndWaitAsync_WhenEnabled_PublishesToEverySink()
    {
        var first = new RecordingSink("first");
        var second = new RecordingSink("second");
        var broadcaster = Create(enabled: true, first, second);

        Assert.True(broadcaster.HasActiveSinks);

        await broadcaster.DispatchAndWaitAsync(SampleEvent());

        Assert.Single(first.Received);
        Assert.Single(second.Received);
        Assert.Equal("evt-1", first.Received[0]);
    }

    [UnitTest]
    public async Task DispatchAndWaitAsync_WhenDisabled_DoesNotPublish()
    {
        var sink = new RecordingSink("noop-default");
        var broadcaster = Create(enabled: false, sink);

        Assert.False(broadcaster.HasActiveSinks);

        await broadcaster.DispatchAndWaitAsync(SampleEvent());

        Assert.Empty(sink.Received);
    }

    [UnitTest]
    public async Task DispatchAndWaitAsync_WhenNoSinksRegistered_IsNoOp()
    {
        var broadcaster = Create(enabled: true);

        Assert.False(broadcaster.HasActiveSinks);

        // Must complete without throwing even though no sinks are present.
        await broadcaster.DispatchAndWaitAsync(SampleEvent());
    }

    [UnitTest]
    public async Task DispatchAndWaitAsync_WhenSinkThrows_IsolatesFailureFromSiblings()
    {
        var failing = new ThrowingSink("kafka");
        var healthy = new RecordingSink("nats");
        var broadcaster = Create(enabled: true, failing, healthy);

        // The failing sink must not prevent the healthy sink from receiving the event,
        // and the failure must not propagate to the caller (the write path).
        await broadcaster.DispatchAndWaitAsync(SampleEvent());

        Assert.Single(healthy.Received);
    }

    [UnitTest]
    public void Dispatch_WhenDisabled_IsNoOpAndDoesNotThrow()
    {
        var sink = new RecordingSink("noop");
        var broadcaster = Create(enabled: false, sink);

        broadcaster.Dispatch(SampleEvent());

        Assert.Empty(sink.Received);
    }

    private sealed class RecordingSink(string name) : IFeatureChangeEventSink
    {
        public string Name { get; } = name;
        public List<string> Received { get; } = [];

        public Task PublishAsync(FeatureChangeEvent featureEvent, CancellationToken cancellationToken = default)
        {
            Received.Add(featureEvent.EventId);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingSink(string name) : IFeatureChangeEventSink
    {
        public string Name { get; } = name;

        public Task PublishAsync(FeatureChangeEvent featureEvent, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("sink transport unavailable");
    }
}

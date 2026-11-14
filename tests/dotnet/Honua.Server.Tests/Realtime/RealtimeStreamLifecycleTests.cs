// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.Metrics;
using FluentAssertions;
using Honua.Core.Features.Realtime;
using Honua.Server.Features.Realtime;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Realtime;

public sealed class RealtimeStreamLifecycleTests
{
    [UnitTest]
    public async Task BeginDrain_ActiveStreams_RefusesAdmissionAndSignalsClose()
    {
        var lifecycle = CreateLifecycle();
        using var session = lifecycle.TryOpen(new(RealtimeFamily.Features, "sse", () => { }));
        session.Should().NotBeNull();
        session!.RetryMilliseconds.Should().BeInRange(1000, 5000);

        lifecycle.BeginDrain();

        lifecycle.IsDraining.Should().BeTrue();
        lifecycle.TryOpen(new(RealtimeFamily.Features, "sse", () => { })).Should().BeNull();
        await Task.Delay(Timeout.InfiniteTimeSpan, session.DrainingToken)
            .ContinueWith(task => task.IsCanceled.Should().BeTrue(), TaskScheduler.Default)
            .WaitAsync(TimeSpan.FromSeconds(5));
        session.Dispose();
        await lifecycle.DrainAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [UnitTest]
    public async Task DrainAsync_UncooperativeStream_AbortsAtDeadline()
    {
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifecycle = CreateLifecycle();
        using var session = lifecycle.TryOpen(new(RealtimeFamily.Mcp, "sse", () => aborted.TrySetResult()));

        await lifecycle.DrainAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        aborted.Task.IsCompletedSuccessfully.Should().BeTrue();
    }

    [UnitTest]
    public async Task DrainAsync_ReleasedStream_DoesNotAbort()
    {
        var aborted = false;
        var lifecycle = CreateLifecycle();
        var session = lifecycle.TryOpen(new(RealtimeFamily.Alerts, "websocket", () => aborted = true));
        session!.Dispose();

        await lifecycle.DrainAsync(CancellationToken.None);

        aborted.Should().BeFalse();
    }

    [UnitTest]
    public async Task DrainAsync_HostDeadlineExpires_AbortsBeforeReturning()
    {
        var aborted = false;
        var lifecycle = new RealtimeStreamLifecycle(TimeProvider.System, Options.Create(new RealtimeOptions
        {
            ShutdownDrain = TimeSpan.FromSeconds(20)
        }));
        using var session = lifecycle.TryOpen(new(RealtimeFamily.Mcp, "sse", () => aborted = true));
        using var hostDeadline = new CancellationTokenSource();
        hostDeadline.Cancel();

        Func<Task> drain = () => lifecycle.DrainAsync(hostDeadline.Token);

        await drain.Should().ThrowAsync<OperationCanceledException>();
        aborted.Should().BeTrue("the listener can stop as soon as the caller returns");
        session!.Dispose();
        await lifecycle.DrainAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [UnitTest]
    public async Task DrainAsync_AbortInProgress_DisposalWaitsForTransportCallback()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var lifecycle = CreateLifecycle();
        var session = lifecycle.TryOpen(new(RealtimeFamily.Alerts, "websocket", () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        }));
        var drain = lifecycle.DrainAsync(CancellationToken.None);
        Task? disposal = null;
        try
        {
            (await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5)))).Should().BeTrue();
            var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = Task.Run(() => { attempted.SetResult(); session!.Dispose(); });
            await attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(50);
            disposal.IsCompleted.Should().BeFalse("disposal must wait for an active transport callback");
        }
        finally
        {
            release.Set();
            if (disposal is not null) { await disposal.WaitAsync(TimeSpan.FromSeconds(5)); }
            session!.Dispose();
            await drain.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [UnitTest]
    public void Session_DisposedTwice_RecordsBalancedSubscriberCount()
    {
        var transport = Guid.NewGuid().ToString("N");
        var changes = new List<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, active) =>
        {
            if (instrument.Name == "honua.realtime.subscribers")
            {
                active.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "transport" && Equals(tag.Value, transport))
                {
                    changes.Add(value);
                }
            }
        });
        listener.Start();
        var lifecycle = CreateLifecycle();
        var session = lifecycle.TryOpen(new(RealtimeFamily.Features, transport, () => { }));

        session!.Dispose();
        session.Dispose();

        changes.Should().Equal(1, -1);
    }

    [UnitTest]
    public void Session_FlushedFrame_RecordsLatencyAndResetWithTransportTags()
    {
        var transport = Guid.NewGuid().ToString("N");
        double? latency = null;
        var resets = new List<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, active) =>
        {
            if (instrument.Name is "honua.realtime.delivery.latency" or "honua.realtime.resets")
            {
                active.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            if (HasTransportTag(tags, transport))
            {
                latency = value;
            }
        });
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            if (HasTransportTag(tags, transport))
            {
                resets.Add(value);
            }
        });
        listener.Start();
        using var session = CreateLifecycle().TryOpen(new(RealtimeFamily.Features, transport, () => { }));

        session!.RecordDelivery(DateTimeOffset.UtcNow.AddSeconds(-2));
        session.RecordReset(RealtimeResetReasons.Retention);

        latency.Should().NotBeNull();
        latency!.Value.Should().BeInRange(1.9, 10);
        resets.Should().Equal(1);
    }

    private static bool HasTransportTag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string transport)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == "transport" && Equals(tag.Value, transport))
            {
                return true;
            }
        }
        return false;
    }

    private static RealtimeStreamLifecycle CreateLifecycle()
        => new(TimeProvider.System, Options.Create(new RealtimeOptions
        {
            ShutdownDrain = TimeSpan.FromMilliseconds(100)
        }));
}

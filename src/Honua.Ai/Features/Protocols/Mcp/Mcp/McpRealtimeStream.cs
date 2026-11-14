// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Honua.Core.Features.Realtime;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp;

internal static class McpRealtimeStream
{
    internal static bool TryParsePosition(string value, out long? after)
    {
        after = null;
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (!value.StartsWith("mcp:", StringComparison.Ordinal) ||
            !long.TryParse(value.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
        {
            return false;
        }

        after = sequence;
        return true;
    }

    internal static async Task PumpAsync(
        Stream output,
        ChannelReader<string> local,
        RealtimeStreamSubscription subscription,
        CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, subscription.Closed, subscription.Lease.LostToken);
        var frames = Channel.CreateBounded<string>(256);
        var localPump = CopyLocalAsync(local, frames.Writer, lifetime.Token);
        var remotePump = CopyRealtimeAsync(subscription.Reader, frames.Writer, lifetime.Token);
        var heartbeatPump = HeartbeatAsync(frames.Writer, lifetime.Token);
        try
        {
            await foreach (var frame in frames.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                if (!subscription.Lease.IsHeld)
                {
                    return;
                }
                await output.WriteAsync(Encoding.UTF8.GetBytes(frame), lifetime.Token).ConfigureAwait(false);
                await output.FlushAsync(lifetime.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(localPump, remotePump, heartbeatPump).ConfigureAwait(false);
        }
    }

    private static async Task HeartbeatAsync(ChannelWriter<string> writer, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                await writer.WriteAsync(": hb\n\n", token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException)
        {
        }
    }

    private static async Task CopyLocalAsync(ChannelReader<string> reader, ChannelWriter<string> writer, CancellationToken token)
    {
        try
        {
            await foreach (var payload in reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                await writer.WriteAsync($"event: message\ndata: {payload}\n\n", token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException)
        {
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private static async Task CopyRealtimeAsync(ChannelReader<RealtimeDelivery> reader, ChannelWriter<string> writer, CancellationToken token)
    {
        try
        {
            await foreach (var delivery in reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                var frame = delivery switch
                {
                    RealtimeDelivery.Event item => $"id: {item.Value.Position}\nevent: message\ndata: {item.Value.Payload.GetRawText()}\n\n",
                    RealtimeDelivery.Reset reset => $"event: reset\ndata: {{\"reason\":{JsonSerializer.Serialize(reset.Reason, McpJsonContext.Default.String)},\"head\":{reset.Head.ToString(CultureInfo.InvariantCulture)}}}\n\n",
                    _ => ": hb\n\n"
                };
                await writer.WriteAsync(frame, token).ConfigureAwait(false);
                if (delivery is RealtimeDelivery.Reset)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException)
        {
        }
        finally
        {
            writer.TryComplete();
        }
    }
}

internal sealed class RealtimeStreamSubscription : IAsyncDisposable
{
    private readonly CancellationTokenSource _closed = new();
    private readonly IRealtimeSubscription _subscription;
    private readonly Channel<RealtimeDelivery> _channel = Channel.CreateBounded<RealtimeDelivery>(256);

    internal RealtimeStreamSubscription(IRealtimeFanoutProvider provider, string sessionId, long after, IRealtimeStreamLease lease)
    {
        Lease = lease;
        _subscription = provider.Subscribe(new RealtimeSubscriptionRequest(
            RealtimeFamily.Mcp, [sessionId, "*"], after, new ClosingWriter(_channel.Writer, _closed)));
    }

    internal IRealtimeStreamLease Lease { get; }
    internal ChannelReader<RealtimeDelivery> Reader => _channel.Reader;
    internal CancellationToken Closed => _closed.Token;

    public async ValueTask DisposeAsync()
    {
        await _subscription.DisposeAsync().ConfigureAwait(false);
        _closed.Dispose();
    }

    private sealed class ClosingWriter(ChannelWriter<RealtimeDelivery> writer, CancellationTokenSource closed) : ChannelWriter<RealtimeDelivery>
    {
        public override bool TryComplete(Exception? error = null)
        {
            var completed = writer.TryComplete(error);
            if (error is not null)
            {
                closed.Cancel();
            }
            return completed;
        }

        public override bool TryWrite(RealtimeDelivery item) => writer.TryWrite(item);

        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) => writer.WaitToWriteAsync(cancellationToken);
    }
}

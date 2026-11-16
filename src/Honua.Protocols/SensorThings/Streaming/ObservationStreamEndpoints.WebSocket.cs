// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Honua.Core.Features.Realtime;

namespace Honua.Protocols.SensorThings.Streaming;

internal static partial class ObservationStreamEndpoints
{
    private static readonly TimeSpan CloseWriteBudget = TimeSpan.FromSeconds(2);

    private static async Task HandleWebSocketAsync(
        HttpContext context,
        ObservationStreamSession session,
        long? datastreamId)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted, session.DisconnectToken);
        using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var receiveTask = ReceiveUntilCloseAsync(socket, sendCts, receiveCts.Token);
        try
        {
            await SendObservationFramesUntilEndedAsync(SendFramesAsync(), sendCts.Token).ConfigureAwait(false);

            // Only this handler writes to the socket. Finish delivery before acknowledging Close.
            if (socket.State == WebSocketState.CloseReceived)
            {
                await AcknowledgeCloseAsync(context, socket).ConfigureAwait(false);
            }
            else if (socket.State == WebSocketState.Open)
            {
                await WriteSlowConsumerGapAsync(context, session, datastreamId, socket).ConfigureAwait(false);
            }
        }
        finally
        {
            // Cancelling a pending receive can abort the socket, so do it after terminal writes.
            receiveCts.Cancel();
            await receiveTask.ConfigureAwait(false);
        }

        async Task SendFramesAsync()
        {
            await SendSocketAsync(socket,
                JsonSerializer.Serialize(ObservationStreamStatus.Connected(datastreamId),
                    ObservationStreamJsonContext.Default.ObservationStreamStatus),
                sendCts.Token).ConfigureAwait(false);
            await SendObservationFramesAsync(socket, session, sendCts.Token).ConfigureAwait(false);
        }
    }

    private static async Task SendObservationFramesUntilEndedAsync(
        Task sendTask, CancellationToken ct)
    {
        try
        {
            await sendTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A peer close, session disconnect or request cancellation stops delivery.
        }
        catch (ChannelClosedException)
        {
            // Provider closes the subscription when delivery cannot continue.
        }
        catch (WebSocketException)
        {
            // The peer can disconnect while delivery is in progress.
        }
    }

    private static async Task ReceiveUntilCloseAsync(
        WebSocket socket, CancellationTokenSource sendCts, CancellationToken cancellationToken)
    {
        // Client data is unused; discard it in bounded chunks while still processing control frames.
        var buffer = new byte[256];
        try
        {
            while (socket.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                var received = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
            // The peer can disconnect without a close handshake.
        }
        finally
        {
            sendCts.Cancel();
        }
    }

    private static async Task AcknowledgeCloseAsync(HttpContext context, WebSocket socket)
    {
        using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        closeCts.CancelAfter(CloseWriteBudget);
        try
        {
            await socket.CloseOutputAsync(
                socket.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                socket.CloseStatusDescription,
                closeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (closeCts.IsCancellationRequested)
        {
            AbortTransport(context, socket);
        }
        catch (WebSocketException)
        {
            AbortTransport(context, socket);
        }
    }

    private static async Task SendObservationFramesAsync(
        WebSocket socket, ObservationStreamSession session, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var readTask = session.Reader.WaitToReadAsync(readCts.Token).AsTask();
            var timeoutTask = Task.Delay(HeartbeatInterval, readCts.Token);
            var completed = await Task.WhenAny(readTask, timeoutTask).ConfigureAwait(false);
            readCts.Cancel();

            if (completed == timeoutTask)
            {
                await SwallowAsync(readTask).ConfigureAwait(false);
                await SendSocketAsync(socket, "{\"heartbeat\":true}", ct).ConfigureAwait(false);
                continue;
            }

            await SwallowAsync(timeoutTask).ConfigureAwait(false);
            if (!await readTask.ConfigureAwait(false))
            {
                break;
            }

            while (session.Reader.TryRead(out var frame))
            {
                var message = FormatDelivery(frame);
                await SendSocketAsync(socket, message.Json, ct).ConfigureAwait(false);
                if (frame is RealtimeDelivery.Reset) return;
            }
        }
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Honua.Core.Features.Realtime;
using Honua.Infrastructure.Realtime;

namespace Honua.Protocols.SensorThings.Streaming;

internal static partial class ObservationStreamEndpoints
{
    internal static async Task HandleWebSocketAsync(
        HttpContext context,
        ObservationStreamSession session,
        long? datastreamId)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        using var cancellation = new WebSocketDeliveryCancellation(context, session);
        var receiveTask = ReceiveUntilCloseAsync(socket, cancellation);
        try
        {
            await SendObservationFramesUntilEndedAsync(SendFramesAsync(), cancellation.Delivery.Token).ConfigureAwait(false);

            // Finish delivery before acknowledging Close; the authorization wrapper serializes terminal writes.
            if (socket.State == WebSocketState.CloseReceived)
            {
                await AcknowledgeCloseAsync(context, socket).ConfigureAwait(false);
            }
            else if (socket.State == WebSocketState.Open)
            {
                await WriteSlowConsumerGapAsync(context, session, datastreamId, socket).ConfigureAwait(false);
                await CloseForServiceRestartAsync(context, socket, session).ConfigureAwait(false);
            }
        }
        finally
        {
            await FinishReceiveAsync(socket, receiveTask, cancellation.Receive).ConfigureAwait(false);
        }

        async Task SendFramesAsync()
        {
            await SendSocketAsync(socket,
                JsonSerializer.Serialize(ObservationStreamStatus.Connected(datastreamId),
                    ObservationStreamJsonContext.Default.ObservationStreamStatus),
                cancellation.Write.Token).ConfigureAwait(false);
            await SendObservationFramesAsync(socket, session, cancellation).ConfigureAwait(false);
        }
    }

    private static async Task FinishReceiveAsync(WebSocket socket, Task receiveTask, CancellationTokenSource receive)
    {
        try
        {
            if (socket.State == WebSocketState.CloseSent)
            {
                // Keep receiving the peer's reply before cancellation can abort the handshake.
                await receiveTask.WaitAsync(TransportWriteBudget, receive.Token).ConfigureAwait(false);
            }
        }
        catch (TimeoutException)
        {
            // A peer that never replies cannot retain the session indefinitely.
        }
        catch (OperationCanceledException) when (receive.IsCancellationRequested)
        {
            // Request cancellation also ends the bounded handshake wait.
        }
        finally
        {
            receive.Cancel();
            await receiveTask.ConfigureAwait(false);
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
        WebSocket socket, WebSocketDeliveryCancellation cancellation)
    {
        var cancellationToken = cancellation.Receive.Token;
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
            // The handler stops the pending receive after completing its terminal write.
        }
        catch (WebSocketException)
        {
            // The peer can disconnect without a close handshake.
        }
        finally
        {
            // Stop reads immediately, but let an active transport write finish before Close.
            // Cancelling that write immediately would abort a managed WebSocket.
            cancellation.Delivery.Cancel();
            cancellation.Write.CancelAfter(TransportWriteBudget);
        }
    }

    // The drain signal ends delivery; 1012 tells the client to reconnect, normally to another task.
    private static async Task CloseForServiceRestartAsync(HttpContext context, WebSocket socket, ObservationStreamSession session)
    {
        if (socket.State != WebSocketState.Open || !session.DrainingToken.IsCancellationRequested
            || session.DisconnectReason == ObservationStreamDisconnectReason.SlowConsumer)
        {
            return;
        }

        using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        closeCts.CancelAfter(TransportWriteBudget);
        try
        {
            await socket.CloseOutputAsync(RealtimeStreamAdmission.ServiceRestart,
                RealtimeStreamAdmission.ServiceRestartDescription, closeCts.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
        {
            AbortTransport(context, socket);
        }
    }

    private static async Task AcknowledgeCloseAsync(HttpContext context, WebSocket socket)
    {
        using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        closeCts.CancelAfter(TransportWriteBudget);
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
        WebSocket socket, ObservationStreamSession session, WebSocketDeliveryCancellation cancellation)
    {
        var ct = cancellation.Delivery.Token;
        var writeToken = cancellation.Write.Token;
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
                ct.ThrowIfCancellationRequested();
                await SendSocketAsync(socket, "{\"heartbeat\":true}", writeToken).ConfigureAwait(false);
                continue;
            }

            await SwallowAsync(timeoutTask).ConfigureAwait(false);
            if (!await readTask.ConfigureAwait(false))
            {
                break;
            }

            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open
                && session.Reader.TryRead(out var frame))
            {
                var message = FormatDelivery(frame);
                await SendSocketAsync(socket, message.Json, writeToken).ConfigureAwait(false);
                if (RecordDeliveredFrame(session, frame)) return;
            }
        }
    }

    private sealed class WebSocketDeliveryCancellation(HttpContext context, ObservationStreamSession session) : IDisposable
    {
        // The drain signal stops delivery only; an in-flight write finishes before the 1012 close.
        internal CancellationTokenSource Delivery { get; } = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted, session.DisconnectToken, session.DrainingToken);
        internal CancellationTokenSource Write { get; } = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted, session.DisconnectToken);
        internal CancellationTokenSource Receive { get; } = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);

        public void Dispose()
        {
            Receive.Dispose();
            Write.Dispose();
            Delivery.Dispose();
        }
    }
}

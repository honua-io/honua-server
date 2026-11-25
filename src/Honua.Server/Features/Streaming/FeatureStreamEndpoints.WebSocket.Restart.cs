// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.WebSockets;
using Honua.Infrastructure.Realtime;

namespace Honua.Server.Features.Streaming;

internal static partial class FeatureStreamEndpoints
{
    // A drain signalled before the upgrade is refused like a new stream instead of accepted and closed.
    private static async Task<WebSocket?> AcceptUnlessRestartingAsync(HttpContext context, FeatureStreamSession session)
    {
        if (session.ServiceRestart)
        {
            RealtimeStreamAdmission.WriteDraining(context.Response);
            return null;
        }

        return await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
    }

    // On the drain signal, send 1012 between frames and keep receiving: the peer's Close reply ends
    // the receive loop normally. A peer that never replies is aborted at the lifecycle deadline.
    private static async Task CloseOnServiceRestartAsync(
        WebSocket webSocket, FeatureStreamSession session, CancellationToken cancellationToken)
    {
        using var wake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.ServiceRestartToken);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, wake.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (wake.IsCancellationRequested)
        {
            // Either the restart signal or the end of the stream.
        }

        if (!session.ServiceRestart || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await session.WriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (webSocket.State == WebSocketState.Open)
                {
                    await webSocket.CloseOutputAsync(RealtimeStreamAdmission.ServiceRestart,
                        RealtimeStreamAdmission.ServiceRestartDescription, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                session.WriteLock.Release();
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
        {
            // The stream ended or the socket failed before the close could be sent.
        }
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.WebSockets;
using Honua.Core.Features.Realtime;
using Honua.Infrastructure.Realtime;

namespace Honua.Server.Features.Streaming;

internal static partial class FeatureStreamEndpoints
{
    private sealed class WebSocketSessionTeardown
    {
        public required HttpContext Context { get; init; }
        public required WebSocket WebSocket { get; init; }
        public required FeatureStreamSession Session { get; init; }
        public required CancellationTokenSource LinkedCts { get; init; }
        public required Task WriterTask { get; init; }
        public required Task RestartClose { get; init; }
        public required ILogger Logger { get; init; }
    }

    private static async Task TeardownWebSocketSessionAsync(WebSocketSessionTeardown teardown)
    {
        await teardown.LinkedCts.CancelAsync().ConfigureAwait(false);
        await DrainTaskIgnoringFaultsAsync(teardown.WriterTask, teardown.Logger, teardown.Session.SessionId).ConfigureAwait(false);
        await DrainTaskIgnoringFaultsAsync(teardown.RestartClose, teardown.Logger, teardown.Session.SessionId).ConfigureAwait(false);
        await CloseWebSocketOutputBoundedAsync(teardown).ConfigureAwait(false);
    }

    private static async Task DrainTaskIgnoringFaultsAsync(Task task, ILogger logger, Guid sessionId)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FeatureStreamLog.WebSocketCloseFailed(logger, ex, sessionId);
        }
    }

    private static async Task CloseWebSocketOutputBoundedAsync(WebSocketSessionTeardown teardown)
    {
        if (teardown.WebSocket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(teardown.Context.RequestAborted);
        closeCts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var (status, description) = teardown.Session.ServiceRestart
                ? (RealtimeStreamAdmission.ServiceRestart, RealtimeStreamAdmission.ServiceRestartDescription)
                : (WebSocketCloseStatus.NormalClosure,
                    teardown.Session.DisconnectToken.IsCancellationRequested ? "Session disconnected." : "Stream closed.");
            await teardown.WebSocket.CloseOutputAsync(status, description, closeCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException or IOException)
        {
            FeatureStreamLog.WebSocketCloseFailed(teardown.Logger, ex, teardown.Session.SessionId);
            teardown.WebSocket.Abort();
        }
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Streaming;

/// <summary>
/// Feature-change streaming endpoints supporting WebSocket and SSE transports
/// on a single logical route. Admin endpoints expose session visibility.
/// </summary>
internal static class FeatureStreamEndpoints
{
    private const string WebSocketTransport = "WebSocket";
    private const string SseTransport = "SSE";

    public static void MapFeatureStreamEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Feature-change events are global and include replay, so only admins can subscribe.
        var streamGroup = endpoints.MapGroup("/api/v{version:apiVersion}/streaming")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Streaming")
            .RequireAdminAuthorization();

        streamGroup.MapGet("/features", HandleFeatureStream)
            .WithDisplayName("Stream Feature Changes")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .WithDescription("Opens a WebSocket or SSE stream of real-time feature-change events. " +
                             "WebSocket: send Upgrade header. SSE: send Accept: text/event-stream.")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        // Admin endpoints for session visibility
        var adminGroup = endpoints.MapGroup("/api/v{version:apiVersion}/admin/streaming/features")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Streaming")
            .RequireAdminAuthorization();

        adminGroup.MapGet("/sessions", HandleListSessions)
            .WithDisplayName("List Feature Stream Sessions")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .Produces<ApiResponse<FeatureStreamStatusResponse>>();

        adminGroup.MapDelete("/sessions/{sessionId:guid}", HandleDisconnectSession)
            .WithDisplayName("Disconnect Feature Stream Session")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Delete]))
            .Produces<ApiResponse<object>>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> HandleFeatureStream(
        [FromServices] FeatureStreamSessionManager sessionManager,
        [FromServices] IFeatureChangeEventStore eventStore,
        [FromServices] IOptions<FeatureStreamOptions> options,
        ILogger<FeatureStreamEndpointsLog> logger,
        HttpContext context)
    {
        // Determine transport from request headers.
        if (context.WebSockets.IsWebSocketRequest)
        {
            await HandleWebSocketStream(sessionManager, eventStore, options.Value, logger, context).ConfigureAwait(false);
            return Results.Empty;
        }

        var accept = context.Request.Headers.Accept.ToString();
        if (accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            await HandleSseStream(sessionManager, eventStore, options.Value, logger, context).ConfigureAwait(false);
            return Results.Empty;
        }

        return ProblemDetailsHelpers.CreateAdminProblem(
            context,
            StatusCodes.Status400BadRequest,
            "WebSocket upgrade or Accept: text/event-stream header required.");
    }

    private static async Task HandleWebSocketStream(
        FeatureStreamSessionManager sessionManager,
        IFeatureChangeEventStore eventStore,
        FeatureStreamOptions options,
        ILogger logger,
        HttpContext context)
    {
        using var webSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);

        var clientLabel = context.Request.Query["clientLabel"].ToString();
        var cursorParam = context.Request.Query["cursor"].ToString();
        long? cursor = long.TryParse(cursorParam, CultureInfo.InvariantCulture, out var c) ? c : null;

        using var session = sessionManager.CreateSession(WebSocketTransport, NullIfEmpty(clientLabel));

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted, session.DisconnectToken);

        // Replay missed events directly to the WebSocket, bypassing the bounded channel
        // so that large replay backlogs are not truncated by the buffer limit.
        // Live broadcasts flow into the channel concurrently; the drain deduplicates
        // using the replay cursor so events are delivered exactly once.
        bool hasReplay = cursor.HasValue;
        long replayCursor = 0;
        if (hasReplay)
        {
            try
            {
                replayCursor = await ReplayToWebSocketAsync(webSocket, eventStore, cursor!.Value, options.ReplayBatchSize, logger, session.SessionId, linkedCts.Token).ConfigureAwait(false);

                // Catch-up: replay events published during the main replay window that
                // were silently dropped from the bounded channel pre-drain.
                replayCursor = await ReplayToWebSocketAsync(webSocket, eventStore, replayCursor, options.ReplayBatchSize, logger, session.SessionId, linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linkedCts.Token.IsCancellationRequested)
            {
                return; // Admin disconnect, slow-consumer removal, or request aborted during replay.
            }
            catch (WebSocketException)
            {
                return; // Client disconnected during replay.
            }
        }

        // Activate drain with buffer-sized grace for replay sessions so concurrent
        // overflows during the handoff are absorbed instead of disconnecting.
        sessionManager.MarkDrainStarted(session.SessionId,
            hasReplay ? options.MaxBufferPerConnection : 0);

        if (!hasReplay)
        {
            // Fresh live stream — no replay path, nothing to recover.
            sessionManager.ClearDrainGrace(session.SessionId);
        }

        // Convergent handoff: alternately drain the channel and sweep the store
        // until both are simultaneously empty.  Each TryRead pass creates headroom
        // for concurrent broadcasts; each store sweep recovers grace-dropped events.
        // The loop exits only when the channel is empty AND the store has no new
        // events, so ClearDrainGrace runs with an empty channel.
        try
        {
            if (hasReplay)
            {
                long previousCursor;
                do
                {
                    // Drain channel for headroom only — the store sweep below
                    // delivers everything in cursor order including grace-drops.
                    while (session.Reader.TryRead(out _)) { }

                    previousCursor = replayCursor;
                    replayCursor = await ReplayToWebSocketAsync(webSocket, eventStore, replayCursor, options.ReplayBatchSize, logger, session.SessionId, linkedCts.Token).ConfigureAwait(false);
                } while (replayCursor > previousCursor || session.Reader.TryPeek(out _));
            }
        }
        catch (OperationCanceledException) when (linkedCts.Token.IsCancellationRequested)
        {
            return; // Client disconnected during handoff.
        }
        catch (WebSocketException)
        {
            return; // Client disconnected during handoff.
        }

        // The final handoff runs inside the drain task on its first iteration.
        // It alternates draining the channel (creating headroom) and sweeping
        // the store (recovering grace-drops) until both are quiescent, then
        // clears grace so subsequent overflows are genuine slow consumers.
        // For fresh live sessions (no cursor), onFirstRead is null and the first
        // message flows straight to normal delivery.
        var writerTask = WriteWebSocketAsync(webSocket, session, replayCursor, linkedCts.Token,
            onFirstRead: hasReplay
                ? async (cursor, ct) =>
                {
                    bool progress;
                    do
                    {
                        progress = false;

                        // Drain channel for headroom only — the store sweep below
                        // delivers everything in cursor order including grace-drops.
                        while (session.Reader.TryRead(out _)) { }

                        long prev = cursor;
                        cursor = await ReplayToWebSocketAsync(webSocket, eventStore, cursor, options.ReplayBatchSize, logger, session.SessionId, ct).ConfigureAwait(false);
                        if (cursor > prev)
                        {
                            progress = true;
                        }
                    } while (progress || session.Reader.TryPeek(out _));

                    sessionManager.ClearDrainGrace(session.SessionId);
                    return cursor;
                }
        : null);

        // Receive loop keeps the connection alive and detects client close.
        var buffer = new byte[1];
        try
        {
            while (webSocket.State == WebSocketState.Open && !linkedCts.Token.IsCancellationRequested)
            {
                var result = await webSocket.ReceiveAsync(buffer, linkedCts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (linkedCts.Token.IsCancellationRequested)
        {
            // Normal shutdown or disconnect.
        }
        catch (WebSocketException)
        {
            // Client disconnected abruptly.
        }

        // Signal writer to stop and wait for it.
        await linkedCts.CancelAsync().ConfigureAwait(false);
        await writerTask.ConfigureAwait(false);

        // Graceful close.
        if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    session.DisconnectToken.IsCancellationRequested ? "Session disconnected." : "Stream closed.",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (WebSocketException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private static async Task WriteWebSocketAsync(
        WebSocket webSocket,
        FeatureStreamSession session,
        long replayCursor,
        CancellationToken cancellationToken,
        Func<long, CancellationToken, Task<long>>? onFirstRead = null)
    {
        try
        {
            await foreach (var message in session.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                // First dequeue proves the drain is active.  Run the final store
                // sweep and clear grace while the reader keeps creating headroom.
                if (onFirstRead is not null)
                {
                    replayCursor = await onFirstRead(replayCursor, cancellationToken).ConfigureAwait(false);
                    onFirstRead = null;
                }

                if (webSocket.State != WebSocketState.Open)
                {
                    break;
                }

                // Skip events already delivered during replay to prevent duplicate delivery.
                if (!message.IsHeartbeat && replayCursor > 0 && message.Envelope.Cursor <= replayCursor)
                {
                    continue;
                }

                var payload = message.IsHeartbeat
                    ? JsonSerializer.SerializeToUtf8Bytes(
                        new FeatureStreamHeartbeat(),
                        FeatureStreamJsonContext.Default.FeatureStreamHeartbeat)
                    : JsonSerializer.SerializeToUtf8Bytes(
                        message.Envelope,
                        FeatureStreamJsonContext.Default.FeatureStreamEnvelope);

                await webSocket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (WebSocketException)
        {
            // Client gone.
        }
        catch (ObjectDisposedException)
        {
            // Socket already closed.
        }
    }

    private static async Task HandleSseStream(
        FeatureStreamSessionManager sessionManager,
        IFeatureChangeEventStore eventStore,
        FeatureStreamOptions options,
        ILogger logger,
        HttpContext context)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";

        try
        {
            await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return; // Client disconnected during handshake.
        }
        catch (IOException)
        {
            return; // Client disconnected during handshake.
        }
        catch (ObjectDisposedException)
        {
            return; // Client disconnected during handshake.
        }

        var clientLabel = context.Request.Query["clientLabel"].ToString();
        var cursorParam = context.Request.Query["cursor"].ToString();
        long? cursor = long.TryParse(cursorParam, CultureInfo.InvariantCulture, out var c) ? c : null;

        // Also check Last-Event-ID header (standard SSE reconnect mechanism).
        if (!cursor.HasValue)
        {
            var lastEventId = context.Request.Headers["Last-Event-ID"].ToString();
            if (long.TryParse(lastEventId, CultureInfo.InvariantCulture, out var lei))
            {
                cursor = lei;
            }
        }

        using var session = sessionManager.CreateSession(SseTransport, NullIfEmpty(clientLabel));

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted, session.DisconnectToken);

        // Replay missed events directly to the SSE response, bypassing the bounded channel
        // so that large replay backlogs are not truncated by the buffer limit.
        // Live broadcasts flow into the channel concurrently; the drain deduplicates
        // using the replay cursor so events are delivered exactly once.
        bool hasReplay = cursor.HasValue;
        long replayCursor = 0;
        if (hasReplay)
        {
            try
            {
                replayCursor = await ReplayToSseAsync(context.Response, eventStore, cursor!.Value, options.ReplayBatchSize, logger, session.SessionId, linkedCts.Token).ConfigureAwait(false);

                // Catch-up: replay events published during the main replay window that
                // were silently dropped from the bounded channel pre-drain.
                replayCursor = await ReplayToSseAsync(context.Response, eventStore, replayCursor, options.ReplayBatchSize, logger, session.SessionId, linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linkedCts.Token.IsCancellationRequested)
            {
                return; // Admin disconnect, slow-consumer removal, or request aborted during replay.
            }
            catch (IOException)
            {
                return; // Client disconnected during replay.
            }
            catch (ObjectDisposedException)
            {
                return; // Response stream disposed during replay.
            }
        }

        // Activate drain with buffer-sized grace for replay sessions so concurrent
        // overflows during the handoff are absorbed instead of disconnecting.
        sessionManager.MarkDrainStarted(session.SessionId,
            hasReplay ? options.MaxBufferPerConnection : 0);

        if (!hasReplay)
        {
            // Fresh live stream — no replay path, nothing to recover.
            sessionManager.ClearDrainGrace(session.SessionId);
        }

        // Convergent handoff: alternately drain the channel and sweep the store
        // until both are simultaneously empty.  Exits only when the channel is
        // empty AND the store has no new events, so ClearDrainGrace runs with
        // an empty channel and no unrecoverable grace-drops.
        try
        {
            if (hasReplay)
            {
                long previousCursor;
                do
                {
                    // Drain channel for headroom only — the store sweep below
                    // delivers everything in cursor order including grace-drops.
                    while (session.Reader.TryRead(out _)) { }

                    previousCursor = replayCursor;
                    replayCursor = await ReplayToSseAsync(context.Response, eventStore, replayCursor, options.ReplayBatchSize, logger, session.SessionId, linkedCts.Token).ConfigureAwait(false);
                } while (replayCursor > previousCursor || session.Reader.TryPeek(out _));
            }

            // For replay sessions, grace clear is deferred to the first drain
            // iteration (below) so the reader creates headroom for the final sweep.
            // For fresh sessions, grace was already cleared above.
            bool handoffDone = !hasReplay;

            await foreach (var message in session.Reader.ReadAllAsync(linkedCts.Token).ConfigureAwait(false))
            {
                if (!handoffDone)
                {
                    // The triggering message was consumed from the channel by
                    // ReadAllAsync but not yet sent.  Drain for headroom only and
                    // let the store sweep deliver everything (including the
                    // triggering message and any grace-drops) in cursor order.
                    // PublishAsync persists before Broadcast, so every channel
                    // item is guaranteed to be in the store.
                    bool progress;
                    do
                    {
                        progress = false;
                        while (session.Reader.TryRead(out _)) { }

                        long prev = replayCursor;
                        replayCursor = await ReplayToSseAsync(context.Response, eventStore, replayCursor, options.ReplayBatchSize, logger, session.SessionId, linkedCts.Token).ConfigureAwait(false);
                        if (replayCursor > prev)
                        {
                            progress = true;
                        }
                    } while (progress || session.Reader.TryPeek(out _));

                    sessionManager.ClearDrainGrace(session.SessionId);
                    handoffDone = true;

                    // The triggering message was delivered by the store sweep —
                    // skip normal processing to avoid duplicates.
                    continue;
                }

                // Skip events already delivered during replay to prevent duplicate delivery.
                if (!message.IsHeartbeat && replayCursor > 0 && message.Envelope.Cursor <= replayCursor)
                {
                    continue;
                }

                if (message.IsHeartbeat)
                {
                    await context.Response.WriteAsync(": heartbeat\n\n", linkedCts.Token).ConfigureAwait(false);
                }
                else
                {
                    var json = JsonSerializer.Serialize(
                        message.Envelope,
                        FeatureStreamJsonContext.Default.FeatureStreamEnvelope);

                    await context.Response.WriteAsync(
                        string.Concat(
                            "id: ", message.Envelope.Cursor.ToString(CultureInfo.InvariantCulture), "\n",
                            "event: feature-change\n",
                            "data: ", json, "\n\n"),
                        linkedCts.Token).ConfigureAwait(false);
                }

                await context.Response.Body.FlushAsync(linkedCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (linkedCts.Token.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (IOException)
        {
            // Client disconnected.
        }
        catch (ObjectDisposedException)
        {
            // Response stream already disposed.
        }
    }

    private static async Task<long> ReplayToWebSocketAsync(
        WebSocket webSocket,
        IFeatureChangeEventStore eventStore,
        long fromCursor,
        int batchSize,
        ILogger logger,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var cursor = fromCursor;
        while (!cancellationToken.IsCancellationRequested)
        {
            var events = await eventStore.QueryAsync(cursor, null, null, batchSize, cancellationToken).ConfigureAwait(false);
            if (events.Count == 0)
            {
                break;
            }

            FeatureStreamLog.ReplayStarted(logger, events.Count, cursor, sessionId);

            foreach (var evt in events)
            {
                var envelope = FeatureStreamPublisher.ToEnvelope(evt);
                var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, FeatureStreamJsonContext.Default.FeatureStreamEnvelope);
                await webSocket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
                cursor = evt.Cursor;
            }

            if (events.Count < batchSize)
            {
                break;
            }
        }

        return cursor;
    }

    private static async Task<long> ReplayToSseAsync(
        HttpResponse response,
        IFeatureChangeEventStore eventStore,
        long fromCursor,
        int batchSize,
        ILogger logger,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var cursor = fromCursor;
        while (!cancellationToken.IsCancellationRequested)
        {
            var events = await eventStore.QueryAsync(cursor, null, null, batchSize, cancellationToken).ConfigureAwait(false);
            if (events.Count == 0)
            {
                break;
            }

            FeatureStreamLog.ReplayStarted(logger, events.Count, cursor, sessionId);

            foreach (var evt in events)
            {
                var envelope = FeatureStreamPublisher.ToEnvelope(evt);
                var json = JsonSerializer.Serialize(envelope, FeatureStreamJsonContext.Default.FeatureStreamEnvelope);
                await response.WriteAsync(
                    string.Concat(
                        "id: ", envelope.Cursor.ToString(CultureInfo.InvariantCulture), "\n",
                        "event: feature-change\n",
                        "data: ", json, "\n\n"),
                    cancellationToken).ConfigureAwait(false);
                await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                cursor = evt.Cursor;
            }

            if (events.Count < batchSize)
            {
                break;
            }
        }

        return cursor;
    }

    private static IResult HandleListSessions(
        [FromServices] FeatureStreamSessionManager sessionManager,
        ILogger<FeatureStreamEndpointsLog> logger)
    {
        var sessions = sessionManager.GetSessions();
        var now = DateTimeOffset.UtcNow;

        var sessionResponses = sessions.Select(s => new FeatureStreamSessionResponse
        {
            SessionId = s.SessionId,
            ConnectedAt = s.ConnectedAt,
            ClientLabel = s.ClientLabel,
            Transport = s.Transport,
            LastQueuedCursor = s.LastQueuedCursor,
            DurationSeconds = (now - s.ConnectedAt).TotalSeconds
        }).ToArray();

        var wsSessions = sessionResponses.Count(s => s.Transport == WebSocketTransport);
        var sseSessions = sessionResponses.Count(s => s.Transport == SseTransport);

        var response = new FeatureStreamStatusResponse
        {
            ActiveSessions = sessionResponses.Length,
            WebSocketSessions = wsSessions,
            SseSessions = sseSessions,
            SlowConsumerDrops = sessionManager.SlowConsumerDrops,
            HeartbeatsSent = sessionManager.HeartbeatsSent,
            Sessions = sessionResponses,
            GeneratedAt = now
        };

        return Results.Json(
            ApiResponse<FeatureStreamStatusResponse>.CreateSuccess(response),
            FeatureStreamJsonContext.Default.ApiResponseFeatureStreamStatusResponse);
    }

    private static IResult HandleDisconnectSession(
        Guid sessionId,
        [FromServices] FeatureStreamSessionManager sessionManager,
        HttpContext context,
        ILogger<FeatureStreamEndpointsLog> logger)
    {
        if (!sessionManager.DisconnectSession(sessionId))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status404NotFound,
                string.Concat("Feature stream session ", sessionId.ToString(), " not found."));
        }

        return Results.Json(
            ApiResponse<object>.SuccessWithMessage(string.Concat("Session ", sessionId.ToString(), " disconnected.")),
            FeatureStreamJsonContext.Default.ApiResponseObject);
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// Log category for feature stream endpoints.
/// </summary>
internal sealed class FeatureStreamEndpointsLog;

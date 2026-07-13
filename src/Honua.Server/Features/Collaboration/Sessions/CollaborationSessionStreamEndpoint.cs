// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Collaboration.Sessions;

/// <summary>
/// Authenticated WebSocket transport for a saved-map collaboration session (#971/#1290): the
/// join handshake authorizes through the shared <see cref="ISavedMapCollaborationAuthorizer"/>,
/// streams the presence snapshot plus participant join/leave/heartbeat/cursor/selection/follow
/// events for the map, and accepts client control frames that update the joining participant's
/// own presence. Cross-node fan-out is provided by the Redis backplane registered alongside the
/// in-memory session service. Mirrors the feature-stream WebSocket writer/receiver pattern.
/// </summary>
internal static partial class CollaborationSessionStreamEndpoint
{
    private const int MaxControlFrameBytes = 16 * 1024;
    private static readonly TimeSpan WriterIdleTimeout = TimeSpan.FromSeconds(20);

    internal sealed class CollaborationStreamLogCategory;

    public static async Task HandleStream(
        string mapId,
        InMemoryCollaborationSessionService sessions,
        ILogger<CollaborationStreamLogCategory> logger,
        HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            // The route is upgrade-only; a plain GET is a client error.
            await StandardErrorHelpers.CreateBadRequest(
                    context,
                    "The collaboration session stream requires a WebSocket upgrade request.")
                .ExecuteAsync(context)
                .ConfigureAwait(false);
            return;
        }

        // Authorize and register the participant BEFORE upgrading so an unauthorized join fails
        // with a typed HTTP status (401/403) instead of an opaque socket close. The authorizer is
        // the same fail-closed identity/RBAC seam used by the HTTP join endpoint.
        var join = await sessions.JoinAsync(
                mapId,
                new CollaborationJoinRequest
                {
                    DisplayName = context.Request.Query["displayName"].ToString() is { Length: > 0 } dn ? dn : null,
                    ClientInstanceId = context.Request.Query["clientInstanceId"].ToString() is { Length: > 0 } ci ? ci : null
                },
                context.User,
                context.RequestAborted)
            .ConfigureAwait(false);

        if (join.Response is null)
        {
            var failure = join.Authorization.Status switch
            {
                SavedMapCollaborationAuthorizationStatus.RequiresAuthentication =>
                    StandardErrorHelpers.CreateUnauthorized(
                        context,
                        join.Authorization.Detail ?? "Authentication is required to join this collaboration session."),
                _ => StandardErrorHelpers.CreateForbidden(
                    context,
                    join.Authorization.Detail ?? "You are not allowed to join this collaboration session.")
            };
            await failure.ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        var sessionId = join.Response.SessionId;
        using var webSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        using var writeLock = new SemaphoreSlim(1, 1);

        try
        {
            // Initial frames: connection status + the current presence snapshot so the client can
            // render the "N here" roster immediately without a separate REST round-trip.
            await SendStatusAsync(
                webSocket,
                writeLock,
                new CollaborationStatusFrame
                {
                    Type = "status",
                    Status = "connected",
                    Message = "Collaboration session connected.",
                    SessionId = sessionId
                },
                context.RequestAborted).ConfigureAwait(false);

            await SendEnvelopeAsync(
                webSocket,
                writeLock,
                new CollaborationEventEnvelope
                {
                    Type = CollaborationSessionEventTypes.Snapshot,
                    EventId = Guid.NewGuid(),
                    MapId = mapId,
                    SessionId = sessionId,
                    Timestamp = join.Response.Snapshot.GeneratedAt,
                    Snapshot = join.Response.Snapshot
                },
                context.RequestAborted).ConfigureAwait(false);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var writerTask = WriteLoopAsync(webSocket, writeLock, sessions, sessionId, logger, linkedCts.Token);
            await ReceiveLoopAsync(webSocket, sessions, sessionId, logger, linkedCts.Token).ConfigureAwait(false);

            await linkedCts.CancelAsync().ConfigureAwait(false);
            await writerTask.ConfigureAwait(false);
        }
        finally
        {
            // Always remove the participant so presence does not leak when the socket closes.
            sessions.Leave(sessionId, reason: "disconnected");

            if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Collaboration session closed.",
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                    // Client already gone.
                }
                catch (ObjectDisposedException)
                {
                    // Socket already disposed.
                }
            }
        }
    }

    private static async Task WriteLoopAsync(
        WebSocket webSocket,
        SemaphoreSlim writeLock,
        InMemoryCollaborationSessionService sessions,
        Guid sessionId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && webSocket.State == WebSocketState.Open)
            {
                // Wait for outbox events without busy-polling. A periodic timeout lets the loop
                // re-check session liveness even when no events arrive so a pruned participant's
                // writer terminates promptly.
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                waitCts.CancelAfter(WriterIdleTimeout);

                bool stillPresent;
                try
                {
                    stillPresent = await sessions.WaitForEventsAsync(sessionId, waitCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (waitCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    // Idle timeout — loop back and re-check liveness.
                    continue;
                }

                if (!stillPresent)
                {
                    break;
                }

                foreach (var ev in sessions.DrainEvents(sessionId))
                {
                    if (webSocket.State != WebSocketState.Open)
                    {
                        return;
                    }

                    await SendEnvelopeAsync(webSocket, writeLock, ev, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (WebSocketException)
        {
            // Client disconnected.
        }
        catch (ObjectDisposedException)
        {
            // Socket disposed.
        }
    }

    private static async Task ReceiveLoopAsync(
        WebSocket webSocket,
        InMemoryCollaborationSessionService sessions,
        Guid sessionId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var (closeRequested, text, sizeExceeded) =
                    await ReceiveTextAsync(webSocket, cancellationToken).ConfigureAwait(false);
                if (closeRequested || sizeExceeded)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                ApplyControlFrame(sessions, sessionId, text, logger);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (WebSocketException)
        {
            // Client disconnected.
        }
        catch (ObjectDisposedException)
        {
            // Socket disposed.
        }
    }

    private static void ApplyControlFrame(
        InMemoryCollaborationSessionService sessions,
        Guid sessionId,
        string text,
        ILogger logger)
    {
        CollaborationClientFrame? frame;
        try
        {
            frame = JsonSerializer.Deserialize(
                text,
                CollaborationSessionJsonContext.Default.CollaborationClientFrame);
        }
        catch (JsonException)
        {
            // Malformed control frames are ignored; presence remains unchanged.
            return;
        }

        if (frame is null || string.IsNullOrWhiteSpace(frame.Type))
        {
            return;
        }

        try
        {
            switch (frame.Type.Trim().ToLowerInvariant())
            {
                case "heartbeat":
                case "ping":
                    sessions.Heartbeat(sessionId);
                    break;
                case "cursor":
                    if (frame.Cursor is not null)
                    {
                        sessions.UpdateCursor(sessionId, frame.Cursor);
                    }

                    break;
                case "selection":
                    if (frame.Selection is not null)
                    {
                        sessions.UpdateSelection(sessionId, frame.Selection);
                    }

                    break;
                case "follow":
                    if (frame.FollowSessionId is { } target)
                    {
                        sessions.Follow(sessionId, target);
                    }

                    break;
                case "unfollow":
                    sessions.Unfollow(sessionId);
                    break;
                default:
                    // Unknown control type — ignore to stay forward-compatible with SDK clients.
                    break;
            }
        }
        catch (KeyNotFoundException)
        {
            // Session or follow target vanished concurrently; drop the update.
        }
    }

    private static async Task<(bool CloseRequested, string? Text, bool SizeExceeded)> ReceiveTextAsync(
        WebSocket webSocket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await webSocket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (true, null, false);
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                return (false, null, false);
            }

            if (stream.Length + result.Count > MaxControlFrameBytes)
            {
                return (false, null, true);
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return (false, Encoding.UTF8.GetString(stream.ToArray()), false);
            }
        }
    }

    private static Task SendStatusAsync(
        WebSocket webSocket,
        SemaphoreSlim writeLock,
        CollaborationStatusFrame frame,
        CancellationToken cancellationToken)
        => SendJsonAsync(
            webSocket,
            writeLock,
            JsonSerializer.SerializeToUtf8Bytes(frame, CollaborationSessionJsonContext.Default.CollaborationStatusFrame),
            cancellationToken);

    private static Task SendEnvelopeAsync(
        WebSocket webSocket,
        SemaphoreSlim writeLock,
        CollaborationEventEnvelope envelope,
        CancellationToken cancellationToken)
        => SendJsonAsync(
            webSocket,
            writeLock,
            JsonSerializer.SerializeToUtf8Bytes(envelope, CollaborationSessionJsonContext.Default.CollaborationEventEnvelope),
            cancellationToken);

    private static async Task SendJsonAsync(
        WebSocket webSocket,
        SemaphoreSlim writeLock,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        if (webSocket.State != WebSocketState.Open)
        {
            return;
        }

        // Writer loop and the initial handshake share one socket; SendAsync is not safe to call
        // concurrently, so every send takes the per-connection write lock.
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (webSocket.State == WebSocketState.Open)
            {
                await webSocket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            writeLock.Release();
        }
    }
}

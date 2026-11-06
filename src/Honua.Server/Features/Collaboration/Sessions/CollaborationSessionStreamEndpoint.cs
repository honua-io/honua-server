// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Collaboration.Operations;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Collaboration.Sessions;

// If the cursor is outside the retained window, a typed resync-required event precedes a snapshot with no tail, and a node that is not authoritative for replay serves presence only.
internal static partial class CollaborationSessionStreamEndpoint
{
    private const int MaxControlFrameBytes = 16 * 1024;
    private static readonly TimeSpan WriterIdleTimeout = TimeSpan.FromSeconds(20);

    internal sealed class CollaborationStreamLogCategory;

    public static async Task HandleStream(
        string mapId,
        InMemoryCollaborationSessionService sessions,
        ISavedMapOperationLogRepository operationLog,
        ILogger<CollaborationStreamLogCategory> logger,
        HttpContext context)
    {
        // One canonical session/log key per draft regardless of the GUID textual form in the
        // route (honua-server#2999): sessions, appends, replay, and checkpoints must all agree.
        mapId = SavedMapCollaborationMapId.Normalize(mapId);

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

        long resumeFrom = 0;
        if (context.Request.Query["resumeFrom"].ToString() is { Length: > 0 } resumeText &&
            (!long.TryParse(resumeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out resumeFrom) ||
             resumeFrom < 0))
        {
            await StandardErrorHelpers.CreateBadRequest(
                    context,
                    "resumeFrom must be a non-negative operation cursor.")
                .ExecuteAsync(context)
                .ConfigureAwait(false);
            return;
        }

        // Authorize and register the participant BEFORE upgrading so an unauthorized join fails
        // with a typed HTTP status (401/403) instead of an opaque socket close. The authorizer is
        // the same fail-closed Studio identity/RBAC seam used by the HTTP join endpoint.
        var displayName = context.Request.Query["displayName"].ToString() is { Length: > 0 } dn ? dn : null;
        var clientInstanceId = context.Request.Query["clientInstanceId"].ToString() is { Length: > 0 } ci ? ci : null;
        if (!InMemoryCollaborationSessionService.TryValidateJoinFields(displayName, clientInstanceId, out var fieldError))
        {
            await StandardErrorHelpers.CreateBadRequest(context, fieldError!)
                .ExecuteAsync(context)
                .ConfigureAwait(false);
            return;
        }

        var join = await sessions.JoinAsync(
                mapId,
                new CollaborationJoinRequest
                {
                    DisplayName = displayName,
                    ClientInstanceId = clientInstanceId
                },
                context.User,
                context.RequestAborted)
            .ConfigureAwait(false);

        if (join.Response is null)
        {
            await CollaborationSessionEndpoints.MapJoinFailure(join, context)
                .ExecuteAsync(context)
                .ConfigureAwait(false);
            return;
        }

        var sessionId = join.Response.SessionId;
        var actorId = join.Response.ParticipantId;
        using var webSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        using var writeLock = new SemaphoreSlim(1, 1);

        try
        {
            // Handshake frames: a live status envelope, an optional resync-required error when the
            // resume cursor fell out of the replay window, then the snapshot (presence plus the
            // operation tail) so the client can render without a separate REST round-trip.
            await SendEnvelopeAsync(
                webSocket,
                writeLock,
                sessions.StampEnvelope(mapId, sessionId, actorId, new CollaborationSessionEvent
                {
                    Type = CollaborationSessionEventTypes.Status,
                    Status = "live"
                }),
                context.RequestAborted).ConfigureAwait(false);

            // Whether this node may answer replay at all. When the deployment is declared
            // multi-replica and the op log is process-local, the advertised capability already
            // says Replay=false — so this handshake must not hand back node-local history that
            // another replica's history contradicts. Presence still streams; the client is told
            // to load the durable document instead (honua-server#2999 review).
            var replayAvailable = sessions.Capabilities.Replay;
            var resyncAnnounced = false;

            // Where the snapshot tail starts. On the healthy path that is the client's own resume
            // cursor, so it only receives operations it has not seen. When the resume cursor has
            // fallen outside the retained window the client must reload the durable document,
            // which only reflects CHECKPOINTED operations — so the snapshot must carry the whole
            // retained window instead of nothing, otherwise every retained-but-not-yet-
            // checkpointed operation is dropped while the snapshot advertises the head cursor
            // (honua-server#2999 review). Re-sending an already-checkpointed prefix is safe:
            // every operation family is absolute-state and the reducer rebuilds from the snapshot.
            var tailSince = resumeFrom;
            if (!replayAvailable)
            {
                await AnnounceResyncAsync(
                    webSocket,
                    writeLock,
                    sessions,
                    mapId,
                    sessionId,
                    actorId,
                    "Operation replay is unavailable in this deployment: the collaboration " +
                    "operation log is not shared across replicas, so this node cannot prove its " +
                    "history is complete. Reload the saved map from its durable state.",
                    context.RequestAborted).ConfigureAwait(false);
                resyncAnnounced = true;
            }
            else
            {
                var replay = await operationLog.ReplayAsync(
                        new SavedMapId(mapId),
                        new SavedMapOperationCursor(resumeFrom),
                        context.RequestAborted)
                    .ConfigureAwait(false);

                if (replay.Status != SavedMapOperationReplayStatus.Ok)
                {
                    tailSince = replay.MinimumReplayCursor.Value;
                    await AnnounceResyncAsync(
                        webSocket,
                        writeLock,
                        sessions,
                        mapId,
                        sessionId,
                        actorId,
                        replay.Message ?? "The resume cursor is outside the retained operation replay window.",
                        context.RequestAborted).ConfigureAwait(false);
                    resyncAnnounced = true;
                }
            }

            // Stamp the snapshot envelope BEFORE assembling its contents, then re-read the op
            // tail and presence afterwards. Any event published while the snapshot is being
            // assembled is therefore stamped with a LATER sequence than the snapshot and is
            // delivered by the write loop after it (instead of being discarded by the client
            // reducer as pre-snapshot), while its state is only possibly duplicated inside the
            // snapshot — which is safe because presence is absolute-state and operations carry
            // op-log cursors. Events stamped before the snapshot envelope are covered by the
            // re-read contents.
            CollaborationEventEnvelope snapshotEnvelope;
            SnapshotTail tail;
            while (true)
            {
                snapshotEnvelope = sessions.StampEnvelope(mapId, sessionId, actorId, new CollaborationSessionEvent
                {
                    Type = CollaborationSessionEventTypes.Snapshot
                });

                tail = replayAvailable
                    ? await ReadSnapshotTailAsync(operationLog, mapId, tailSince, context.RequestAborted)
                        .ConfigureAwait(false)
                    : SnapshotTail.Unavailable;

                if (!tail.ResyncRequired)
                {
                    break;
                }

                if (resyncAnnounced)
                {
                    // The gap was already announced AND the window moved again through both
                    // retries, so the tail is empty while DeliveredThrough sits at the new base.
                    // Emitting that snapshot would advance the client past retained,
                    // UNCHECKPOINTED operations — it reloads the durable document, which reflects
                    // only checkpointed ones, and would then discard those operations' later
                    // envelopes as already-seen. There is no cursor that honestly describes this
                    // state, so terminate the handshake and let the client reconnect into a
                    // stable window (honua-server#2999 review).
                    await CloseWithErrorAsync(
                        webSocket,
                        "The retained operation replay window is advancing faster than the snapshot "
                        + "can be assembled. Reconnect to retry.",
                        context.RequestAborted).ConfigureAwait(false);
                    return;
                }

                // The resume cursor was inside the window during the preliminary replay, but
                // concurrent appends pruned it before the snapshot tail was read. The client is
                // NOT holding a complete history any more, so the gap must be announced —
                // silently restarting the tail from the new window base would leave it believing
                // it had every operation between its cursor and that base (honua-server#2999
                // review). The announcement is emitted before the snapshot is sent and the
                // snapshot is re-stamped afterwards, so the error keeps a lower sequence than the
                // snapshot the client rebuilds from.
                await AnnounceResyncAsync(
                    webSocket,
                    writeLock,
                    sessions,
                    mapId,
                    sessionId,
                    actorId,
                    tail.ResyncMessage ?? "The retained operation replay window advanced past the resume cursor.",
                    context.RequestAborted).ConfigureAwait(false);
                resyncAnnounced = true;
                // Restart from the window's current base, not from what the pruned-then-retried
                // read happened to deliver: a resyncing client reloads the durable document, which
                // reflects only CHECKPOINTED operations, so the snapshot must carry the whole
                // retained window rather than the suffix after it.
                tailSince = tail.WindowBaseCursor;
            }

            // The snapshot's own Sequence is the boundary the client reducer compares later
            // envelopes against, so it MUST be the sequence stamped before the tail was read —
            // not the channel's current sequence. Using the current one would place the boundary
            // after operations that were appended during snapshot assembly but are absent from
            // Operations, and the reducer would then discard their envelopes as pre-snapshot,
            // silently losing committed edits (honua-server#2999 review).
            var snapshot = sessions.GetSnapshot(mapId) with
            {
                Sequence = snapshotEnvelope.Sequence,
                Operations = tail.Operations,
                // Never advertise a cursor the snapshot did not actually deliver up to. Claiming
                // the current head while withholding operations would advance the client past
                // committed edits it never received, and its reducer would then discard those
                // operations' later envelopes as already-seen (honua-server#2999 review). When
                // replay is unavailable there is no defensible position at all, so no cursor is
                // advertised.
                Cursor = replayAvailable
                    ? tail.DeliveredThrough.ToString(CultureInfo.InvariantCulture)
                    : null
            };
            await SendEnvelopeAsync(
                webSocket,
                writeLock,
                snapshotEnvelope with
                {
                    Event = new CollaborationSessionEvent
                    {
                        Type = CollaborationSessionEventTypes.Snapshot,
                        Snapshot = snapshot
                    }
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

    // The tail reports whether the window moved past the requested start so the caller emits resync-required instead of a suffix with a hole.
    private readonly record struct SnapshotTail(
        CollaborationOperationWire[] Operations,
        long DeliveredThrough,
        bool ResyncRequired,
        string? ResyncMessage,
        long WindowBaseCursor)
    {
        // There is no tail because this node may not answer replay.
        public static SnapshotTail Unavailable { get; } =
            new([], 0, ResyncRequired: false, ResyncMessage: null, WindowBaseCursor: 0);
    }

    // The snapshot never advertises a cursor it did not deliver, and a moved window restarts the tail and reports the gap.
    private static async Task<SnapshotTail> ReadSnapshotTailAsync(
        ISavedMapOperationLogRepository operationLog,
        string mapId,
        long sinceCursor,
        CancellationToken cancellationToken)
    {
        var resyncRequired = false;
        string? resyncMessage = null;
        var windowBase = sinceCursor;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var replay = await operationLog.ReplayAsync(
                    new SavedMapId(mapId),
                    new SavedMapOperationCursor(sinceCursor),
                    cancellationToken)
                .ConfigureAwait(false);

            if (replay.Status == SavedMapOperationReplayStatus.Ok)
            {
                var operations = replay.Operations.Select(CollaborationOperationWire.FromEnvelope).ToArray();
                var deliveredThrough = replay.Operations.Count > 0
                    ? replay.Operations[replay.Operations.Count - 1].ServerCursor.Value
                    : sinceCursor;
                return new SnapshotTail(operations, deliveredThrough, resyncRequired, resyncMessage, windowBase);
            }

            resyncRequired = true;
            resyncMessage ??= replay.Message;
            sinceCursor = replay.MinimumReplayCursor.Value;
            windowBase = sinceCursor;
        }

        // The window kept moving; deliver nothing and stay honest about the position reached.
        return new SnapshotTail([], sinceCursor, ResyncRequired: true, resyncMessage, windowBase);
    }

    // The resync event ends the handshake without a snapshot the client could not trust.
    private static async Task CloseWithErrorAsync(
        WebSocket webSocket,
        string reason,
        CancellationToken cancellationToken)
    {
        if (webSocket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        try
        {
            await webSocket.CloseAsync(
                WebSocketCloseStatus.InternalServerError,
                reason,
                cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
            // Client already gone.
        }
        catch (OperationCanceledException)
        {
            // Shutting down or the client disconnected mid-close.
        }
    }

    private static Task AnnounceResyncAsync(
        WebSocket webSocket,
        SemaphoreSlim writeLock,
        InMemoryCollaborationSessionService sessions,
        string mapId,
        Guid sessionId,
        string actorId,
        string message,
        CancellationToken cancellationToken)
        => SendEnvelopeAsync(
            webSocket,
            writeLock,
            sessions.StampEnvelope(mapId, sessionId, actorId, new CollaborationSessionEvent
            {
                Type = CollaborationSessionEventTypes.Error,
                Code = CollaborationErrorCodes.ResyncRequired,
                Message = message,
                Terminal = false,
                ResyncRequired = true
            }),
            cancellationToken);

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
        => ApplyControlFrame(sessions, sessionId, text, sessions.Capabilities, logger);

    // Control frames are applied against the advertised capability snapshot and fail closed.
    internal static void ApplyControlFrame(
        InMemoryCollaborationSessionService sessions,
        Guid sessionId,
        string text,
        CollaborationCapabilities capabilities,
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

        // Presence frames are gated on the SAME flags the handshake advertised. Processing them
        // anyway fanned the update out to same-replica participants only, so an older or
        // nonconforming client saw topology-dependent partial presence — local collaborators
        // moving, remote ones frozen — rather than the disabled behaviour it was told to expect.
        // Partial presence is worse than none: it looks like the feature working
        // (honua-server#2999/#3074). Heartbeat stays ungated; it is session liveness, not
        // presence, and dropping it would expire live sessions.
        try
        {
            switch (frame.Type.Trim().ToLowerInvariant())
            {
                case "heartbeat":
                case "ping":
                    sessions.Heartbeat(sessionId);
                    break;
                case "cursor":
                    if (frame.Cursor is not null && capabilities.Cursors)
                    {
                        sessions.UpdateCursor(sessionId, frame.Cursor);
                    }

                    break;
                case "selection":
                    if (frame.Selection is not null && capabilities.Selections)
                    {
                        sessions.UpdateSelection(sessionId, frame.Selection);
                    }

                    break;
                case "follow":
                    if (capabilities.Follow)
                    {
                        ApplyFollowFrame(sessions, sessionId, frame.Follow);
                    }

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

    private static void ApplyFollowFrame(
        InMemoryCollaborationSessionService sessions,
        Guid sessionId,
        CollaborationFollowTarget? follow)
    {
        if (follow is null)
        {
            return;
        }

        if (!follow.Following || string.IsNullOrWhiteSpace(follow.TargetParticipantId))
        {
            sessions.Unfollow(sessionId);
            return;
        }

        // Participant ids are the session id in "N" form; a malformed target is ignored.
        if (Guid.TryParseExact(follow.TargetParticipantId.Trim(), "N", out var target))
        {
            sessions.Follow(sessionId, target);
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

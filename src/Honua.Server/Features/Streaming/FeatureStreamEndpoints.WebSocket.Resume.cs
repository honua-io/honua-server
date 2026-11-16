// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.WebSockets;
using System.Text.Json;
using Honua.Core.Features.Realtime;
using Honua.Infrastructure.Events;

namespace Honua.Server.Features.Streaming;

internal static partial class FeatureStreamEndpoints
{
    private readonly record struct WebSocketConnectionAnnouncement(
        WebSocket Socket,
        FeatureStreamSession Session,
        FeatureStreamCursor Requested,
        long InitialLiveCursor);

    private readonly record struct WebSocketReset(
        WebSocket Socket,
        SemaphoreSlim WriteLock,
        string Reason,
        long Head);

    private readonly record struct WebSocketSubscribeReplay(
        FeatureStreamDependencies Dependencies,
        WebSocket Socket,
        FeatureStreamSession Session,
        StreamSubscriptionFilter? Filter,
        FeatureStreamSubscriptionMode Mode,
        string SubscriptionId,
        JsonElement Cursor);

    private readonly record struct WebSocketReplayPlan(
        FeatureStreamCursor Requested,
        string? SnapshotReason,
        long PreAddCursor);

    private readonly record struct WebSocketRetentionGate(
        WebSocket Socket,
        FeatureStreamSession Session,
        IFeatureChangeEventStore EventStore,
        StreamSubscriptionFilter? Filter,
        string SubscriptionId);

    private static async Task AnnounceWebSocketConnectionAsync(
        WebSocketConnectionAnnouncement announcement,
        CancellationToken cancellationToken)
    {
        await SendWebSocketStatusAsync(
            announcement.Socket,
            announcement.Session.WriteLock,
            new FeatureStreamStatusFrame
            {
                Status = "connected",
                Message = "Feature stream connected.",
                SessionId = announcement.Session.SessionId
            },
            cancellationToken).ConfigureAwait(false);

        if (announcement.Requested.Kind != FeatureStreamCursorKind.Epoch)
        {
            return;
        }

        await SendWebSocketResetAsync(
            new(announcement.Socket, announcement.Session.WriteLock, RealtimeResetReasons.CursorEpoch, announcement.InitialLiveCursor),
            cancellationToken).ConfigureAwait(false);
    }

    private static Task SendWebSocketResetAsync(WebSocketReset reset, CancellationToken cancellationToken)
        => SendWebSocketJsonAsync(
            reset.Socket,
            reset.WriteLock,
            JsonSerializer.SerializeToUtf8Bytes(
                new FeatureStreamResetFrame
                {
                    Reason = reset.Reason,
                    Head = FeatureStreamCursor.Format(reset.Head)
                },
                FeatureStreamJsonContext.Default.FeatureStreamResetFrame),
            cancellationToken);

    // False when the expired cursor cannot take a snapshot and the socket must close.
    private static async Task<bool> AdmitExpiredWebSocketCursorAsync(
        WebSocketRetentionGate gate,
        CancellationToken cancellationToken)
    {
        var head = await gate.EventStore.GetCurrentCursorAsync(cancellationToken).ConfigureAwait(false);
        await SendWebSocketResetAsync(
            new(gate.Socket, gate.Session.WriteLock, RealtimeResetReasons.Retention, head),
            cancellationToken).ConfigureAwait(false);
        var snapshotScopeError = ValidateSnapshotScope(FeatureStreamSubscriptionMode.Snapshot, gate.Filter);
        if (snapshotScopeError is null)
        {
            return true;
        }

        await TrySendWebSocketTerminalErrorAsync(
            gate.Socket,
            gate.Session.WriteLock,
            gate.Session.SessionId,
            gate.SubscriptionId,
            snapshotScopeError,
            cancellationToken).ConfigureAwait(false);
        return false;
    }

    // Null when the control cursor was rejected or the client left before the plan was ready.
    private static async Task<WebSocketReplayPlan?> PrepareWebSocketReplayAsync(
        WebSocketSubscribeReplay replay,
        CancellationToken cancellationToken)
    {
        var requested = ReadControlCursor(replay.Cursor);
        if (requested.Kind == FeatureStreamCursorKind.Invalid)
        {
            await SendWebSocketErrorAsync(
                replay.Socket,
                replay.Session.WriteLock,
                "invalid-cursor",
                "cursor must be a features:<generation> position.",
                replay.SubscriptionId,
                cancellationToken).ConfigureAwait(false);
            return null;
        }

        var snapshotReason = await ResolveControlSnapshotReasonAsync(replay, requested, cancellationToken).ConfigureAwait(false);
        if (snapshotReason.Rejected)
        {
            return null;
        }

        var preAddCursor = await ReadPreAddCursorAsync(replay, requested, snapshotReason.Reason, cancellationToken).ConfigureAwait(false);
        if (preAddCursor is null)
        {
            return null;
        }

        return new WebSocketReplayPlan(requested, snapshotReason.Reason, preAddCursor.Value);
    }

    private static FeatureStreamCursor ReadControlCursor(JsonElement cursor) => cursor.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => FeatureStreamCursor.None,
        JsonValueKind.String => FeatureStreamCursor.Parse(cursor.GetString(), lastEventId: null),
        JsonValueKind.Number => FeatureStreamCursor.Parse(cursor.GetRawText(), lastEventId: null),
        _ => new FeatureStreamCursor(FeatureStreamCursorKind.Invalid, 0)
    };

    private readonly record struct ControlSnapshotReason(string? Reason, bool Rejected);

    private static async Task<ControlSnapshotReason> ResolveControlSnapshotReasonAsync(
        WebSocketSubscribeReplay replay,
        FeatureStreamCursor requested,
        CancellationToken cancellationToken)
    {
        string? snapshotReason = null;
        if (requested.Position.HasValue || IsSnapshotMode(replay.Mode))
        {
            try
            {
                snapshotReason = requested.Position.HasValue
                    ? await ResolveResnapshotReasonAsync(replay.Dependencies.EventStore, requested.Position.Value, cancellationToken).ConfigureAwait(false)
                    : FeatureStreamSnapshotReasons.Initial;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new(null, Rejected: true);
            }
        }

        if (snapshotReason is null || !requested.Position.HasValue)
        {
            return new(snapshotReason, Rejected: false);
        }

        var recoveryScopeError = ValidateSnapshotScope(FeatureStreamSubscriptionMode.Snapshot, replay.Filter);
        if (recoveryScopeError is null)
        {
            return new(snapshotReason, Rejected: false);
        }

        await SendWebSocketErrorAsync(
            replay.Socket,
            replay.Session.WriteLock,
            "invalid-subscription",
            recoveryScopeError,
            replay.SubscriptionId,
            cancellationToken).ConfigureAwait(false);
        return new(null, Rejected: true);
    }

    private static async Task<long?> ReadPreAddCursorAsync(
        WebSocketSubscribeReplay replay,
        FeatureStreamCursor requested,
        string? snapshotReason,
        CancellationToken cancellationToken)
    {
        var pauseForReplay = requested.Position.HasValue || snapshotReason is not null;
        if (pauseForReplay && requested.Kind != FeatureStreamCursorKind.Epoch)
        {
            return 0;
        }

        try
        {
            return await replay.Dependencies.EventStore.GetCurrentCursorAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}

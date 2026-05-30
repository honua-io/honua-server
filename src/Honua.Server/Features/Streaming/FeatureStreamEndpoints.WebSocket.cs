// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Services;
using Honua.Protocols.Ogc.Common;

namespace Honua.Server.Features.Streaming;

/// <summary>
/// WebSocket transport for feature-change streaming: connect handshake, the
/// channel-drain writer loop, the control-frame dispatcher, subscribe/unsubscribe
/// handlers, and the WebSocket framing/dedup helpers.
/// </summary>
internal static partial class FeatureStreamEndpoints
{
    private static async Task HandleWebSocketStream(
        FeatureStreamDependencies deps,
        ILogger logger,
        HttpContext context,
        IStreamSubscriptionFilter? subscriptionFilter,
        bool addDefaultSubscription)
    {
        var sessionManager = deps.SessionManager;
        var eventStore = deps.EventStore;
        var options = deps.Options.Value;
        var clientLabel = context.Request.Query["clientLabel"].ToString();
        var cursorParam = context.Request.Query["cursor"].ToString();
        long? cursor = long.TryParse(cursorParam, CultureInfo.InvariantCulture, out var c) ? c : null;
        var session = sessionManager.TryCreateSession(
            WebSocketTransport,
            NullIfEmpty(clientLabel),
            subscriptionFilter,
            addDefaultSubscription);
        if (session is null)
        {
            await WriteSessionLimitExceededAsync(context, options.MaxConcurrentSessions).ConfigureAwait(false);
            return;
        }

        using var sessionLease = session;
        using var webSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);

        // The default subscription is allocated at session creation and is never replaced
        // (control-frame subscribes always create distinct ids), so its generation is stable
        // for the life of the session. The replay/handoff/poll callbacks below pin this
        // generation when claiming send-time deliveries through the session manager.
        long defaultSubscriptionGeneration = sessionManager.GetDefaultSubscriptionGeneration(session.SessionId);

        await SendWebSocketStatusAsync(
            webSocket,
            session.WriteLock,
            new FeatureStreamStatusFrame
            {
                Status = "connected",
                Message = "Feature stream connected.",
                SessionId = session.SessionId
            },
            context.RequestAborted).ConfigureAwait(false);

        if (subscriptionFilter is not null)
        {
            FeatureStreamLog.SessionCreatedWithFilter(logger, session.SessionId, subscriptionFilter.Summary);
            await SendWebSocketStatusAsync(
                webSocket,
                session.WriteLock,
                new FeatureStreamStatusFrame
                {
                    Status = "subscribed",
                    Message = "Initial subscription accepted.",
                    SessionId = session.SessionId,
                    SubscriptionId = FeatureStreamSessionManager.DefaultSubscriptionId
                },
                context.RequestAborted).ConfigureAwait(false);
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted, session.DisconnectToken);

        // Replay missed events directly to the WebSocket, bypassing the bounded channel
        // so that large replay backlogs are not truncated by the buffer limit.
        // Live broadcasts flow into the channel concurrently; the drain deduplicates
        // using the replay cursor so events are delivered exactly once.
        bool hasReplay = addDefaultSubscription && cursor.HasValue;
        long replayCursor = 0;
        if (hasReplay)
        {
            try
            {
                replayCursor = await ReplayToWebSocketAsync(
                    webSocket,
                    session.WriteLock,
                    eventStore,
                    cursor!.Value,
                    options.ReplayBatchSize,
                    logger,
                    session.SessionId,
                    linkedCts.Token,
                    subscriptionFilter,
                    FeatureStreamSessionManager.DefaultSubscriptionId,
                    sessionManager,
                    defaultSubscriptionGeneration).ConfigureAwait(false);

                // Catch-up: replay events published during the main replay window that
                // were silently dropped from the bounded channel pre-drain.
                replayCursor = await ReplayToWebSocketAsync(
                    webSocket,
                    session.WriteLock,
                    eventStore,
                    replayCursor,
                    options.ReplayBatchSize,
                    logger,
                    session.SessionId,
                    linkedCts.Token,
                    subscriptionFilter,
                    FeatureStreamSessionManager.DefaultSubscriptionId,
                    sessionManager,
                    defaultSubscriptionGeneration).ConfigureAwait(false);
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
        else
        {
            replayCursor = await eventStore.GetCurrentCursorAsync(linkedCts.Token).ConfigureAwait(false);
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
                    replayCursor = await ReplayToWebSocketAsync(
                        webSocket,
                        session.WriteLock,
                        eventStore,
                        replayCursor,
                        options.ReplayBatchSize,
                        logger,
                        session.SessionId,
                        linkedCts.Token,
                        subscriptionFilter,
                        FeatureStreamSessionManager.DefaultSubscriptionId,
                        sessionManager,
                        defaultSubscriptionGeneration).ConfigureAwait(false);
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
        var writerTask = WriteWebSocketAsync(webSocket, session, replayCursor, sessionManager, logger, linkedCts.Token,
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
                        cursor = await ReplayToWebSocketAsync(
                            webSocket,
                            session.WriteLock,
                            eventStore,
                            cursor,
                            options.ReplayBatchSize,
                            logger,
                            session.SessionId,
                            ct,
                            subscriptionFilter,
                            FeatureStreamSessionManager.DefaultSubscriptionId,
                            sessionManager,
                            defaultSubscriptionGeneration).ConfigureAwait(false);
                        if (cursor > prev)
                        {
                            progress = true;
                        }
                    } while (progress || session.Reader.TryPeek(out _));

                    sessionManager.ClearDrainGrace(session.SessionId);
                    return cursor;
                }
        : null,
            // The poll closure is always installed so control-frame subscriptions (added
            // after connect via subscribe frames) are also covered by the durable-store
            // sweep — without this, a non-admin session opened with no query filters
            // (addDefaultSubscription:false) would have no recovery path for cross-node
            // events that the broadcast/Redis pub/sub fan-out missed. The default
            // subscription path stays gated on addDefaultSubscription so its writer-side
            // cursor fence remains the single source of truth for that subscription.
            onPoll: async (cursor, ct) =>
            {
                if (addDefaultSubscription)
                {
                    cursor = await ReplayToWebSocketAsync(
                        webSocket,
                        session.WriteLock,
                        eventStore,
                        cursor,
                        options.ReplayBatchSize,
                        logger,
                        session.SessionId,
                        ct,
                        subscriptionFilter,
                        FeatureStreamSessionManager.DefaultSubscriptionId,
                        sessionManager,
                        defaultSubscriptionGeneration).ConfigureAwait(false);
                }

                foreach (var sub in sessionManager.GetActivePollableSubscriptions(session.SessionId))
                {
                    var advanced = await ReplayToWebSocketAsync(
                        webSocket,
                        session.WriteLock,
                        eventStore,
                        sub.LastPolledCursor,
                        options.ReplayBatchSize,
                        logger,
                        session.SessionId,
                        ct,
                        sub.Filter,
                        sub.SubscriptionId,
                        sessionManager,
                        sub.Generation).ConfigureAwait(false);
                    if (advanced > sub.LastPolledCursor)
                    {
                        sessionManager.TryAdvanceSubscriptionPollCursor(
                            session.SessionId,
                            sub.SubscriptionId,
                            sub.Generation,
                            advanced);
                    }
                }

                return cursor;
            },
            pollInterval: options.CrossNodeSyncInterval);

        // Receive loop keeps the connection alive and detects client close.
        try
        {
            while (webSocket.State == WebSocketState.Open && !linkedCts.Token.IsCancellationRequested)
            {
                var receive = await ReceiveWebSocketTextAsync(
                    webSocket,
                    options.MaxControlFrameBytes,
                    linkedCts.Token).ConfigureAwait(false);
                if (receive.CloseRequested)
                {
                    break;
                }

                if (receive.SizeExceeded)
                {
                    await SendWebSocketErrorAsync(
                        webSocket,
                        session.WriteLock,
                        "control-frame-too-large",
                        $"WebSocket control frame exceeded the {options.MaxControlFrameBytes}-byte limit.",
                        null,
                        linkedCts.Token).ConfigureAwait(false);
                    break;
                }

                if (string.IsNullOrWhiteSpace(receive.Text))
                {
                    continue;
                }

                await HandleWebSocketControlAsync(
                    deps,
                    logger,
                    context,
                    webSocket,
                    session,
                    receive.Text,
                    linkedCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (linkedCts.Token.IsCancellationRequested)
        {
            // Normal shutdown or disconnect.
        }
        catch (WebSocketException ex)
        {
            FeatureStreamLog.WebSocketReceiveEnded(logger, ex, session.SessionId);
        }
        catch (ObjectDisposedException ex)
        {
            FeatureStreamLog.WebSocketReceiveEnded(logger, ex, session.SessionId);
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
            catch (WebSocketException ex)
            {
                FeatureStreamLog.WebSocketCloseFailed(logger, ex, session.SessionId);
            }
            catch (ObjectDisposedException ex)
            {
                FeatureStreamLog.WebSocketCloseFailed(logger, ex, session.SessionId);
            }
        }
    }

    private static async Task WriteWebSocketAsync(
        WebSocket webSocket,
        FeatureStreamSession session,
        long replayCursor,
        FeatureStreamSessionManager sessionManager,
        ILogger logger,
        CancellationToken cancellationToken,
        Func<long, CancellationToken, Task<long>>? onFirstRead = null,
        Func<long, CancellationToken, Task<long>>? onPoll = null,
        TimeSpan? pollInterval = null)
    {
        try
        {
            var effectivePollInterval = pollInterval.GetValueOrDefault(TimeSpan.FromSeconds(1));
            // Deadline-based poll scheduling: nextPollAt advances by exactly one
            // interval per fire, regardless of how many channel-traffic loop
            // iterations elapsed in between. Without an absolute deadline,
            // continuous local broadcasts win the WhenAny race every iteration
            // and each round resets the poll delay to a fresh full interval,
            // starving the durable-store recovery path indefinitely.
            var nextPollAt = onPoll is null
                ? DateTimeOffset.MaxValue
                : DateTimeOffset.UtcNow + effectivePollInterval;

            while (!cancellationToken.IsCancellationRequested)
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var waitToReadTask = session.Reader.WaitToReadAsync(waitCts.Token).AsTask();
                TimeSpan remainingDelay;
                if (onPoll is null)
                {
                    remainingDelay = Timeout.InfiniteTimeSpan;
                }
                else
                {
                    var diff = nextPollAt - DateTimeOffset.UtcNow;
                    remainingDelay = diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
                }
                var waitForPollTask = Task.Delay(remainingDelay, waitCts.Token);
                var completed = await Task.WhenAny(waitToReadTask, waitForPollTask).ConfigureAwait(false);

                if (completed == waitForPollTask)
                {
                    waitCts.Cancel();
                    try
                    {
                        await waitToReadTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (waitCts.Token.IsCancellationRequested)
                    {
                        // Ignore expected cancellation for the alternate waiter.
                    }

                    replayCursor = await onPoll!(replayCursor, cancellationToken).ConfigureAwait(false);
                    nextPollAt = DateTimeOffset.UtcNow + effectivePollInterval;
                    continue;
                }

                waitCts.Cancel();
                if (!await waitToReadTask.ConfigureAwait(false))
                {
                    break;
                }

                try
                {
                    await waitForPollTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (waitCts.Token.IsCancellationRequested)
                {
                    // Ignore expected cancellation for the alternate waiter.
                }

                while (session.Reader.TryRead(out var message))
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
                        return;
                    }

                    if (message.IsHeartbeat)
                    {
                        var heartbeatPayload = JsonSerializer.SerializeToUtf8Bytes(
                            new FeatureStreamHeartbeat(),
                            FeatureStreamJsonContext.Default.FeatureStreamHeartbeat);
                        await SendWebSocketJsonAsync(webSocket, session.WriteLock, heartbeatPayload, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var subscriptionId = message.Envelope.SubscriptionId ?? FeatureStreamSessionManager.DefaultSubscriptionId;
                    var isDefaultSubscription = string.Equals(
                        subscriptionId,
                        FeatureStreamSessionManager.DefaultSubscriptionId,
                        StringComparison.Ordinal);

                    // Default-subscription cursor fence. The writer's replayCursor tracks
                    // the high-water mark already delivered to the default subscription
                    // through the initial replay, the convergent handoff loop, the
                    // onFirstRead store sweep, the onPoll cross-node poll, and (post-fix)
                    // each successful live send. Send-time dedup
                    // (TryClaimSubscriptionDelivery) keeps the replay-to-live handoff
                    // exactly-once for small replay windows, but the recent-event LRU is
                    // bounded at RecentEventIdCapacity (128). On a high-volume final sweep
                    // a queued copy can arrive after its dedup key has been evicted, and
                    // the writer would re-send it. SSE has the same fence at the SSE drain.
                    // Non-default subscriptions are protected by their pause/unpause/post-
                    // unpause-sweep choreography, so broadcast does not queue frames for
                    // them within their own replay range.
                    if (replayCursor > 0
                        && isDefaultSubscription
                        && message.Envelope.Cursor <= replayCursor)
                    {
                        continue;
                    }

                    // Per-(event, subscription) dedup is claimed at send time so
                    // events that the channel later dropped as pre-drain overflow
                    // remain replayable. The claim also verifies that the
                    // subscription's generation has not advanced — if it did
                    // (unsubscribe or same-id replacement), the queued frame is
                    // stale and must be dropped without claiming dedup so a
                    // future replay for the new generation can still deliver the
                    // event. Whichever path (this writer or the per-subscription
                    // replay) wins the atomic test-and-set sends the frame; the
                    // other observes the recorded key and skips.
                    var claim = sessionManager.TryClaimSubscriptionDelivery(
                        session.SessionId,
                        subscriptionId,
                        message.SubscriptionGeneration,
                        message.Envelope.EventId);
                    if (claim == SubscriptionDeliveryClaim.StaleGeneration)
                    {
                        FeatureStreamLog.StaleSubscriptionFrameDropped(
                            logger,
                            session.SessionId,
                            subscriptionId,
                            message.SubscriptionGeneration);
                        continue;
                    }

                    if (claim == SubscriptionDeliveryClaim.AlreadyDelivered)
                    {
                        continue;
                    }

                    var payload = JsonSerializer.SerializeToUtf8Bytes(
                        message.Envelope,
                        FeatureStreamJsonContext.Default.FeatureStreamEnvelope);

                    await SendWebSocketJsonAsync(webSocket, session.WriteLock, payload, cancellationToken).ConfigureAwait(false);

                    // Watermark advance after a successful live send. The bounded
                    // RecentEventIdCapacity dedup LRU only protects the most recent 128
                    // entries per session; under high-volume live traffic a delivered
                    // event id can be evicted before the next durable poll runs, which
                    // would let the per-subscription store sweep re-emit it. Advancing
                    // the per-subscription poll watermark here scopes the next durable
                    // sweep to events strictly past the live-delivered cursor, preserving
                    // the documented at-least-once contract with the effectively
                    // exactly-once handoff in the small-window case.
                    var deliveredCursor = message.Envelope.Cursor;
                    if (isDefaultSubscription)
                    {
                        if (deliveredCursor > replayCursor)
                        {
                            replayCursor = deliveredCursor;
                        }
                    }
                    else
                    {
                        sessionManager.TryAdvanceSubscriptionPollCursor(
                            session.SessionId,
                            subscriptionId,
                            message.SubscriptionGeneration,
                            deliveredCursor);
                    }
                }

                // Drained the burst. If the poll deadline elapsed during the
                // drain, run the poll inline now — otherwise continuous broadcast
                // traffic could keep us pinned in the read branch and the
                // durable-store sweep would never fire.
                if (onPoll is not null && DateTimeOffset.UtcNow >= nextPollAt)
                {
                    replayCursor = await onPoll(replayCursor, cancellationToken).ConfigureAwait(false);
                    nextPollAt = DateTimeOffset.UtcNow + effectivePollInterval;
                }
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

    private static async Task HandleWebSocketControlAsync(
        FeatureStreamDependencies deps,
        ILogger logger,
        HttpContext context,
        WebSocket webSocket,
        FeatureStreamSession session,
        string text,
        CancellationToken cancellationToken)
    {
        FeatureStreamControlMessage? control;
        try
        {
            control = JsonSerializer.Deserialize(
                text,
                FeatureStreamJsonContext.Default.FeatureStreamControlMessage);
        }
        catch (JsonException)
        {
            await SendWebSocketErrorAsync(webSocket, session.WriteLock, "invalid-json", "Invalid WebSocket control frame JSON.", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (control is null || string.IsNullOrWhiteSpace(control.Type))
        {
            await SendWebSocketErrorAsync(webSocket, session.WriteLock, "invalid-control", "WebSocket control frame requires a type.", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (control.Type.Trim().ToLowerInvariant())
        {
            case "ping":
                await SendWebSocketStatusAsync(
                    webSocket,
                    session.WriteLock,
                    new FeatureStreamStatusFrame
                    {
                        Status = "ok",
                        Message = "Feature stream is connected.",
                        SessionId = session.SessionId
                    },
                    cancellationToken).ConfigureAwait(false);
                return;

            case "unsubscribe":
                await HandleWebSocketUnsubscribeAsync(deps.SessionManager, logger, webSocket, session, control, cancellationToken).ConfigureAwait(false);
                return;

            case "subscribe":
                await HandleWebSocketSubscribeAsync(deps, logger, context, webSocket, session, control, cancellationToken).ConfigureAwait(false);
                return;

            default:
                await SendWebSocketErrorAsync(webSocket, session.WriteLock, "unsupported-control", $"Unsupported WebSocket control frame type '{control.Type}'.", null, cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    private static async Task HandleWebSocketSubscribeAsync(
        FeatureStreamDependencies deps,
        ILogger logger,
        HttpContext context,
        WebSocket webSocket,
        FeatureStreamSession session,
        FeatureStreamControlMessage control,
        CancellationToken cancellationToken)
    {
        var (filter, error) = await BuildControlSubscriptionFilterAsync(deps, context, control, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            await SendWebSocketErrorAsync(webSocket, session.WriteLock, "invalid-subscription", error, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var options = deps.Options.Value;
        var subscriptionId = string.IsNullOrWhiteSpace(control.SubscriptionId)
            ? Guid.NewGuid().ToString("N")
            : control.SubscriptionId.Trim();

        if (subscriptionId.Length > options.MaxSubscriptionIdLength)
        {
            await SendWebSocketErrorAsync(
                webSocket,
                session.WriteLock,
                "invalid-subscription-id",
                $"subscriptionId exceeds the {options.MaxSubscriptionIdLength}-character limit.",
                null,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        // The default subscription id is reserved for the server-managed
        // session-wide subscription that is allocated at session creation and
        // owns the writer's onPoll/onFirstRead replay cursor. A client subscribe
        // here would replace it with a new generation, leaving the writer's
        // pinned default-subscription generation stale and silently dropping
        // every cross-node poll result.
        if (string.Equals(subscriptionId, FeatureStreamSessionManager.DefaultSubscriptionId, StringComparison.OrdinalIgnoreCase))
        {
            await SendWebSocketErrorAsync(
                webSocket,
                session.WriteLock,
                "invalid-subscription-id",
                $"subscriptionId '{FeatureStreamSessionManager.DefaultSubscriptionId}' is reserved for server-managed subscriptions.",
                null,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        // For no-cursor subscribes capture the store cursor BEFORE TryAddSubscription so
        // the per-subscription poll watermark covers any event committed between this
        // moment and the moment broadcast first sees the new subscription. Without a
        // pre-add snapshot, an event whose Broadcast iteration races ahead of the Add
        // would be missed by both the broadcast (sub not in dict yet) and the poll
        // (watermark already past the event). Dedup at send time handles any overlap
        // when the broadcast did see the sub. With-cursor subscribes do not need this:
        // their post-unpause-sweep advances the watermark to the last delivered cursor.
        var replayCursor = control.Cursor;
        long preAddCursor = 0;
        if (!replayCursor.HasValue)
        {
            try
            {
                preAddCursor = await deps.EventStore.GetCurrentCursorAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }

        // Pause the subscription before replay so the live channel writer cannot
        // queue events that the per-subscription replay path is about to deliver
        // directly. The unpause after replay restores normal live fan-out.
        var addOutcome = deps.SessionManager.TryAddSubscription(session.SessionId, subscriptionId, filter, paused: replayCursor.HasValue);
        switch (addOutcome.Result)
        {
            case AddSubscriptionResult.Added:
                break;
            case AddSubscriptionResult.LimitReached:
                await SendWebSocketErrorAsync(
                    webSocket,
                    session.WriteLock,
                    "subscription-limit-reached",
                    $"Session already has the maximum of {options.MaxSubscriptionsPerSession} subscriptions; unsubscribe before adding more.",
                    subscriptionId,
                    cancellationToken).ConfigureAwait(false);
                return;
            case AddSubscriptionResult.SessionGone:
            default:
                await SendWebSocketErrorAsync(webSocket, session.WriteLock, "session-closed", "Feature stream session is no longer active.", subscriptionId, cancellationToken).ConfigureAwait(false);
                return;
        }

        // Pin the generation we just allocated so replay claims and the writer
        // drain reject any stale-generation frames if a future control frame
        // replaces this same id mid-flight.
        var subscriptionGeneration = addOutcome.Generation;
        FeatureStreamLog.SubscriptionAdded(logger, session.SessionId, subscriptionId, filter?.Summary ?? "all");

        // Seed the per-subscription poll watermark for the no-cursor path so the
        // writer's cross-node sweep starts from the pre-add snapshot. The
        // with-cursor path advances the watermark in the finally block below.
        if (!replayCursor.HasValue && preAddCursor > 0)
        {
            deps.SessionManager.TryAdvanceSubscriptionPollCursor(
                session.SessionId,
                subscriptionId,
                subscriptionGeneration,
                preAddCursor);
        }
        long? currentCursor = null;
        try
        {
            if (replayCursor.HasValue)
            {
                currentCursor = await ReplayToWebSocketAsync(
                    webSocket,
                    session.WriteLock,
                    deps.EventStore,
                    replayCursor.Value,
                    deps.Options.Value.ReplayBatchSize,
                    logger,
                    session.SessionId,
                    cancellationToken,
                    filter,
                    subscriptionId,
                    deps.SessionManager,
                    subscriptionGeneration).ConfigureAwait(false);
            }

            await SendWebSocketStatusAsync(
                webSocket,
                session.WriteLock,
                new FeatureStreamStatusFrame
                {
                    Status = "subscribed",
                    Message = "Subscription accepted.",
                    SessionId = session.SessionId,
                    SubscriptionId = subscriptionId,
                    Cursor = currentCursor
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (replayCursor.HasValue)
            {
                // Convergent paused-state replay: events persisted after the last
                // ReplayToWebSocketAsync batch but before unpause would otherwise
                // be skipped by Broadcast (paused) and missed entirely. Sweep the
                // store one more time while still paused to drain that gap. Loop
                // until the store has no new events past the cursor we already
                // delivered.
                if (currentCursor.HasValue && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        long previousCursor;
                        do
                        {
                            previousCursor = currentCursor.Value;
                            currentCursor = await ReplayToWebSocketAsync(
                                webSocket,
                                session.WriteLock,
                                deps.EventStore,
                                currentCursor.Value,
                                deps.Options.Value.ReplayBatchSize,
                                logger,
                                session.SessionId,
                                cancellationToken,
                                filter,
                                subscriptionId,
                                deps.SessionManager,
                                subscriptionGeneration).ConfigureAwait(false);
                        } while (currentCursor.Value > previousCursor);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // Cancellation during catch-up: exit cleanly and let the
                        // unpause below run so the session does not stay stuck.
                    }
                    catch (WebSocketException)
                    {
                        // Client disconnected during catch-up; nothing more to do.
                    }
                }

                // Unpause now. Any event broadcast after this moment is queued
                // via the normal channel path. The atomic per-(event,
                // subscription) dedup in BroadcastLocally and ReplayToWebSocketAsync
                // ensures that a final post-unpause sweep below cannot duplicate
                // events the broadcast already queued.
                deps.SessionManager.TryUnpauseSubscription(session.SessionId, subscriptionId);

                // Final post-unpause sweep: closes the moment-of-unpause race
                // where a broadcast fires while paused (skipped) and is then
                // never re-broadcast. The convergent loop above narrowed the
                // window to a single instant, but the at-least-once contract
                // demands we still cover it. Atomic dedup keeps it exactly-once.
                if (currentCursor.HasValue && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        currentCursor = await ReplayToWebSocketAsync(
                            webSocket,
                            session.WriteLock,
                            deps.EventStore,
                            currentCursor.Value,
                            deps.Options.Value.ReplayBatchSize,
                            logger,
                            session.SessionId,
                            cancellationToken,
                            filter,
                            subscriptionId,
                            deps.SessionManager,
                            subscriptionGeneration).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // Normal shutdown.
                    }
                    catch (WebSocketException)
                    {
                        // Client disconnected.
                    }
                }

                // Seed the per-subscription poll watermark to the highest cursor
                // delivered through the per-sub replay path. The writer's cross-node
                // sweep starts from this watermark on each interval — without it,
                // the poll would re-scan the entire replay range.
                if (currentCursor.HasValue && currentCursor.Value > 0)
                {
                    deps.SessionManager.TryAdvanceSubscriptionPollCursor(
                        session.SessionId,
                        subscriptionId,
                        subscriptionGeneration,
                        currentCursor.Value);
                }
            }
        }
    }

    private static async Task HandleWebSocketUnsubscribeAsync(
        FeatureStreamSessionManager sessionManager,
        ILogger logger,
        WebSocket webSocket,
        FeatureStreamSession session,
        FeatureStreamControlMessage control,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(control.SubscriptionId))
        {
            await SendWebSocketErrorAsync(webSocket, session.WriteLock, "invalid-unsubscribe", "unsubscribe requires subscriptionId.", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var subscriptionId = control.SubscriptionId.Trim();

        // Mirror the subscribe-side reservation: clients cannot remove the
        // server-managed default subscription. Without this guard a removal
        // would strand the writer's pinned default-subscription generation,
        // and onPoll cross-node sweeps would silently drop every event.
        if (string.Equals(subscriptionId, FeatureStreamSessionManager.DefaultSubscriptionId, StringComparison.OrdinalIgnoreCase))
        {
            await SendWebSocketErrorAsync(
                webSocket,
                session.WriteLock,
                "invalid-unsubscribe",
                $"subscriptionId '{FeatureStreamSessionManager.DefaultSubscriptionId}' is reserved and cannot be unsubscribed.",
                null,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!sessionManager.TryRemoveSubscription(session.SessionId, subscriptionId))
        {
            await SendWebSocketErrorAsync(webSocket, session.WriteLock, "subscription-not-found", "Subscription was not found.", subscriptionId, cancellationToken).ConfigureAwait(false);
            return;
        }

        FeatureStreamLog.SubscriptionRemoved(logger, session.SessionId, subscriptionId);
        await SendWebSocketStatusAsync(
            webSocket,
            session.WriteLock,
            new FeatureStreamStatusFrame
            {
                Status = "unsubscribed",
                Message = "Subscription removed.",
                SessionId = session.SessionId,
                SubscriptionId = subscriptionId
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(IStreamSubscriptionFilter? Filter, string? Error)> BuildControlSubscriptionFilterAsync(
        FeatureStreamDependencies deps,
        HttpContext context,
        FeatureStreamControlMessage control,
        CancellationToken cancellationToken)
    {
        var serviceId = NullIfEmpty(control.ServiceId);
        var snapshot = await deps.MetadataV2GraphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        MetadataV2Service? service = null;
        if (serviceId is not null)
        {
            service = ResolveStreamService(snapshot, serviceId);
            if (service is null)
            {
                return (null, $"Service '{serviceId}' not found.");
            }

            serviceId = service.Metadata.Name;
        }

        var layerIds = ResolveControlLayerIds(control);
        if (serviceId is null && layerIds is null && !IsAdmin(context.User))
        {
            return (null, "Unfiltered all-layer feature streams require admin access.");
        }

        if (layerIds is not null)
        {
            var resolvedLayerIds = new List<int>(layerIds.Length);
            var seenResolvedLayerIds = new HashSet<int>();
            foreach (var layerId in layerIds)
            {
                // When a serviceId was provided, restrict layer ids to that service so a
                // caller cannot piggy-back unrelated layers on an authorized service.
                // When no serviceId was provided, look up the layer's primary service so
                // the access policy check evaluates the service-level policy too.
                StreamLayerDescriptor? layer;
                MetadataV2Service? authService;
                if (service is not null)
                {
                    layer = ResolveStreamLayer(snapshot, service, layerId);
                    if (layer is null)
                    {
                        return (null, $"Layer {layerId} is not part of service '{service.Metadata.Name}'.");
                    }

                    authService = service;
                }
                else
                {
                    layer = ResolveStreamLayer(snapshot, service: null, layerId);
                    if (layer is null)
                    {
                        return (null, $"Layer {layerId} not found.");
                    }

                    authService = layer.Service;
                }

                var accessError = RequireStreamLayerAccess(context, layer.Resource, authService);
                if (accessError is not null)
                {
                    return (null, "Access to the requested stream layer is forbidden.");
                }

                if (seenResolvedLayerIds.Add(layer.LayerId))
                {
                    resolvedLayerIds.Add(layer.LayerId);
                }
            }

            layerIds = resolvedLayerIds.ToArray();
        }
        else if (service is not null)
        {
            var accessError = RequireAllLayerAccess(context, snapshot, service);
            if (accessError is not null)
            {
                return (null, "Access to the requested stream service is forbidden.");
            }
        }

        double[]? bbox = null;
        if (control.Bbox is not null)
        {
            if (control.Bbox.Length != 4 || control.Bbox.Any(value => !double.IsFinite(value)))
            {
                return (null, "bbox requires four finite values.");
            }

            if (!IsSupportedBboxCrs(control.BboxCrs))
            {
                return (null, "Feature streams currently accept bbox filters in EPSG:4326 only.");
            }

            if (layerIds is null || layerIds.Length != 1)
            {
                return (null, "bbox filters require exactly one layer.");
            }

            if (control.Bbox[0] > control.Bbox[2] || control.Bbox[1] > control.Bbox[3] ||
                control.Bbox[0] < -180 || control.Bbox[2] > 180 ||
                control.Bbox[1] < -90 || control.Bbox[3] > 90)
            {
                return (null, "Invalid EPSG:4326 bbox.");
            }

            var layer = ResolveStreamLayer(snapshot, service, layerIds[0]);
            if (layer is null)
            {
                return (null, $"Layer {layerIds[0]} not found.");
            }

            var projected = await TryProjectSubscriptionBboxAsync(deps, control.Bbox, layer, cancellationToken).ConfigureAwait(false);
            if (projected.Error is not null)
            {
                return (null, projected.Error);
            }

            bbox = projected.Bbox;
        }

        FilterExpression? attributeFilter = null;
        if (!string.IsNullOrWhiteSpace(control.Filter))
        {
            if (layerIds is null || layerIds.Length != 1)
            {
                return (null, "attribute filters require exactly one layer.");
            }

            if (!TryResolveFilterLanguage(control.FilterLang, out var language, out var filterLangError))
            {
                return (null, filterLangError);
            }

            var parseResult = deps.FilterExpressionService.Parse(language, control.Filter);
            if (!parseResult.IsSuccess || parseResult.Expression is null)
            {
                return (null, $"Invalid filter expression: {parseResult.ErrorMessage}");
            }

            if (InMemoryFilterEvaluator.ExceedsMaxDepth(parseResult.Expression))
            {
                return (null, $"Filter expression exceeds maximum depth ({InMemoryFilterEvaluator.MaxStreamingDepth}) for streaming subscriptions.");
            }

            if (!InMemoryFilterEvaluator.TryValidateStreamingExpression(parseResult.Expression, out var validationError))
            {
                return (null, validationError ?? "Streaming subscriptions do not support the requested filter expression.");
            }

            var layer = ResolveStreamLayer(snapshot, service, layerIds[0]);
            if (layer is null)
            {
                return (null, $"Layer {layerIds[0]} not found.");
            }

            if (!TryValidateAttributeFilterFields(parseResult.Expression, layer, out var fieldError))
            {
                return (null, fieldError);
            }

            attributeFilter = parseResult.Expression;
        }

        StreamTemporalFilter? temporalFilter = null;
        if (!string.IsNullOrWhiteSpace(control.Datetime))
        {
            if (layerIds is null || layerIds.Length != 1)
            {
                return (null, "temporal filters require exactly one time-aware layer.");
            }

            var layer = ResolveStreamLayer(snapshot, service, layerIds[0]);
            if (layer is null)
            {
                return (null, $"Layer {layerIds[0]} not found.");
            }

            if (!TemporalExtentHelpers.HasOptInTemporalFields(layer.Resource))
            {
                return (null, $"Layer {layer.LayerId} is not time-aware; temporal stream filters require resource temporal metadata.");
            }

            if (!TryBuildStreamTemporalFilter(control.Datetime, layer.Resource, out var parsedTemporalFilter, out var temporalError) ||
                parsedTemporalFilter is null)
            {
                return (null, temporalError ?? "Invalid datetime parameter.");
            }

            temporalFilter = parsedTemporalFilter;
        }

        return (new StreamSubscriptionFilter(serviceId, layerIds, bbox, attributeFilter, temporalFilter), null);
    }

    private static int[]? ResolveControlLayerIds(FeatureStreamControlMessage control)
    {
        if (control.LayerId.HasValue)
        {
            return [control.LayerId.Value];
        }

        if (control.Layers is { Length: > 0 })
        {
            return control.Layers;
        }

        if (control.LayerIds is { Length: > 0 })
        {
            return control.LayerIds;
        }

        return null;
    }

    private static async Task<(bool CloseRequested, string? Text, bool SizeExceeded)> ReceiveWebSocketTextAsync(
        WebSocket webSocket,
        int maxBytes,
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

            // Enforce the configured cap before allocating more memory: a
            // malicious client could otherwise buffer a never-ending fragmented
            // text frame and exhaust server memory on a streaming connection.
            if (stream.Length + result.Count > maxBytes)
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

    private static Task SendWebSocketStatusAsync(
        WebSocket webSocket,
        SemaphoreSlim writeLock,
        FeatureStreamStatusFrame frame,
        CancellationToken cancellationToken)
        => SendWebSocketJsonAsync(
            webSocket,
            writeLock,
            JsonSerializer.SerializeToUtf8Bytes(frame, FeatureStreamJsonContext.Default.FeatureStreamStatusFrame),
            cancellationToken);

    private static Task SendWebSocketErrorAsync(
        WebSocket webSocket,
        SemaphoreSlim writeLock,
        string code,
        string message,
        string? subscriptionId,
        CancellationToken cancellationToken)
        => SendWebSocketJsonAsync(
            webSocket,
            writeLock,
            JsonSerializer.SerializeToUtf8Bytes(
                new FeatureStreamErrorFrame
                {
                    Code = code,
                    Message = message,
                    SubscriptionId = subscriptionId
                },
                FeatureStreamJsonContext.Default.FeatureStreamErrorFrame),
            cancellationToken);

    private static async Task SendWebSocketJsonAsync(
        WebSocket webSocket,
        SemaphoreSlim writeLock,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        if (webSocket.State != WebSocketState.Open)
        {
            return;
        }

        // The writer task drain, per-subscription replay, and control-handler frames
        // all share one socket. WebSocket.SendAsync is not safe to call concurrently
        // from multiple producers, so every send acquires the per-session write lock.
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (webSocket.State == WebSocketState.Open)
            {
                await webSocket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            writeLock.Release();
        }
    }
}

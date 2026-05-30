// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Infrastructure.Events;

namespace Honua.Server.Features.Streaming;

/// <summary>
/// Server-Sent Events transport for feature-change streaming: writes SSE event
/// frames, handles replay/handoff, deadline-based cross-node polling, and the
/// channel-drain loop for SSE clients.
/// </summary>
internal static partial class FeatureStreamEndpoints
{
    private static async Task WriteSseEventAsync<T>(
        HttpResponse response,
        string eventName,
        T payload,
        JsonTypeInfo<T> jsonTypeInfo,
        long? id,
        CancellationToken cancellationToken)
    {
        if (id.HasValue)
        {
            await response.WriteAsync(
                string.Concat("id: ", id.Value.ToString(CultureInfo.InvariantCulture), "\n"),
                cancellationToken).ConfigureAwait(false);
        }

        var json = JsonSerializer.Serialize(payload, jsonTypeInfo);
        await response.WriteAsync(
            string.Concat(
                "event: ", eventName, "\n",
                "data: ", json, "\n\n"),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task HandleSseStream(
        FeatureStreamSessionManager sessionManager,
        IFeatureChangeEventStore eventStore,
        FeatureStreamOptions options,
        ILogger logger,
        HttpContext context,
        IStreamSubscriptionFilter? subscriptionFilter)
    {
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

        var session = sessionManager.TryCreateSession(SseTransport, NullIfEmpty(clientLabel), subscriptionFilter);
        if (session is null)
        {
            await WriteSessionLimitExceededAsync(context, options.MaxConcurrentSessions).ConfigureAwait(false);
            return;
        }

        using var sessionLease = session;

        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";

        try
        {
            await WriteSseEventAsync(
                context.Response,
                "status",
                new FeatureStreamStatusFrame
                {
                    Status = "connected",
                    Message = "Feature stream connected.",
                    SessionId = session.SessionId
                },
                FeatureStreamJsonContext.Default.FeatureStreamStatusFrame,
                null,
                context.RequestAborted).ConfigureAwait(false);

            if (subscriptionFilter is not null)
            {
                await WriteSseEventAsync(
                    context.Response,
                    "status",
                    new FeatureStreamStatusFrame
                    {
                        Status = "subscribed",
                        Message = "Initial subscription accepted.",
                        SessionId = session.SessionId,
                        SubscriptionId = FeatureStreamSessionManager.DefaultSubscriptionId
                    },
                    FeatureStreamJsonContext.Default.FeatureStreamStatusFrame,
                    null,
                    context.RequestAborted).ConfigureAwait(false);
            }

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

        if (subscriptionFilter is not null)
        {
            FeatureStreamLog.SessionCreatedWithFilter(logger, session.SessionId, subscriptionFilter.Summary);
        }

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
                replayCursor = await ReplayToSseAsync(
                    context.Response,
                    eventStore,
                    cursor!.Value,
                    options.ReplayBatchSize,
                    logger,
                    session.SessionId,
                    linkedCts.Token,
                    subscriptionFilter,
                    FeatureStreamSessionManager.DefaultSubscriptionId).ConfigureAwait(false);

                // Catch-up: replay events published during the main replay window that
                // were silently dropped from the bounded channel pre-drain.
                replayCursor = await ReplayToSseAsync(
                    context.Response,
                    eventStore,
                    replayCursor,
                    options.ReplayBatchSize,
                    logger,
                    session.SessionId,
                    linkedCts.Token,
                    subscriptionFilter,
                    FeatureStreamSessionManager.DefaultSubscriptionId).ConfigureAwait(false);
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
                    replayCursor = await ReplayToSseAsync(
                        context.Response,
                        eventStore,
                        replayCursor,
                        options.ReplayBatchSize,
                        logger,
                        session.SessionId,
                        linkedCts.Token,
                        subscriptionFilter,
                        FeatureStreamSessionManager.DefaultSubscriptionId).ConfigureAwait(false);
                } while (replayCursor > previousCursor || session.Reader.TryPeek(out _));
            }

            // For replay sessions, grace clear is deferred to the first drain
            // iteration (below) so the reader creates headroom for the final sweep.
            // For fresh sessions, grace was already cleared above.
            bool handoffDone = !hasReplay;

            // Deadline-based poll scheduling: nextPollAt advances by exactly one
            // interval per fire, regardless of how many channel-traffic loop
            // iterations elapsed in between. Without an absolute deadline,
            // continuous local broadcasts win the WhenAny race every iteration
            // and each round resets the poll delay to a fresh full interval,
            // starving the durable-store recovery path indefinitely.
            var nextPollAt = DateTimeOffset.UtcNow + options.CrossNodeSyncInterval;

            while (!linkedCts.Token.IsCancellationRequested)
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token);
                var waitToReadTask = session.Reader.WaitToReadAsync(waitCts.Token).AsTask();
                var diff = nextPollAt - DateTimeOffset.UtcNow;
                var remainingDelay = diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
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

                    replayCursor = await ReplayToSseAsync(
                        context.Response,
                        eventStore,
                        replayCursor,
                        options.ReplayBatchSize,
                        logger,
                        session.SessionId,
                        linkedCts.Token,
                        subscriptionFilter,
                        FeatureStreamSessionManager.DefaultSubscriptionId).ConfigureAwait(false);
                    nextPollAt = DateTimeOffset.UtcNow + options.CrossNodeSyncInterval;
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
                    if (!handoffDone)
                    {
                        bool progress;
                        do
                        {
                            progress = false;
                            while (session.Reader.TryRead(out _)) { }

                            long prev = replayCursor;
                            replayCursor = await ReplayToSseAsync(
                                context.Response,
                                eventStore,
                                replayCursor,
                                options.ReplayBatchSize,
                                logger,
                                session.SessionId,
                                linkedCts.Token,
                                subscriptionFilter,
                                FeatureStreamSessionManager.DefaultSubscriptionId).ConfigureAwait(false);
                            if (replayCursor > prev)
                            {
                                progress = true;
                            }
                        } while (progress || session.Reader.TryPeek(out _));

                        sessionManager.ClearDrainGrace(session.SessionId);
                        handoffDone = true;
                        continue;
                    }

                    if (!message.IsHeartbeat && replayCursor > 0 && message.Envelope.Cursor <= replayCursor)
                    {
                        continue;
                    }

                    if (message.IsHeartbeat)
                    {
                        await WriteSseEventAsync(
                            context.Response,
                            "heartbeat",
                            new FeatureStreamHeartbeat(),
                            FeatureStreamJsonContext.Default.FeatureStreamHeartbeat,
                            null,
                            linkedCts.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        await WriteSseEventAsync(
                            context.Response,
                            "feature-change",
                            message.Envelope,
                            FeatureStreamJsonContext.Default.FeatureStreamEnvelope,
                            message.Envelope.Cursor,
                            linkedCts.Token).ConfigureAwait(false);

                        replayCursor = message.Envelope.Cursor;
                    }

                    await context.Response.Body.FlushAsync(linkedCts.Token).ConfigureAwait(false);
                }

                // Drained the burst. If the poll deadline elapsed during the
                // drain, run the poll inline now — otherwise continuous broadcast
                // traffic could keep us pinned in the read branch and the
                // durable-store sweep would never fire.
                if (DateTimeOffset.UtcNow >= nextPollAt)
                {
                    replayCursor = await ReplayToSseAsync(
                        context.Response,
                        eventStore,
                        replayCursor,
                        options.ReplayBatchSize,
                        logger,
                        session.SessionId,
                        linkedCts.Token,
                        subscriptionFilter,
                        FeatureStreamSessionManager.DefaultSubscriptionId).ConfigureAwait(false);
                    nextPollAt = DateTimeOffset.UtcNow + options.CrossNodeSyncInterval;
                }
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
}

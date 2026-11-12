// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.WebSockets;
using System.Text.Json;
using Honua.Infrastructure.Events;

namespace Honua.Server.Features.Streaming;

internal static partial class FeatureStreamEndpoints
{
    private static async Task<long> ReplayToWebSocketAsync(
        WebSocket webSocket,
        SemaphoreSlim writeLock,
        IFeatureChangeEventStore eventStore,
        long fromCursor,
        int batchSize,
        ILogger logger,
        Guid sessionId,
        CancellationToken cancellationToken,
        IStreamSubscriptionFilter? subscriptionFilter = null,
        string? subscriptionId = null,
        FeatureStreamSessionManager? sessionManager = null,
        long subscriptionGeneration = 0,
        FeatureStreamDeliveredCursorWindow? liveDelivered = null)
    {
        var cursor = fromCursor;
        var delivered = 0L;
        await EnsureReplayableAfterAsync(eventStore, fromCursor, logger, sessionId, subscriptionId, cancellationToken)
            .ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            var events = await eventStore.QueryAsync(cursor, null, null, batchSize, cancellationToken).ConfigureAwait(false);

            FeatureStreamLog.ReplayStarted(logger, events.Count, cursor, sessionId);

            foreach (var evt in events)
            {
                var envelope = FeatureStreamPublisher.ToEnvelope(evt) with { SubscriptionId = subscriptionId };
                cursor = evt.Cursor;

                // The live path already sent this cursor (it arrived ahead of the sweep, or
                // out of order below the live high-water mark): skip it so the sweep that
                // resumes from the swept floor does not re-send it (honua-server#6104).
                if (liveDelivered is not null && liveDelivered.WasDeliveredLive(evt.Cursor))
                {
                    continue;
                }

                // Apply subscription filter during replay — advance cursor past filtered events.
                if (subscriptionFilter is not null
                    && !subscriptionFilter.Matches(envelope, evt.GeometryEnvelope, evt.PropertiesJson))
                {
                    continue;
                }

                if (subscriptionFilter is StreamSubscriptionFilter securedFilter)
                {
                    envelope = securedFilter.Project(envelope);
                }

                // When a session manager and generation are supplied, claim the
                // (event, subscription) slot atomically. The claim also verifies
                // the subscription's generation, fencing stale replays after an
                // unsubscribe/replacement (although the per-connection control
                // loop is single-threaded, so this matches the writer-side
                // contract). Whichever send-time path wins the atomic test-and-
                // set sends the frame; the other observes the recorded key and
                // skips.
                if (sessionManager is not null && subscriptionId is not null && subscriptionGeneration > 0)
                {
                    if (sessionManager.TryClaimSubscriptionDelivery(sessionId, subscriptionId, subscriptionGeneration, evt.EventId)
                        != SubscriptionDeliveryClaim.Claimed)
                    {
                        continue;
                    }
                }
                else if (sessionManager is not null && subscriptionId is not null &&
                    // Generation-less call site (legacy/test); fall back to the dedup-only path.
                    !sessionManager.TryRememberSubscriptionDelivery(sessionId, subscriptionId, evt.EventId))
                {
                    continue;
                }

                // Sequence is allocated only for frames that survive filtering and dedup, and
                // INSIDE the write lock, so allocation order is wire order even when the writer
                // task is draining the same subscription concurrently (#3038 REQ-002 + review).
                if (!await SendStampedWebSocketJsonAsync(
                        webSocket,
                        writeLock,
                        envelope,
                        sessionManager,
                        sessionId,
                        subscriptionId,
                        subscriptionGeneration,
                        cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                delivered++;
            }

            if (events.Count < batchSize)
            {
                await EnsureReplayableAfterAsync(eventStore, cursor, logger, sessionId, subscriptionId, cancellationToken)
                    .ConfigureAwait(false);
                break;
            }
        }

        sessionManager?.RecordReplayEventsDelivered(WebSocketTransport, delivered);
        return cursor;
    }

    private static async Task<long> ReplayToSseAsync(
        HttpResponse response,
        IFeatureChangeEventStore eventStore,
        long fromCursor,
        int batchSize,
        ILogger logger,
        Guid sessionId,
        FeatureStreamSessionManager sessionManager,
        CancellationToken cancellationToken,
        IStreamSubscriptionFilter? subscriptionFilter = null,
        string? subscriptionId = null,
        long subscriptionGeneration = 0,
        FeatureStreamDeliveredCursorWindow? liveDelivered = null)
    {
        var cursor = fromCursor;
        var delivered = 0L;
        await EnsureReplayableAfterAsync(eventStore, fromCursor, logger, sessionId, subscriptionId, cancellationToken)
            .ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            var events = await eventStore.QueryAsync(cursor, null, null, batchSize, cancellationToken).ConfigureAwait(false);

            FeatureStreamLog.ReplayStarted(logger, events.Count, cursor, sessionId);

            foreach (var evt in events)
            {
                var envelope = FeatureStreamPublisher.ToEnvelope(evt) with { SubscriptionId = subscriptionId };
                cursor = evt.Cursor;

                // The live path already sent this cursor (it arrived ahead of the sweep, or
                // out of order below the live high-water mark): skip it so the sweep that
                // resumes from the swept floor does not re-send it (honua-server#6104).
                if (liveDelivered is not null && liveDelivered.WasDeliveredLive(evt.Cursor))
                {
                    continue;
                }

                // Apply subscription filter during replay — advance cursor past filtered events.
                if (subscriptionFilter is not null
                    && !subscriptionFilter.Matches(envelope, evt.GeometryEnvelope, evt.PropertiesJson))
                {
                    continue;
                }

                if (subscriptionFilter is StreamSubscriptionFilter securedFilter)
                {
                    envelope = securedFilter.Project(envelope);
                }

                var stampedEnvelope = StampSequence(
                    envelope,
                    sessionManager,
                    sessionId,
                    subscriptionId,
                    subscriptionGeneration);
                if (stampedEnvelope is null)
                {
                    continue;
                }

                envelope = stampedEnvelope;

                await WriteSseEventAsync(
                    response,
                    "feature-change",
                    envelope,
                    FeatureStreamJsonContext.Default.FeatureStreamEnvelope,
                    envelope.Cursor,
                    cancellationToken).ConfigureAwait(false);
                await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                delivered++;
            }

            if (events.Count < batchSize)
            {
                await EnsureReplayableAfterAsync(eventStore, cursor, logger, sessionId, subscriptionId, cancellationToken)
                    .ConfigureAwait(false);
                break;
            }
        }

        sessionManager.RecordReplayEventsDelivered(SseTransport, delivered);
        return cursor;
    }

    // A replay that starts, or ends, before the first resumable position has lost changes and fails
    // closed. A short read is otherwise proof of exhaustion: generations are allocated under a lock
    // held to commit, so no change below the last one read commits later.
    internal static async Task EnsureReplayableAfterAsync(
        IFeatureChangeEventStore eventStore,
        long replayCursor,
        ILogger logger,
        Guid sessionId,
        string? subscriptionId,
        CancellationToken cancellationToken)
    {
        var retentionWindow = await eventStore.GetRetentionWindowAsync(cancellationToken).ConfigureAwait(false);
        if (retentionWindow.HasGapAfter(replayCursor))
        {
            var firstAvailableCursor = retentionWindow.IsEmpty
                ? retentionWindow.CurrentCursor
                : retentionWindow.OldestRetainedCursor;
            ThrowReplayWindowGap(replayCursor, firstAvailableCursor, logger, sessionId, subscriptionId);
        }
    }

    private static void ThrowReplayWindowGap(
        long previousCursor,
        long firstAvailableCursor,
        ILogger logger,
        Guid sessionId,
        string? subscriptionId)
    {
        FeatureStreamLog.ReplayWindowGapDetected(
            logger,
            sessionId,
            subscriptionId ?? FeatureStreamSessionManager.DefaultSubscriptionId,
            previousCursor,
            firstAvailableCursor);
        throw new FeatureStreamReplayWindowGapException(previousCursor, firstAvailableCursor);
    }

    // The sequence stays absent when no session can honor it, and a generation that is not current is not stamped.
    private static FeatureStreamEnvelope? StampSequence(
        FeatureStreamEnvelope envelope,
        FeatureStreamSessionManager? sessionManager,
        Guid sessionId,
        string? subscriptionId,
        long subscriptionGeneration)
    {
        if (sessionManager is null || subscriptionId is null)
        {
            return envelope;
        }

        var sequence = sessionManager.NextSubscriptionSequence(sessionId, subscriptionId, subscriptionGeneration);
        return sequence < 0 ? null : envelope with { Sequence = sequence };
    }

    internal sealed class FeatureStreamReplayWindowGapException(
        long requestedCursor,
        long firstAvailableCursor)
        : Exception(
            $"Feature stream replay expected cursor {requestedCursor + 1}, "
            + $"but the first retained cursor is {firstAvailableCursor}.")
    {
    }
}

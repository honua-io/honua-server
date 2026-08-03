// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.WebSockets;
using System.Text.Json;
using Honua.Infrastructure.Events;

namespace Honua.Server.Features.Streaming;

/// <summary>
/// Durable-store replay helpers shared by the WebSocket and SSE transports.
/// Walks the feature-change event store from a cursor, applies subscription
/// filters and per-(event, subscription) dedup, then writes envelopes to the
/// transport.
/// </summary>
internal static partial class FeatureStreamEndpoints
{
    internal static bool HasReplayWindowGap(long requestedCursor, long firstAvailableCursor)
        => requestedCursor < long.MaxValue && firstAvailableCursor > requestedCursor + 1;

    internal static bool TryFindReplayWindowGap(
        IReadOnlyList<FeatureChangeEvent> events,
        long requestedCursor,
        out long previousCursor,
        out long firstAvailableCursor)
    {
        previousCursor = requestedCursor;
        firstAvailableCursor = requestedCursor;

        foreach (var evt in events)
        {
            var expectedCursor = previousCursor == long.MaxValue
                ? long.MaxValue
                : previousCursor + 1;
            if (evt.Cursor != expectedCursor)
            {
                firstAvailableCursor = evt.Cursor;
                return true;
            }

            previousCursor = evt.Cursor;
        }

        return false;
    }

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
        long subscriptionGeneration = 0)
    {
        var cursor = fromCursor;
        var requiredThroughCursor = fromCursor;
        var delivered = 0L;
        while (!cancellationToken.IsCancellationRequested)
        {
            var events = await eventStore.QueryAsync(cursor, null, null, batchSize, cancellationToken).ConfigureAwait(false);
            ThrowIfReplayWindowHasGap(events, cursor, logger, sessionId, subscriptionId);

            FeatureStreamLog.ReplayStarted(logger, events.Count, cursor, sessionId);

            foreach (var evt in events)
            {
                var envelope = FeatureStreamPublisher.ToEnvelope(evt) with { SubscriptionId = subscriptionId };
                cursor = evt.Cursor;

                // Apply subscription filter during replay — advance cursor past filtered events.
                if (subscriptionFilter is not null
                    && !subscriptionFilter.Matches(envelope, evt.GeometryEnvelope, evt.PropertiesJson))
                {
                    continue;
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
                requiredThroughCursor = await ValidateReplayTailAsync(
                    eventStore,
                    cursor,
                    requiredThroughCursor,
                    logger,
                    sessionId,
                    subscriptionId,
                    cancellationToken).ConfigureAwait(false);
                if (requiredThroughCursor <= cursor)
                {
                    break;
                }
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
        long subscriptionGeneration = 0)
    {
        var cursor = fromCursor;
        var requiredThroughCursor = fromCursor;
        var delivered = 0L;
        while (!cancellationToken.IsCancellationRequested)
        {
            var events = await eventStore.QueryAsync(cursor, null, null, batchSize, cancellationToken).ConfigureAwait(false);
            ThrowIfReplayWindowHasGap(events, cursor, logger, sessionId, subscriptionId);

            FeatureStreamLog.ReplayStarted(logger, events.Count, cursor, sessionId);

            foreach (var evt in events)
            {
                var envelope = FeatureStreamPublisher.ToEnvelope(evt) with { SubscriptionId = subscriptionId };
                cursor = evt.Cursor;

                // Apply subscription filter during replay — advance cursor past filtered events.
                if (subscriptionFilter is not null
                    && !subscriptionFilter.Matches(envelope, evt.GeometryEnvelope, evt.PropertiesJson))
                {
                    continue;
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
                requiredThroughCursor = await ValidateReplayTailAsync(
                    eventStore,
                    cursor,
                    requiredThroughCursor,
                    logger,
                    sessionId,
                    subscriptionId,
                    cancellationToken).ConfigureAwait(false);
                if (requiredThroughCursor <= cursor)
                {
                    break;
                }
            }
        }

        sessionManager.RecordReplayEventsDelivered(SseTransport, delivered);
        return cursor;
    }

    internal static async Task<long> ValidateReplayTailAsync(
        IFeatureChangeEventStore eventStore,
        long replayCursor,
        long requiredThroughCursor,
        ILogger logger,
        Guid sessionId,
        string? subscriptionId,
        CancellationToken cancellationToken)
    {
        // A previous short batch observed this durable head and deliberately retried so an
        // append racing that first query would not be mistaken for a gap. Redis commands are
        // ordered: if the retry still cannot reach the already-observed head, at least one
        // required payload is unavailable and replay must fail closed.
        if (replayCursor < requiredThroughCursor)
        {
            ThrowReplayWindowGap(
                replayCursor,
                requiredThroughCursor,
                logger,
                sessionId,
                subscriptionId);
        }

        // Empty and short batches are not proof of exhaustion. A Redis payload can expire or
        // be evicted while its durable cursor remains advanced. Re-read the typed window before
        // ending; if a concurrent append advanced the head, retry once through that observed
        // boundary. The next short batch either reaches it or takes the fail-closed branch above.
        var retentionWindow = await eventStore
            .GetRetentionWindowAsync(cancellationToken).ConfigureAwait(false);
        if (retentionWindow.HasGapAfter(replayCursor))
        {
            var firstAvailableCursor = retentionWindow.IsEmpty
                ? retentionWindow.CurrentCursor
                : retentionWindow.OldestRetainedCursor;
            ThrowReplayWindowGap(
                replayCursor,
                firstAvailableCursor,
                logger,
                sessionId,
                subscriptionId);
        }

        return retentionWindow.CurrentCursor;
    }

    private static void ThrowIfReplayWindowHasGap(
        IReadOnlyList<FeatureChangeEvent> events,
        long requestedCursor,
        ILogger logger,
        Guid sessionId,
        string? subscriptionId)
    {
        if (!TryFindReplayWindowGap(
                events,
                requestedCursor,
                out var previousCursor,
                out var firstAvailableCursor))
        {
            return;
        }

        ThrowReplayWindowGap(previousCursor, firstAvailableCursor, logger, sessionId, subscriptionId);
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

    /// <summary>
    /// Stamps the next subscription-local sequence onto an envelope that is about to be
    /// written. Returns the envelope unchanged when no session manager or subscription is
    /// available (generation-less legacy/test call sites), so the field stays absent rather
    /// than advertising a sequence the caller cannot honor. Returns <see langword="null"/>
    /// when the requested subscription generation is no longer current so callers cannot
    /// accidentally emit a stale sequence-less frame.
    /// </summary>
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

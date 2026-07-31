// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Events;

namespace Honua.Server.Features.Streaming;

/// <summary>
/// Snapshot-then-delta support: baseline capture, replacement-snapshot detection, and the
/// transport-neutral snapshot emitter shared by the SSE and WebSocket transports (#3038).
/// </summary>
/// <remarks>
/// <para><b>Consistency boundary.</b> The baseline cursor is captured from the durable
/// event store <i>before</i> the baseline read begins, and deltas are replayed from that
/// cursor once the snapshot has been written. Because a feature-change event is appended
/// only after its mutation has committed, every event with a cursor at or below the
/// baseline cursor is already visible to the baseline read — so nothing can be lost between
/// the two. A mutation that commits before the read but whose event is appended after the
/// baseline cursor appears in both the baseline and the delta stream, which makes the
/// handoff at-least-once rather than exactly-once at the boundary. Delta envelopes carry
/// the full post-mutation attribute snapshot, so applying them over the baseline in
/// sequence order is idempotent and convergent.</para>
/// <para><b>Admission parity.</b> Baseline features are admitted through the same
/// <see cref="StreamSubscriptionFilter"/> instance the delta path uses, evaluated against
/// the same enrichment representation (geometry envelope plus properties JSON). A feature
/// is therefore in the baseline if and only if a delta describing it would be admitted,
/// which is what makes the subscription-local sequence meaningful across the boundary.</para>
/// <para><b>Bounds.</b> Snapshots require an explicit layer scope and are bounded by
/// <see cref="FeatureStreamOptions.MaxSnapshotScanRows"/> and
/// <see cref="FeatureStreamOptions.MaxSnapshotFeatures"/>. Hitting either bound ends the
/// snapshot with <c>complete: false</c> rather than silently truncating.</para>
/// </remarks>
internal static partial class FeatureStreamEndpoints
{
    private const string SnapshotModeValue = "snapshot";
    private const string DeltaModeValue = "delta";

    /// <summary>
    /// Result of emitting one baseline snapshot.
    /// </summary>
    private readonly record struct SnapshotEmitResult(long BaselineCursor, long FeatureCount, bool Complete);

    /// <summary>
    /// Transport-neutral sink for snapshot frames. Keeps the baseline emitter free of any
    /// SSE/WebSocket framing detail so both transports expose identical semantics.
    /// </summary>
    private abstract class FeatureStreamSnapshotSink
    {
        public abstract Task WriteBeginAsync(FeatureStreamSnapshotBeginFrame frame, CancellationToken cancellationToken);

        public abstract Task WriteFeatureAsync(FeatureStreamSnapshotFeatureFrame frame, CancellationToken cancellationToken);

        public abstract Task WriteEndAsync(FeatureStreamSnapshotEndFrame frame, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Writes snapshot frames as SSE events.
    /// </summary>
    /// <remarks>
    /// Only a COMPLETE <c>snapshot-end</c> carries an SSE <c>id:</c>. A baseline is resumable
    /// as a delta cursor only once it is whole in both senses — every frame delivered AND
    /// nothing truncated by the feature/scan bounds. If the connection drops after
    /// <c>snapshot-begin</c> or a <c>snapshot-feature</c>, the browser reconnects with the
    /// last id it saw, and publishing the baseline cursor early would make that reconnect
    /// look like a replayable delta resume — the client would never receive the rest of its
    /// baseline yet would treat the partial state as current. Leaving those frames
    /// id-less keeps <c>Last-Event-ID</c> at its pre-snapshot value (absent on a fresh
    /// connection, or the same stale cursor that triggered the replacement snapshot), so an
    /// interrupted baseline reconnects into another snapshot rather than a delta tail.
    /// </remarks>
    private sealed class SseSnapshotSink(HttpResponse response) : FeatureStreamSnapshotSink
    {
        public override async Task WriteBeginAsync(FeatureStreamSnapshotBeginFrame frame, CancellationToken cancellationToken)
        {
            await WriteSseEventAsync(
                response,
                "snapshot-begin",
                frame,
                FeatureStreamJsonContext.Default.FeatureStreamSnapshotBeginFrame,
                null,
                cancellationToken).ConfigureAwait(false);
            await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public override Task WriteFeatureAsync(FeatureStreamSnapshotFeatureFrame frame, CancellationToken cancellationToken)
            => WriteSseEventAsync(
                response,
                "snapshot-feature",
                frame,
                FeatureStreamJsonContext.Default.FeatureStreamSnapshotFeatureFrame,
                null,
                cancellationToken);

        public override async Task WriteEndAsync(FeatureStreamSnapshotEndFrame frame, CancellationToken cancellationToken)
        {
            await WriteSseEventAsync(
                response,
                "snapshot-end",
                frame,
                FeatureStreamJsonContext.Default.FeatureStreamSnapshotEndFrame,
                // Only a COMPLETE baseline becomes a resumable checkpoint. A truncated one
                // (feature cap or scan bound) omits features that no later delta will ever
                // mention — they did not change — so resuming from its cursor leaves the client
                // permanently missing them. Withholding the id keeps Last-Event-ID at its
                // pre-snapshot value, so the reconnect takes another snapshot instead of a
                // delta tail (honua-server#3038 review).
                frame.Complete ? frame.Cursor : null,
                cancellationToken).ConfigureAwait(false);
            await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class WebSocketSnapshotSink(WebSocket webSocket, SemaphoreSlim writeLock) : FeatureStreamSnapshotSink
    {
        public override Task WriteBeginAsync(FeatureStreamSnapshotBeginFrame frame, CancellationToken cancellationToken)
            => SendWebSocketJsonAsync(
                webSocket,
                writeLock,
                JsonSerializer.SerializeToUtf8Bytes(frame, FeatureStreamJsonContext.Default.FeatureStreamSnapshotBeginFrame),
                cancellationToken);

        public override Task WriteFeatureAsync(FeatureStreamSnapshotFeatureFrame frame, CancellationToken cancellationToken)
            => SendWebSocketJsonAsync(
                webSocket,
                writeLock,
                JsonSerializer.SerializeToUtf8Bytes(frame, FeatureStreamJsonContext.Default.FeatureStreamSnapshotFeatureFrame),
                cancellationToken);

        public override Task WriteEndAsync(FeatureStreamSnapshotEndFrame frame, CancellationToken cancellationToken)
            => SendWebSocketJsonAsync(
                webSocket,
                writeLock,
                JsonSerializer.SerializeToUtf8Bytes(frame, FeatureStreamJsonContext.Default.FeatureStreamSnapshotEndFrame),
                cancellationToken);
    }

    /// <summary>
    /// Parses the requested subscription mode. Returns false with a client-safe message for
    /// unrecognized values so an unsupported mode can never be silently downgraded to
    /// change-only delivery.
    /// </summary>
    private static bool TryParseSubscriptionMode(
        string? value,
        out FeatureStreamSubscriptionMode mode,
        out string? error)
    {
        mode = FeatureStreamSubscriptionMode.Delta;
        error = null;

        if (string.IsNullOrWhiteSpace(value) || value.Equals(DeltaModeValue, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.Equals(SnapshotModeValue, StringComparison.OrdinalIgnoreCase) ||
            value.Equals("snapshot-then-delta", StringComparison.OrdinalIgnoreCase))
        {
            mode = FeatureStreamSubscriptionMode.Snapshot;
            return true;
        }

        error = $"Unsupported subscription mode '{value}'. Supported modes: delta, snapshot.";
        return false;
    }

    /// <summary>
    /// Validates that a snapshot-then-delta subscription carries the explicit layer scope a
    /// bounded baseline read requires. Returns null when the filter is acceptable.
    /// </summary>
    private static string? ValidateSnapshotScope(
        FeatureStreamSubscriptionMode mode,
        IStreamSubscriptionFilter? filter)
    {
        if (mode != FeatureStreamSubscriptionMode.Snapshot)
        {
            return null;
        }

        if (filter is not StreamSubscriptionFilter scoped || scoped.LayerIds is not { Length: > 0 })
        {
            return "snapshot subscriptions require an explicit layer scope; supply the layers parameter (or the layers/layerId control-frame field).";
        }

        // A value-dependent predicate breaks convergence rather than merely narrowing the
        // baseline: a feature admitted into the snapshot can be updated so it no longer matches,
        // and because replay and live fan-out both evaluate the POST-mutation image, that
        // leaving update is filtered out. The client keeps the stale baseline feature with no
        // event that could ever correct it. Refuse the combination rather than advertise a
        // baseline the deltas cannot keep true; delivering it needs transition semantics the
        // event pipeline does not carry (honua-server#3038 review).
        return scoped.HasValueDependentPredicate
            ? "snapshot subscriptions cannot be combined with bbox, attribute, or temporal filters: a feature that leaves the filter after an update produces no delta, so the baseline could not be kept convergent. Use mode=delta with the filter, or mode=snapshot scoped by service/layer only."
            : null;
    }

    /// <summary>
    /// Decides whether a client-supplied resume cursor can still be served with deltas.
    /// Returns null when replay is safe, or the reason a replacement snapshot must be
    /// emitted instead: the cursor is ahead of the store's current position, or the events
    /// between the cursor and the retained window have been trimmed or expired.
    /// </summary>
    /// <remarks>
    /// Only the <i>absence</i> of a cursor selects the initial-snapshot path; the callers
    /// own that distinction. An explicit <c>cursor=0</c> is a real request to resume from
    /// the beginning of the stream and is validated against the retained window like any
    /// other value — once cursor 1 has been trimmed or expired, resuming at 0 with deltas
    /// alone would leave the client permanently missing the trimmed history.
    /// </remarks>
    private static async Task<string?> ResolveResnapshotReasonAsync(
        IFeatureChangeEventStore eventStore,
        long cursor,
        CancellationToken cancellationToken)
    {
        if (cursor < 0)
        {
            // Cursors are monotonic and non-negative; a negative value cannot be resumed
            // from and is treated like any other fabricated cursor.
            return FeatureStreamSnapshotReasons.CursorInvalid;
        }

        var current = await eventStore.GetCurrentCursorAsync(cancellationToken).ConfigureAwait(false);
        if (cursor > current)
        {
            return FeatureStreamSnapshotReasons.CursorInvalid;
        }

        var oldest = await eventStore.GetOldestRetainedCursorAsync(cancellationToken).ConfigureAwait(false);

        // The retained window starts at `oldest`. Deltas the client still needs occupy
        // (cursor, oldest); when that interval is non-empty the events are gone and the
        // client cannot converge from deltas alone.
        return oldest > cursor + 1
            ? FeatureStreamSnapshotReasons.CursorExpired
            : null;
    }

    /// <summary>
    /// Captures the baseline cursor, reads the matching features through the shared feature
    /// reader, and writes <c>snapshot-begin</c>, <c>snapshot-feature</c>, and
    /// <c>snapshot-end</c> frames with contiguous subscription-local sequence numbers.
    /// </summary>
    private static async Task<SnapshotEmitResult> EmitSnapshotAsync(
        FeatureStreamDependencies deps,
        ILogger logger,
        FeatureStreamSnapshotSink sink,
        Guid sessionId,
        string subscriptionId,
        long subscriptionGeneration,
        StreamSubscriptionFilter filter,
        string reason,
        CancellationToken cancellationToken)
    {
        var options = deps.Options.Value;

        // Capture the delta boundary BEFORE reading state. Events at or below this cursor
        // committed before the read started and are therefore already reflected in the
        // baseline; everything above it is replayed as a delta after snapshot-end.
        var baselineCursor = await deps.EventStore.GetCurrentCursorAsync(cancellationToken).ConfigureAwait(false);
        var snapshotId = Guid.NewGuid().ToString("N");
        var layerIds = filter.LayerIds ?? [];

        await sink.WriteBeginAsync(
            new FeatureStreamSnapshotBeginFrame
            {
                SnapshotId = snapshotId,
                SubscriptionId = subscriptionId,
                Sequence = NextSequence(deps.SessionManager, sessionId, subscriptionId, subscriptionGeneration),
                Cursor = baselineCursor,
                Reason = reason,
                ServiceId = filter.ServiceId,
                LayerIds = layerIds
            },
            cancellationToken).ConfigureAwait(false);

        var graph = await deps.MetadataV2GraphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var service = filter.ServiceId is null ? null : ResolveStreamService(graph, filter.ServiceId);

        long emitted = 0;
        long scanned = 0;
        var complete = true;

        // Set once the feature cap is reached. Anything still unread after that point —
        // later pages of the current layer, or any remaining layer — makes the baseline
        // non-authoritative, including when the cap lands exactly on a page boundary.
        var capped = false;

        foreach (var layerId in layerIds)
        {
            if (capped)
            {
                complete = false;
                break;
            }

            var descriptor = ResolveStreamLayer(graph, service, layerId);
            if (descriptor is null)
            {
                // The subscription filter authorized this layer at subscribe time; a
                // concurrent catalog revision can still remove it. Report the baseline as
                // incomplete rather than emitting a silently partial view.
                complete = false;
                continue;
            }

            var layerSrid = descriptor.Resource.ReadSrid();
            var serviceId = filter.ServiceId ?? descriptor.Service?.Metadata.Name ?? string.Empty;
            var remainingScan = options.MaxSnapshotScanRows - (int)scanned;
            if (remainingScan <= 0)
            {
                complete = false;
                break;
            }

            // Identifier-first paging: one bounded id sweep, then ordered fetches by object
            // id. Offset paging over an unordered result would be unstable across pages.
            var objectIds = await deps.FeatureReader.QueryObjectIdsAsync(
                descriptor.LayerId,
                new FeatureQuery { Limit = remainingScan + 1 },
                cancellationToken).ConfigureAwait(false);

            var pageIds = objectIds.AsEnumerable();
            if (objectIds.Length > remainingScan)
            {
                complete = false;
                pageIds = objectIds.Take(remainingScan);
            }

            var ordered = pageIds.Order().ToArray();
            scanned += ordered.Length;

            for (var offset = 0; offset < ordered.Length; offset += options.SnapshotPageSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chunk = ordered.Skip(offset).Take(options.SnapshotPageSize).ToImmutableArray();
                var page = await deps.FeatureReader.QueryAsync(
                    descriptor.LayerId,
                    new FeatureQuery
                    {
                        ObjectIds = chunk,
                        IncludeNullGeometry = true,
                        Limit = chunk.Length
                    },
                    cancellationToken).ConfigureAwait(false);

                foreach (var feature in page.Items)
                {
                    if (emitted >= options.MaxSnapshotFeatures)
                    {
                        // Rows left inside the current page: definitively truncated.
                        capped = true;
                        complete = false;
                        break;
                    }

                    var frame = BuildSnapshotFeatureFrame(
                        feature,
                        serviceId,
                        descriptor.LayerId,
                        layerSrid,
                        filter,
                        snapshotId,
                        subscriptionId,
                        baselineCursor,
                        deps.SessionManager,
                        sessionId,
                        subscriptionGeneration);
                    if (frame is null)
                    {
                        continue;
                    }

                    await sink.WriteFeatureAsync(frame, cancellationToken).ConfigureAwait(false);
                    emitted++;
                }

                if (capped)
                {
                    break;
                }

                if (emitted >= options.MaxSnapshotFeatures)
                {
                    // The cap landed exactly on a page boundary, so no row was visibly
                    // dropped. Ids beyond this page are still unread, so the baseline is
                    // authoritative only when this page was the layer's last one; the
                    // remaining-layer case is handled at the top of the layer loop.
                    capped = true;
                    if (offset + options.SnapshotPageSize < ordered.Length)
                    {
                        complete = false;
                    }

                    break;
                }
            }
        }

        await sink.WriteEndAsync(
            new FeatureStreamSnapshotEndFrame
            {
                SnapshotId = snapshotId,
                SubscriptionId = subscriptionId,
                Sequence = NextSequence(deps.SessionManager, sessionId, subscriptionId, subscriptionGeneration),
                Cursor = baselineCursor,
                FeatureCount = emitted,
                Complete = complete
            },
            cancellationToken).ConfigureAwait(false);

        FeatureStreamLog.SnapshotEmitted(logger, sessionId, subscriptionId, reason, emitted, baselineCursor, complete);
        return new SnapshotEmitResult(baselineCursor, emitted, complete);
    }

    /// <summary>
    /// Projects one stored feature into a snapshot frame, applying the subscription's own
    /// admission predicate so the baseline and the delta stream agree on membership.
    /// Returns null when the feature is not admitted by the subscription.
    /// </summary>
    private static FeatureStreamSnapshotFeatureFrame? BuildSnapshotFeatureFrame(
        Feature feature,
        string serviceId,
        int layerId,
        int? layerSrid,
        StreamSubscriptionFilter filter,
        string snapshotId,
        string subscriptionId,
        long baselineCursor,
        FeatureStreamSessionManager sessionManager,
        Guid sessionId,
        long subscriptionGeneration)
    {
        var objectId = feature.ObjectId ?? feature.Id;
        var enrichment = FeatureChangeEventEnrichment.FromFeatureSnapshot(feature, layerSrid);

        // Evaluate the subscription predicate against an insert-shaped envelope: the same
        // representation and the same rules the live broadcast path uses.
        var probe = new FeatureStreamEnvelope
        {
            EventId = snapshotId,
            Cursor = baselineCursor,
            Timestamp = DateTimeOffset.UtcNow,
            ServiceId = serviceId,
            LayerId = layerId,
            ObjectId = objectId,
            FeatureId = objectId.ToString(CultureInfo.InvariantCulture),
            Operation = "insert",
            Protocol = "snapshot",
            RequestId = snapshotId,
            SubscriptionId = subscriptionId
        };

        if (!filter.Matches(probe, enrichment.GeometryEnvelope, enrichment.PropertiesJson))
        {
            return null;
        }

        var geometry = FeatureStreamPublisher.ParseJsonElement(enrichment.GeometryJson);
        var geometryCrs = geometry.HasValue && enrichment.GeometrySrid.HasValue
            ? string.Concat("EPSG:", enrichment.GeometrySrid.Value.ToString(CultureInfo.InvariantCulture))
            : null;

        return new FeatureStreamSnapshotFeatureFrame
        {
            SnapshotId = snapshotId,
            SubscriptionId = subscriptionId,
            Sequence = NextSequence(sessionManager, sessionId, subscriptionId, subscriptionGeneration),
            Cursor = baselineCursor,
            ServiceId = serviceId,
            LayerId = layerId,
            FeatureId = probe.FeatureId,
            ObjectId = objectId,
            Geometry = geometry,
            GeometryCrs = geometryCrs,
            Attributes = FeatureStreamPublisher.ParseAttributes(enrichment.PropertiesJson)
        };
    }

    /// <summary>
    /// Allocates the next subscription-local sequence, falling back to a non-negative value
    /// when the subscription has already been torn down (the frame is about to fail to
    /// write anyway; the contract forbids emitting a negative sequence).
    /// </summary>
    private static long NextSequence(
        FeatureStreamSessionManager sessionManager,
        Guid sessionId,
        string subscriptionId,
        long generation)
    {
        var sequence = sessionManager.NextSubscriptionSequence(sessionId, subscriptionId, generation);
        return sequence < 0 ? 0 : sequence;
    }
}

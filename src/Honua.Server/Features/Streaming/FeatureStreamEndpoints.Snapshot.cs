// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Models;

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
    private const string SnapshotThenDeltaModeValue = "snapshot-then-delta";
    private const string DeltaModeValue = "delta";

    /// <summary>
    /// True for both snapshot framings. Every baseline decision — scope validation,
    /// replacement-snapshot detection, delta resumption from the boundary cursor — is
    /// identical between them; only the frame layout differs.
    /// </summary>
    private static bool IsSnapshotMode(FeatureStreamSubscriptionMode mode)
        => mode is FeatureStreamSubscriptionMode.Snapshot or FeatureStreamSubscriptionMode.SnapshotThenDelta;

    /// <summary>
    /// Fixed per-frame cost of a <c>snapshot-feature</c> frame excluding its geometry and
    /// attributes: frame type, snapshot/subscription ids, sequence, cursor, service/layer
    /// identity, feature/object id, CRS, JSON punctuation, and the SSE event framing. Used to
    /// hold the baseline inside <see cref="FeatureStreamOptions.MaxSnapshotBytes"/> without
    /// serializing every frame twice.
    /// </summary>
    private const int SnapshotFrameOverheadBytes = 256;

    /// <summary>
    /// Client-safe descriptions of why a baseline ended incomplete. They name the bound that was
    /// hit — every one of them is advertised on the capability document — without exposing
    /// provider internals, SQL, or exception text.
    /// </summary>
    private static class SnapshotIncompleteReasons
    {
        public const string FeatureCap =
            "the baseline reached the advertised maxSnapshotFeatures cap";

        public const string ScanBound =
            "the baseline reached the advertised maxSnapshotScanRows bound";

        public const string ByteBudget =
            "the baseline reached the advertised maxSnapshotBytes payload budget";

        public const string LayerRemoved =
            "a layer in the subscription scope left the catalog while the baseline was being read";

        public const string RetentionOvertaken =
            "the replay window was trimmed past the baseline cursor";
    }

    /// <summary>
    /// Result of emitting one baseline snapshot. <paramref name="IncompleteReason"/> is null when
    /// the baseline is whole and otherwise carries a client-safe description of the bound that
    /// truncated it, so the terminating frame can name the condition instead of closing silently.
    /// </summary>
    private readonly record struct SnapshotEmitResult(
        long BaselineCursor,
        long FeatureCount,
        bool Complete,
        string? IncompleteReason = null);

    /// <summary>
    /// Transport-neutral sink for snapshot frames. Keeps the baseline emitter free of any
    /// SSE/WebSocket framing detail so both transports expose identical semantics.
    /// </summary>
    private abstract class FeatureStreamSnapshotSink
    {
        /// <summary>
        /// True when the sink emits the whole baseline as one frame. Sequence allocation is
        /// framing-aware because of this: a batched baseline must consume exactly ONE
        /// subscription-local sequence, or the numbers allocated to buffered frames that are
        /// never written would be burned and the first delta would land at a gap.
        /// </summary>
        public virtual bool BatchesFrames => false;

        public abstract Task WriteBeginAsync(FeatureStreamSnapshotBeginFrame frame, CancellationToken cancellationToken);

        public abstract Task WriteFeatureAsync(FeatureStreamSnapshotFeatureFrame frame, CancellationToken cancellationToken);

        public abstract Task WriteEndAsync(FeatureStreamSnapshotEndFrame frame, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Buffers the streamed frames a baseline produces and writes them as a single
    /// <c>snapshot</c> frame once the baseline is whole. Buffering is bounded by the same
    /// <see cref="FeatureStreamOptions.MaxSnapshotFeatures"/> cap that bounds the streamed
    /// framing, so a batched baseline can never buffer more than a streamed one reads.
    /// </summary>
    private abstract class BatchedSnapshotSink : FeatureStreamSnapshotSink
    {
        private readonly List<FeatureStreamSnapshotFeature> _features = [];
        private FeatureStreamSnapshotBeginFrame? _begin;

        public sealed override bool BatchesFrames => true;

        public sealed override Task WriteBeginAsync(FeatureStreamSnapshotBeginFrame frame, CancellationToken cancellationToken)
        {
            _begin = frame;
            return Task.CompletedTask;
        }

        public sealed override Task WriteFeatureAsync(FeatureStreamSnapshotFeatureFrame frame, CancellationToken cancellationToken)
        {
            _features.Add(new FeatureStreamSnapshotFeature
            {
                Id = frame.FeatureId,
                SourceId = frame.ServiceId,
                LayerId = frame.LayerId,
                GeometryCrs = frame.GeometryCrs,
                Feature = new FeatureStreamSnapshotGeoJsonFeature
                {
                    Id = frame.FeatureId,
                    Geometry = frame.Geometry,
                    Properties = frame.Attributes
                }
            });
            return Task.CompletedTask;
        }

        public sealed override Task WriteEndAsync(FeatureStreamSnapshotEndFrame frame, CancellationToken cancellationToken)
        {
            // The begin frame is written by the emitter before any feature read, so it is
            // always present by the time the end frame arrives. Fail closed rather than
            // emitting a baseline whose boundary metadata was never captured.
            var begin = _begin
                ?? throw new InvalidOperationException("A batched snapshot cannot be completed before it was begun.");

            return WriteSnapshotAsync(
                new FeatureStreamSnapshotFrame
                {
                    SnapshotId = frame.SnapshotId,
                    SubscriptionId = frame.SubscriptionId,
                    Sequence = frame.Sequence,
                    Cursor = frame.Cursor,
                    Reason = begin.Reason,
                    ServiceId = begin.ServiceId,
                    LayerIds = begin.LayerIds,
                    FeatureCount = frame.FeatureCount,
                    Complete = frame.Complete,
                    Features = [.. _features]
                },
                cancellationToken);
        }

        protected abstract Task WriteSnapshotAsync(FeatureStreamSnapshotFrame frame, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Writes a batched baseline as one SSE <c>snapshot</c> event.
    /// </summary>
    /// <remarks>
    /// A batched baseline is atomic on the wire — the client either parses the whole frame
    /// or none of it — so, unlike the streamed framing, there is no partial-baseline state a
    /// reconnect could resume from. The complete baseline still checkpoints only when it is
    /// whole: a truncated baseline omits features no later delta will mention, so resuming
    /// from its cursor would leave the client permanently missing them.
    /// </remarks>
    private sealed class BatchedSseSnapshotSink(HttpResponse response) : BatchedSnapshotSink
    {
        protected override async Task WriteSnapshotAsync(FeatureStreamSnapshotFrame frame, CancellationToken cancellationToken)
        {
            await WriteSseEventAsync(
                response,
                "snapshot",
                frame,
                FeatureStreamJsonContext.Default.FeatureStreamSnapshotFrame,
                frame.Complete ? frame.Cursor : null,
                cancellationToken).ConfigureAwait(false);
            await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes a batched baseline as one WebSocket <c>snapshot</c> frame.
    /// </summary>
    private sealed class BatchedWebSocketSnapshotSink(WebSocket webSocket, SemaphoreSlim writeLock) : BatchedSnapshotSink
    {
        protected override Task WriteSnapshotAsync(FeatureStreamSnapshotFrame frame, CancellationToken cancellationToken)
            => SendWebSocketJsonAsync(
                webSocket,
                writeLock,
                JsonSerializer.SerializeToUtf8Bytes(frame, FeatureStreamJsonContext.Default.FeatureStreamSnapshotFrame),
                cancellationToken);
    }

    /// <summary>
    /// Selects the SSE baseline sink for the requested framing.
    /// </summary>
    private static FeatureStreamSnapshotSink CreateSseSnapshotSink(
        FeatureStreamSubscriptionMode mode,
        HttpResponse response)
        => mode == FeatureStreamSubscriptionMode.SnapshotThenDelta
            ? new BatchedSseSnapshotSink(response)
            : new SseSnapshotSink(response);

    /// <summary>
    /// Selects the WebSocket baseline sink for the requested framing.
    /// </summary>
    private static FeatureStreamSnapshotSink CreateWebSocketSnapshotSink(
        FeatureStreamSubscriptionMode mode,
        WebSocket webSocket,
        SemaphoreSlim writeLock)
        => mode == FeatureStreamSubscriptionMode.SnapshotThenDelta
            ? new BatchedWebSocketSnapshotSink(webSocket, writeLock)
            : new WebSocketSnapshotSink(webSocket, writeLock);

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

        if (value.Equals(SnapshotModeValue, StringComparison.OrdinalIgnoreCase))
        {
            mode = FeatureStreamSubscriptionMode.Snapshot;
            return true;
        }

        if (value.Equals(SnapshotThenDeltaModeValue, StringComparison.OrdinalIgnoreCase))
        {
            mode = FeatureStreamSubscriptionMode.SnapshotThenDelta;
            return true;
        }

        error = $"Unsupported subscription mode '{value}'. Supported modes: delta, snapshot, snapshot-then-delta.";
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
        if (!IsSnapshotMode(mode))
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
    /// Verifies, BEFORE the response is started, that every layer in a snapshot subscription can
    /// actually produce a baseline. Returns null when the subscription is servable, or a typed
    /// RFC 7807 problem naming the condition.
    /// </summary>
    /// <remarks>
    /// A stream cannot report a failure as a problem document once its first frame has been
    /// written — the status line is already committed, so a baseline that dies mid-emission
    /// reaches the client as a truncated body or, behind a buffering gateway, as that gateway's
    /// own untyped 500 (honua-server#3181 REQ-002). The only place a typed response is still
    /// possible is ahead of the handshake, so the two failure modes that are cheap to detect —
    /// a layer that has left the catalog, and a backing store that will not accept the baseline
    /// read at all — are detected here with one bounded probe per layer rather than discovered
    /// halfway through the emission. Failures raised by the probe are logged with their
    /// exception and reported to the client as a condition only, never as provider text.
    /// </remarks>
    private static async Task<IResult?> ValidateSnapshotServabilityAsync(
        FeatureStreamDependencies deps,
        ILogger logger,
        HttpContext context,
        FeatureStreamSubscriptionMode mode,
        IStreamSubscriptionFilter? subscriptionFilter)
    {
        if (!IsSnapshotMode(mode) ||
            subscriptionFilter is not StreamSubscriptionFilter filter ||
            filter.LayerIds is not { Length: > 0 } layerIds)
        {
            return null;
        }

        var graph = await deps.MetadataV2GraphProvider.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
        var service = filter.ServiceId is null ? null : ResolveStreamService(graph, filter.ServiceId);

        foreach (var layerId in layerIds)
        {
            var descriptor = ResolveStreamLayer(graph, service, layerId);
            if (descriptor is null)
            {
                FeatureStreamLog.SnapshotUnservable(logger, layerId, "layer-not-in-catalog");
                return StandardErrorHelpers.CreateServiceUnavailable(
                    context,
                    $"A baseline snapshot cannot be served for layer {layerId.ToString(CultureInfo.InvariantCulture)}: the layer is no longer present in the catalog.");
            }

            try
            {
                await deps.FeatureReader.QueryObjectIdsAsync(
                    descriptor.LayerId,
                    new FeatureQuery { Limit = 1 },
                    context.RequestAborted).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                FeatureStreamLog.SnapshotProbeFailed(logger, layerId, exception);
                return StandardErrorHelpers.CreateServiceUnavailable(
                    context,
                    $"A baseline snapshot cannot be served for layer {layerId.ToString(CultureInfo.InvariantCulture)}: its backing store did not accept the baseline read.");
            }
        }

        return null;
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

        var window = await eventStore.GetRetentionWindowAsync(cancellationToken).ConfigureAwait(false);
        if (cursor > window.CurrentCursor)
        {
            return FeatureStreamSnapshotReasons.CursorInvalid;
        }

        // A known-empty window is replayable only from its current boundary. Older clients
        // still need expired events and must take a replacement snapshot; an indeterminate
        // window always fails closed.
        return window.HasGapAfter(cursor)
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

        // A streamed baseline allocates one subscription-local sequence per emitted frame; a
        // batched baseline is a single frame and must therefore consume exactly one, or the
        // numbers allocated to buffered-but-never-written frames would be burned and the
        // first delta would arrive at what the client reads as a gap.
        long? batchedSequence = null;
        long AllocateSequence()
        {
            if (!sink.BatchesFrames)
            {
                return NextSequence(deps.SessionManager, sessionId, subscriptionId, subscriptionGeneration);
            }

            batchedSequence ??= NextSequence(deps.SessionManager, sessionId, subscriptionId, subscriptionGeneration);
            return batchedSequence.Value;
        }

        await sink.WriteBeginAsync(
            new FeatureStreamSnapshotBeginFrame
            {
                SnapshotId = snapshotId,
                SubscriptionId = subscriptionId,
                Sequence = AllocateSequence(),
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
        long emittedBytes = 0;
        var complete = true;
        string? incompleteReason = null;

        // Set once the feature cap or the payload budget is reached. Anything still unread after
        // that point — later pages of the current layer, or any remaining layer — makes the
        // baseline non-authoritative, including when the bound lands exactly on a page boundary.
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
                incompleteReason ??= SnapshotIncompleteReasons.LayerRemoved;
                continue;
            }

            var layerSrid = descriptor.Resource.ReadSrid();
            var serviceId = filter.ServiceId ?? descriptor.Service?.Metadata.Name ?? string.Empty;
            var remainingScan = options.MaxSnapshotScanRows - (int)scanned;
            if (remainingScan <= 0)
            {
                complete = false;
                incompleteReason ??= SnapshotIncompleteReasons.ScanBound;
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
                incompleteReason ??= SnapshotIncompleteReasons.ScanBound;
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
                        incompleteReason ??= SnapshotIncompleteReasons.FeatureCap;
                        break;
                    }

                    var projection = ProjectSnapshotFeature(
                        feature,
                        serviceId,
                        descriptor.LayerId,
                        layerSrid,
                        filter,
                        snapshotId,
                        subscriptionId,
                        baselineCursor);
                    if (projection is null)
                    {
                        continue;
                    }

                    // Payload budget. The projection is measured BEFORE a sequence is allocated:
                    // a frame that does not fit must consume no subscription-local sequence, or
                    // the first delta after the baseline would land at what the client reads as
                    // a gap. Truncating here is the same fail-closed outcome as the feature cap,
                    // and it is the bound that keeps a baseline deliverable through a response
                    // path that buffers (honua-server#3181).
                    if (emittedBytes + projection.Value.EstimatedBytes > options.MaxSnapshotBytes)
                    {
                        capped = true;
                        complete = false;
                        incompleteReason ??= SnapshotIncompleteReasons.ByteBudget;
                        break;
                    }

                    await sink.WriteFeatureAsync(
                        projection.Value.ToFrame(AllocateSequence()),
                        cancellationToken).ConfigureAwait(false);
                    emitted++;
                    emittedBytes += projection.Value.EstimatedBytes;
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
                        incompleteReason ??= SnapshotIncompleteReasons.FeatureCap;
                    }

                    break;
                }
            }
        }

        // Revalidate the replay window AFTER the scan. The baseline read is not
        // transactional, so a scan slow enough to be overtaken by more than MaxRetainedEvents
        // mutations has its own successor events trimmed out from under it. Two things then go
        // wrong together and neither is individually visible: a feature read early in the scan
        // can be stale, and the delta replay silently starts at the NEW oldest cursor instead
        // of failing — so the client converges on a baseline that is missing changes while
        // snapshot-end tells it complete: true. Re-reading the retained floor here is the only
        // point where the gap is observable (honua-server#3038 review).
        //
        // Reported as incomplete rather than raised: the callers already treat an incomplete
        // baseline as non-resumable and end the stream so the client reconnects and takes a
        // fresh snapshot, which is exactly the required recovery.
        if (complete)
        {
            var retentionWindow = await deps.EventStore
                .GetRetentionWindowAsync(cancellationToken).ConfigureAwait(false);
            if (retentionWindow.HasGapAfter(baselineCursor))
            {
                complete = false;
                incompleteReason ??= SnapshotIncompleteReasons.RetentionOvertaken;
                var oldestRetained = retentionWindow.IsEmpty
                    ? long.MaxValue
                    : retentionWindow.OldestRetainedCursor;
                FeatureStreamLog.SnapshotReplayWindowTrimmed(
                    logger, sessionId, subscriptionId, baselineCursor, oldestRetained);
            }
        }

        await sink.WriteEndAsync(
            new FeatureStreamSnapshotEndFrame
            {
                SnapshotId = snapshotId,
                SubscriptionId = subscriptionId,
                Sequence = AllocateSequence(),
                Cursor = baselineCursor,
                FeatureCount = emitted,
                Complete = complete
            },
            cancellationToken).ConfigureAwait(false);

        FeatureStreamLog.SnapshotEmitted(logger, sessionId, subscriptionId, reason, emitted, baselineCursor, complete);
        return new SnapshotEmitResult(baselineCursor, emitted, complete, complete ? null : incompleteReason);
    }

    /// <summary>
    /// One admitted baseline feature, projected and measured but not yet sequenced. Keeping the
    /// sequence out of the projection is what lets the caller weigh the frame against the
    /// payload budget before committing a subscription-local sequence number to it.
    /// </summary>
    private readonly record struct SnapshotFeatureProjection(
        string SnapshotId,
        string SubscriptionId,
        long Cursor,
        string ServiceId,
        int LayerId,
        string FeatureId,
        long ObjectId,
        JsonElement? Geometry,
        string? GeometryCrs,
        Dictionary<string, JsonElement>? Attributes,
        int EstimatedBytes)
    {
        public FeatureStreamSnapshotFeatureFrame ToFrame(long sequence)
            => new()
            {
                SnapshotId = SnapshotId,
                SubscriptionId = SubscriptionId,
                Sequence = sequence,
                Cursor = Cursor,
                ServiceId = ServiceId,
                LayerId = LayerId,
                FeatureId = FeatureId,
                ObjectId = ObjectId,
                Geometry = Geometry,
                GeometryCrs = GeometryCrs,
                Attributes = Attributes
            };
    }

    /// <summary>
    /// Projects one stored feature into a measured snapshot-frame payload, applying the
    /// subscription's own admission predicate so the baseline and the delta stream agree on
    /// membership. Returns null when the feature is not admitted by the subscription.
    /// </summary>
    private static SnapshotFeatureProjection? ProjectSnapshotFeature(
        Feature feature,
        string serviceId,
        int layerId,
        int? layerSrid,
        StreamSubscriptionFilter filter,
        string snapshotId,
        string subscriptionId,
        long baselineCursor)
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

        // The serialized geometry and properties dominate the frame; everything else is the
        // fixed envelope. Measuring the source JSON avoids serializing each frame twice on the
        // baseline hot path while staying within a few percent of the emitted size.
        var estimatedBytes = SnapshotFrameOverheadBytes
            + (geometry.HasValue ? enrichment.GeometryJson?.Length ?? 0 : 0)
            + (enrichment.PropertiesJson?.Length ?? 0);

        return new SnapshotFeatureProjection(
            snapshotId,
            subscriptionId,
            baselineCursor,
            serviceId,
            layerId,
            probe.FeatureId,
            objectId,
            geometry,
            geometryCrs,
            FeatureStreamPublisher.ParseAttributes(enrichment.PropertiesJson),
            estimatedBytes);
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

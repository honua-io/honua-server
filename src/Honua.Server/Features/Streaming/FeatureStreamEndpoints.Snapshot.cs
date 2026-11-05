// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Models;

namespace Honua.Server.Features.Streaming;

// The baseline cursor is captured before the read, a bound ends the snapshot incomplete, and the handoff is at-least-once.
internal static partial class FeatureStreamEndpoints
{
    private const string SnapshotModeValue = "snapshot";
    private const string SnapshotThenDeltaModeValue = "snapshot-then-delta";
    private const string DeltaModeValue = "delta";

    private static bool IsSnapshotMode(FeatureStreamSubscriptionMode mode)
        => mode is FeatureStreamSubscriptionMode.Snapshot or FeatureStreamSubscriptionMode.SnapshotThenDelta;

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

    private readonly record struct SnapshotEmitResult(
        long BaselineCursor,
        long FeatureCount,
        bool Complete,
        string? IncompleteReason = null);

    private abstract class FeatureStreamSnapshotSink
    {
        // A batched baseline consumes one sequence, or the first delta lands on a gap.
        public virtual bool BatchesFrames => false;

        // Streamed sinks reserve the end sequence at its maximum width so the emitted value cannot exceed the budget.
        public abstract int MeasureFixedBytes(
            FeatureStreamSnapshotBeginFrame begin,
            FeatureStreamSnapshotEndFrame end);

        public abstract int MeasureFeatureBytes(FeatureStreamSnapshotFeatureFrame frame, long featureCount);

        public abstract Task WriteBeginAsync(FeatureStreamSnapshotBeginFrame frame, CancellationToken cancellationToken);

        public abstract Task WriteFeatureAsync(FeatureStreamSnapshotFeatureFrame frame, CancellationToken cancellationToken);

        public abstract Task WriteEndAsync(FeatureStreamSnapshotEndFrame frame, CancellationToken cancellationToken);
    }

    // A batched baseline cannot buffer more than a streamed baseline reads.
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

        public sealed override int MeasureFeatureBytes(FeatureStreamSnapshotFeatureFrame frame, long featureCount)
        {
            var featureBytes = JsonSerializer.SerializeToUtf8Bytes(
                ToBatchedFeature(frame),
                FeatureStreamJsonContext.Default.FeatureStreamSnapshotFeature).Length;
            var commaBytes = featureCount > 1 ? 1 : 0;
            var previousDigits = (featureCount - 1).ToString(CultureInfo.InvariantCulture).Length;
            var currentDigits = featureCount.ToString(CultureInfo.InvariantCulture).Length;
            return featureBytes + commaBytes + currentDigits - previousDigits;
        }

        public sealed override Task WriteFeatureAsync(FeatureStreamSnapshotFeatureFrame frame, CancellationToken cancellationToken)
        {
            _features.Add(ToBatchedFeature(frame));
            return Task.CompletedTask;
        }

        protected static FeatureStreamSnapshotFrame CreateEmptyMeasuredFrame(
            FeatureStreamSnapshotBeginFrame begin,
            FeatureStreamSnapshotEndFrame end)
            => new()
            {
                SnapshotId = end.SnapshotId,
                SubscriptionId = end.SubscriptionId,
                Sequence = end.Sequence,
                Cursor = end.Cursor,
                Reason = begin.Reason,
                ServiceId = begin.ServiceId,
                LayerIds = begin.LayerIds,
                FeatureCount = 0,
                // False is one byte longer than true, so it safely reserves either outcome.
                Complete = false,
                Features = [],
                // The emitted frame receives a fresh UtcNow timestamp. Reserve the longest
                // DateTimeOffset JSON representation so fractional-second trimming can never
                // make the later payload larger than the measured empty envelope.
                Timestamp = DateTimeOffset.MaxValue
            };

        private static FeatureStreamSnapshotFeature ToBatchedFeature(FeatureStreamSnapshotFeatureFrame frame)
            => new()
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
            };

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

    private sealed class BatchedSseSnapshotSink(HttpResponse response) : BatchedSnapshotSink
    {
        public override int MeasureFixedBytes(
            FeatureStreamSnapshotBeginFrame begin,
            FeatureStreamSnapshotEndFrame end)
            => MeasureSseEventBytes(
                "snapshot",
                CreateEmptyMeasuredFrame(begin, end),
                FeatureStreamJsonContext.Default.FeatureStreamSnapshotFrame,
                end.Cursor);

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

    private sealed class BatchedWebSocketSnapshotSink(WebSocket webSocket, SemaphoreSlim writeLock) : BatchedSnapshotSink
    {
        public override int MeasureFixedBytes(
            FeatureStreamSnapshotBeginFrame begin,
            FeatureStreamSnapshotEndFrame end)
            // The frame grows as features are appended and may cross either variable-length
            // WebSocket header boundary. Reserve the maximum server-frame header up front;
            // the remaining increments are then exactly the JSON growth.
            => JsonSerializer.SerializeToUtf8Bytes(
                CreateEmptyMeasuredFrame(begin, end),
                FeatureStreamJsonContext.Default.FeatureStreamSnapshotFrame).Length
                + 10;

        protected override Task WriteSnapshotAsync(FeatureStreamSnapshotFrame frame, CancellationToken cancellationToken)
            => SendWebSocketJsonAsync(
                webSocket,
                writeLock,
                JsonSerializer.SerializeToUtf8Bytes(frame, FeatureStreamJsonContext.Default.FeatureStreamSnapshotFrame),
                cancellationToken);
    }

    private static FeatureStreamSnapshotSink CreateSseSnapshotSink(
        FeatureStreamSubscriptionMode mode,
        HttpResponse response)
        => mode == FeatureStreamSubscriptionMode.SnapshotThenDelta
            ? new BatchedSseSnapshotSink(response)
            : new SseSnapshotSink(response);

    private static FeatureStreamSnapshotSink CreateWebSocketSnapshotSink(
        FeatureStreamSubscriptionMode mode,
        WebSocket webSocket,
        SemaphoreSlim writeLock)
        => mode == FeatureStreamSubscriptionMode.SnapshotThenDelta
            ? new BatchedWebSocketSnapshotSink(webSocket, writeLock)
            : new WebSocketSnapshotSink(webSocket, writeLock);

    // Only a complete snapshot-end carries an event id, so an interrupted baseline reconnects into another snapshot.
    private sealed class SseSnapshotSink(HttpResponse response) : FeatureStreamSnapshotSink
    {
        public override int MeasureFixedBytes(
            FeatureStreamSnapshotBeginFrame begin,
            FeatureStreamSnapshotEndFrame end)
            => MeasureSseEventBytes(
                "snapshot-begin",
                begin,
                FeatureStreamJsonContext.Default.FeatureStreamSnapshotBeginFrame,
                null)
                + MeasureSseEventBytes(
                    "snapshot-end",
                    end,
                    FeatureStreamJsonContext.Default.FeatureStreamSnapshotEndFrame,
                    end.Cursor);

        public override int MeasureFeatureBytes(FeatureStreamSnapshotFeatureFrame frame, long featureCount)
        {
            var previousDigits = (featureCount - 1).ToString(CultureInfo.InvariantCulture).Length;
            var currentDigits = featureCount.ToString(CultureInfo.InvariantCulture).Length;
            return MeasureSseEventBytes(
                "snapshot-feature",
                frame,
                FeatureStreamJsonContext.Default.FeatureStreamSnapshotFeatureFrame,
                null)
                + currentDigits - previousDigits;
        }

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
        public override int MeasureFixedBytes(
            FeatureStreamSnapshotBeginFrame begin,
            FeatureStreamSnapshotEndFrame end)
            => MeasureWebSocketMessageBytes(
                JsonSerializer.SerializeToUtf8Bytes(
                    begin,
                    FeatureStreamJsonContext.Default.FeatureStreamSnapshotBeginFrame).Length)
                + MeasureWebSocketMessageBytes(
                    JsonSerializer.SerializeToUtf8Bytes(
                        end,
                        FeatureStreamJsonContext.Default.FeatureStreamSnapshotEndFrame).Length);

        public override int MeasureFeatureBytes(FeatureStreamSnapshotFeatureFrame frame, long featureCount)
        {
            var previousDigits = (featureCount - 1).ToString(CultureInfo.InvariantCulture).Length;
            var currentDigits = featureCount.ToString(CultureInfo.InvariantCulture).Length;
            return MeasureWebSocketMessageBytes(
                JsonSerializer.SerializeToUtf8Bytes(
                    frame,
                    FeatureStreamJsonContext.Default.FeatureStreamSnapshotFeatureFrame).Length)
                + currentDigits - previousDigits;
        }

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

    private static int MeasureWebSocketMessageBytes(int payloadBytes)
    {
        // Server-to-client frames are not masked. Each snapshot envelope item is sent as its
        // own final text message, so charge its RFC 6455 header as well as the JSON payload.
        var headerBytes = payloadBytes switch
        {
            <= 125 => 2,
            <= ushort.MaxValue => 4,
            _ => 10
        };

        return payloadBytes + headerBytes;
    }

    private static int MeasureSseEventBytes<T>(
        string eventName,
        T payload,
        JsonTypeInfo<T> jsonTypeInfo,
        long? id)
    {
        var json = JsonSerializer.Serialize(payload, jsonTypeInfo);
        var frame = id.HasValue
            ? string.Concat(
                "id: ", id.Value.ToString(CultureInfo.InvariantCulture), "\n",
                "event: ", eventName, "\n",
                "data: ", json, "\n\n")
            : string.Concat("event: ", eventName, "\n", "data: ", json, "\n\n");
        return Encoding.UTF8.GetByteCount(frame);
    }

    // An unrecognized mode is rejected and is never downgraded to change-only delivery.
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

    private static string? ValidateSnapshotScope(
        FeatureStreamSubscriptionMode mode,
        IStreamSubscriptionFilter? filter)
    {
        if (!IsSnapshotMode(mode))
        {
            return null;
        }

        if (filter is not StreamSubscriptionFilter scoped ||
            !scoped.HasExplicitLayerScope ||
            scoped.LayerIds is not { Length: > 0 })
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
            ? "snapshot subscriptions cannot be combined with row-level security, bbox, attribute, or temporal filters: a feature that leaves the filter after an update produces no delta, so the baseline could not be kept convergent. Use mode=delta with the filter, or mode=snapshot scoped by service/layer only."
            : null;
    }

    // The baseline is probed before the response starts, and the client is told the condition rather than provider text.
    private static async Task<IResult?> ValidateSnapshotServabilityAsync(
        FeatureStreamDependencies deps,
        ILogger logger,
        HttpContext context,
        bool isSse,
        FeatureStreamSubscriptionMode mode,
        IStreamSubscriptionFilter? subscriptionFilter)
    {
        if (!IsSnapshotMode(mode) ||
            subscriptionFilter is not StreamSubscriptionFilter filter ||
            filter.LayerIds is not { Length: > 0 } layerIds)
        {
            return null;
        }

        // A reconnect whose cursor remains inside the retained event window takes the delta-only
        // replay path and never reads the feature store. Do not let a temporary feature-read
        // outage block a resume the event store can satisfy by itself (#3206 review).
        var cursorValue = context.Request.Query["cursor"].ToString();
        long? cursor = long.TryParse(cursorValue, CultureInfo.InvariantCulture, out var parsedCursor)
            ? parsedCursor
            : null;
        if (!cursor.HasValue && isSse)
        {
            var lastEventId = context.Request.Headers["Last-Event-ID"].ToString();
            if (long.TryParse(lastEventId, CultureInfo.InvariantCulture, out var parsedLastEventId))
            {
                cursor = parsedLastEventId;
            }
        }

        if (cursor.HasValue &&
            await ResolveResnapshotReasonAsync(
                deps.EventStore,
                cursor.Value,
                context.RequestAborted).ConfigureAwait(false) is null)
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
                var probeIds = await deps.FeatureReader.QueryObjectIdsAsync(
                    descriptor.LayerId,
                    new FeatureQuery { Limit = 1 },
                    context.RequestAborted).ConfigureAwait(false);
                if (!probeIds.IsDefaultOrEmpty)
                {
                    // The id sweep can succeed even when geometry/attribute projection or row
                    // materialization cannot. Exercise the same bounded feature-read path the
                    // baseline uses while the response can still become a typed problem.
                    await deps.FeatureReader.QueryAsync(
                        descriptor.LayerId,
                        new FeatureQuery
                        {
                            ObjectIds = ImmutableArray.Create(probeIds[0]),
                            IncludeNullGeometry = true,
                            Limit = 1
                        },
                        context.RequestAborted).ConfigureAwait(false);
                }
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
                    $"A baseline snapshot cannot be served for layer {layerId.ToString(CultureInfo.InvariantCulture)}: its backing store did not accept the baseline read.",
                    retryable: true);
            }
        }

        return null;
    }

    // An explicit cursor of zero is a real resume, and a trimmed window cannot be served as deltas alone.
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
        var snapshotServiceId = filter.ResolvedServiceId ?? filter.ServiceId;

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

        var beginFrame = new FeatureStreamSnapshotBeginFrame
        {
            SnapshotId = snapshotId,
            SubscriptionId = subscriptionId,
            Sequence = AllocateSequence(),
            Cursor = baselineCursor,
            Reason = reason,
            ServiceId = snapshotServiceId,
            LayerIds = layerIds
        };
        var measuredEndFrame = new FeatureStreamSnapshotEndFrame
        {
            SnapshotId = snapshotId,
            SubscriptionId = subscriptionId,
            Sequence = sink.BatchesFrames ? beginFrame.Sequence : long.MaxValue,
            Cursor = baselineCursor,
            FeatureCount = 0,
            Complete = false
        };
        var fixedBytes = sink.MeasureFixedBytes(beginFrame, measuredEndFrame);
        if (fixedBytes > options.MaxSnapshotBytes)
        {
            return new SnapshotEmitResult(
                baselineCursor,
                0,
                Complete: false,
                SnapshotIncompleteReasons.ByteBudget);
        }

        await sink.WriteBeginAsync(beginFrame, cancellationToken).ConfigureAwait(false);

        var graph = await deps.MetadataV2GraphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var service = snapshotServiceId is null ? null : ResolveStreamService(graph, snapshotServiceId);

        long emitted = 0;
        long scanned = 0;
        long emittedBytes = fixedBytes;
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
            var serviceId = snapshotServiceId ?? descriptor.Service?.Metadata.Id ?? string.Empty;
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
                    var measuredFrame = projection.Value.ToFrame(long.MaxValue);
                    var featureBytes = sink.MeasureFeatureBytes(measuredFrame, emitted + 1);
                    if (emittedBytes + featureBytes > options.MaxSnapshotBytes)
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
                    emittedBytes += featureBytes;
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
        Dictionary<string, JsonElement>? Attributes)
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
            FeatureStreamPublisher.ParseAttributes(enrichment.PropertiesJson));
    }

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

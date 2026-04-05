// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Streaming;

/// <summary>
/// Feature-change streaming endpoints supporting WebSocket and SSE transports
/// on a single logical route. Admin endpoints expose session visibility.
/// </summary>
internal static class FeatureStreamEndpoints
{
    private const int SubscriptionBboxSrid = 4326;
    private const string WebSocketTransport = "WebSocket";
    private const string SseTransport = "SSE";

    public static void MapFeatureStreamEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Feature-change events are global and include replay, so only admins can subscribe.
        var streamGroup = endpoints.MapGroup("/api/v{version:apiVersion}/streaming")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Streaming")
            .RequireAdminAuthorization();

        streamGroup.MapGet("/features", HandleFeatureStream)
            .WithDisplayName("Stream Feature Changes")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .WithDescription("Opens a WebSocket or SSE stream of real-time feature-change events. " +
                             "WebSocket: send Upgrade header. SSE: send Accept: text/event-stream. " +
                             "Query params: cursor (resume from cursor), clientLabel, serviceId, " +
                             "layerIds/layers (comma-separated layer filter), bbox (WGS84; requires exactly one layer), filter, filter-lang.")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        // Admin endpoints for session visibility
        var adminGroup = endpoints.MapGroup("/api/v{version:apiVersion}/admin/streaming/features")
            .WithApiVersionSet()
            .HasApiVersion(1, 0)
            .WithTags("Admin", "Streaming")
            .RequireAdminAuthorization();

        adminGroup.MapGet("/sessions", HandleListSessions)
            .WithDisplayName("List Feature Stream Sessions")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Get]))
            .Produces<ApiResponse<FeatureStreamStatusResponse>>();

        adminGroup.MapDelete("/sessions/{sessionId:guid}", HandleDisconnectSession)
            .WithDisplayName("Disconnect Feature Stream Session")
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Delete]))
            .Produces<ApiResponse<object>>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> HandleFeatureStream(
        [FromServices] FeatureStreamDependencies deps,
        ILogger<FeatureStreamEndpointsLog> logger,
        HttpContext context)
    {
        // Parse and validate subscription filters before accepting the connection.
        var filterResult = await ParseSubscriptionFilterAsync(deps, logger, context).ConfigureAwait(false);
        if (filterResult.Error is not null)
        {
            return filterResult.Error;
        }

        // Determine transport from request headers.
        if (context.WebSockets.IsWebSocketRequest)
        {
            await HandleWebSocketStream(deps.SessionManager, deps.EventStore, deps.Options.Value, logger, context, filterResult.Filter).ConfigureAwait(false);
            return Results.Empty;
        }

        var accept = context.Request.Headers.Accept.ToString();
        if (accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            await HandleSseStream(deps.SessionManager, deps.EventStore, deps.Options.Value, logger, context, filterResult.Filter).ConfigureAwait(false);
            return Results.Empty;
        }

        return ProblemDetailsHelpers.CreateAdminProblem(
            context,
            StatusCodes.Status400BadRequest,
            "WebSocket upgrade or Accept: text/event-stream header required.");
    }

    private static async Task<(IStreamSubscriptionFilter? Filter, IResult? Error)> ParseSubscriptionFilterAsync(
        FeatureStreamDependencies deps,
        ILogger logger,
        HttpContext context)
    {
        var query = context.Request.Query;
        var serviceId = NullIfEmpty(query["serviceId"].ToString());
        var layersParam = NullIfEmpty(query["layers"].ToString());
        var legacyLayerIdsParam = NullIfEmpty(query["layerIds"].ToString());
        var bboxParam = NullIfEmpty(query["bbox"].ToString());
        var filterParam = NullIfEmpty(query["filter"].ToString());

        int[]? layerIds = null;
        double[]? bbox = null;
        FilterExpression? attributeFilter = null;
        bool hasAnyFilter = serviceId is not null;

        // Parse the new strict layer filter first.
        if (!string.IsNullOrWhiteSpace(layersParam))
        {
            var parts = layersParam.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var ids = new List<int>(parts.Length);
            foreach (var part in parts)
            {
                if (!int.TryParse(part, CultureInfo.InvariantCulture, out var id))
                {
                    var msg = $"Invalid layer ID '{part}'. Must be an integer.";
                    FeatureStreamLog.FilterValidationFailed(logger, msg);
                    return (null, ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, msg));
                }

                ids.Add(id);
            }

            // Validate layers exist.
            foreach (var id in ids)
            {
                var layer = await deps.LayerCatalog.GetLayerAsync(id, context.RequestAborted).ConfigureAwait(false);
                if (layer is null)
                {
                    var msg = $"Layer {id} not found.";
                    FeatureStreamLog.FilterValidationFailed(logger, msg);
                    return (null, ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, msg));
                }
            }

            layerIds = ids.ToArray();
            hasAnyFilter = true;
        }
        else if (!string.IsNullOrWhiteSpace(legacyLayerIdsParam))
        {
            var ids = new HashSet<int>();
            foreach (var part in legacyLayerIdsParam.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(part, CultureInfo.InvariantCulture, out var id))
                {
                    ids.Add(id);
                }
            }

            layerIds = ids.ToArray();
            hasAnyFilter = true;
        }

        // Parse bbox (minX,minY,maxX,maxY).
        if (!string.IsNullOrWhiteSpace(bboxParam))
        {
            var parts = bboxParam.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 4)
            {
                var msg = "Invalid bbox: expected 4 comma-separated values (minX,minY,maxX,maxY).";
                FeatureStreamLog.FilterValidationFailed(logger, msg);
                return (null, ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, msg));
            }

            bbox = new double[4];
            for (int i = 0; i < 4; i++)
            {
                if (!double.TryParse(parts[i], CultureInfo.InvariantCulture, out bbox[i]) || !double.IsFinite(bbox[i]))
                {
                    var msg = $"Invalid bbox value '{parts[i]}' at position {i}. Must be a finite number.";
                    FeatureStreamLog.FilterValidationFailed(logger, msg);
                    return (null, ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, msg));
                }
            }

            if (bbox[0] > bbox[2] || bbox[1] > bbox[3])
            {
                var msg = "Invalid bbox: minX must be <= maxX and minY must be <= maxY.";
                FeatureStreamLog.FilterValidationFailed(logger, msg);
                return (null, ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, msg));
            }

            if (layerIds is null || layerIds.Length != 1)
            {
                const string msg = "bbox filters require exactly one layer specified via the layers or layerIds parameter.";
                FeatureStreamLog.FilterValidationFailed(logger, msg);
                return (null, ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, msg));
            }

            var bboxLayer = await deps.LayerCatalog.GetLayerAsync(layerIds[0], context.RequestAborted).ConfigureAwait(false);
            if (bboxLayer is null)
            {
                var msg = $"Layer {layerIds[0]} not found.";
                FeatureStreamLog.FilterValidationFailed(logger, msg);
                return (null, ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, msg));
            }

            var (projectedBbox, bboxError) = await TryProjectSubscriptionBboxAsync(
                deps,
                bbox,
                bboxLayer,
                context.RequestAborted).ConfigureAwait(false);
            if (bboxError is not null)
            {
                FeatureStreamLog.FilterValidationFailed(logger, bboxError);
                return (null, ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, bboxError));
            }

            bbox = projectedBbox;
            hasAnyFilter = true;
        }

        // Parse attribute filter (CQL2-text).
        if (!string.IsNullOrWhiteSpace(filterParam))
        {
            var filterLang = query["filter-lang"].ToString();
            if (!TryResolveFilterLanguage(filterLang, out var language, out var filterLangError))
            {
                FeatureStreamLog.FilterValidationFailed(logger, filterLangError);
                return (null, ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, filterLangError));
            }

            var parseResult = deps.FilterExpressionService.Parse(language, filterParam);
            if (!parseResult.IsSuccess)
            {
                var msg = $"Invalid filter expression: {parseResult.ErrorMessage}";
                FeatureStreamLog.FilterValidationFailed(logger, msg);
                return (null, ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, msg));
            }

            if (parseResult.Expression is not null)
            {
                // Enforce streaming depth limit.
                if (InMemoryFilterEvaluator.ExceedsMaxDepth(parseResult.Expression))
                {
                    var msg = $"Filter expression exceeds maximum depth ({InMemoryFilterEvaluator.MaxStreamingDepth}) for streaming subscriptions.";
                    FeatureStreamLog.FilterValidationFailed(logger, msg);
                    return (null, ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, msg));
                }

                if (!InMemoryFilterEvaluator.TryValidateStreamingExpression(parseResult.Expression, out var validationError))
                {
                    var msg = validationError ?? "Streaming subscriptions do not support the requested filter expression.";
                    FeatureStreamLog.FilterValidationFailed(logger, msg);
                    return (null, ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, msg));
                }

                attributeFilter = parseResult.Expression;
                hasAnyFilter = true;
            }
        }

        if (!hasAnyFilter)
        {
            return (null, null);
        }

        var filter = new StreamSubscriptionFilter(
            serviceId: serviceId,
            layerIds: layerIds,
            bbox: bbox,
            attributeFilter: attributeFilter);
        return (filter, null);
    }

    private static bool TryResolveFilterLanguage(string? filterLang, out FilterLanguage language, out string error)
    {
        language = FilterLanguage.Cql2Text;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(filterLang) ||
            filterLang.Equals("cql2-text", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (filterLang.Equals("cql2-json", StringComparison.OrdinalIgnoreCase))
        {
            language = FilterLanguage.Cql2Json;
            return true;
        }

        error = $"Unsupported filter language '{filterLang}'.";
        return false;
    }

    private static async Task<(double[] Bbox, string? Error)> TryProjectSubscriptionBboxAsync(
        FeatureStreamDependencies deps,
        double[] bbox,
        LayerDefinition layer,
        CancellationToken cancellationToken)
    {
        if (!layer.HasGeometry)
        {
            return (bbox, $"bbox filters are not supported for non-spatial layer {layer.Id}.");
        }

        var layerSrid = layer.SpatialReference.Wkid;
        if (layerSrid <= 0)
        {
            return (bbox, $"Layer {layer.Id} does not define a valid spatial reference.");
        }

        if (layerSrid == SubscriptionBboxSrid)
        {
            return (bbox, null);
        }

        try
        {
            var projectedWkb = await deps.GeometryOperationService.ProjectAsync(
                SpatialFilterHelpers.CreateEnvelopeWkb(bbox[0], bbox[1], bbox[2], bbox[3]),
                SubscriptionBboxSrid,
                layerSrid,
                cancellationToken).ConfigureAwait(false);

            var geometry = WkbReaderCache.Get().Read(projectedWkb);
            if (geometry is null || geometry.IsEmpty)
            {
                return (bbox, $"Unable to project bbox to layer {layer.Id} spatial reference.");
            }

            var env = geometry.EnvelopeInternal;
            return ([env.MinX, env.MinY, env.MaxX, env.MaxY], null);
        }
        catch (ArgumentException ex)
        {
            return (bbox, $"Invalid bbox projection for layer {layer.Id}: {ex.Message}");
        }
        catch (NotSupportedException ex)
        {
            return (bbox, $"bbox filters do not support projecting layer {layer.Id} to SRID {layerSrid}: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return (bbox, $"bbox filters could not be projected for layer {layer.Id}: {ex.Message}");
        }
    }

    private static async Task HandleWebSocketStream(
        FeatureStreamSessionManager sessionManager,
        IFeatureChangeEventStore eventStore,
        FeatureStreamOptions options,
        ILogger logger,
        HttpContext context,
        IStreamSubscriptionFilter? subscriptionFilter)
    {
        using var webSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);

        var clientLabel = context.Request.Query["clientLabel"].ToString();
        var cursorParam = context.Request.Query["cursor"].ToString();
        long? cursor = long.TryParse(cursorParam, CultureInfo.InvariantCulture, out var c) ? c : null;
        using var session = sessionManager.CreateSession(WebSocketTransport, NullIfEmpty(clientLabel), subscriptionFilter);

        if (subscriptionFilter is not null)
        {
            FeatureStreamLog.SessionCreatedWithFilter(logger, session.SessionId, subscriptionFilter.Summary);
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted, session.DisconnectToken);

        // Replay missed events directly to the WebSocket, bypassing the bounded channel
        // so that large replay backlogs are not truncated by the buffer limit.
        // Live broadcasts flow into the channel concurrently; the drain deduplicates
        // using the replay cursor so events are delivered exactly once.
        bool hasReplay = cursor.HasValue;
        long replayCursor = 0;
        if (hasReplay)
        {
            try
            {
                replayCursor = await ReplayToWebSocketAsync(webSocket, eventStore, cursor!.Value, options.ReplayBatchSize, logger, session.SessionId, linkedCts.Token, subscriptionFilter).ConfigureAwait(false);

                // Catch-up: replay events published during the main replay window that
                // were silently dropped from the bounded channel pre-drain.
                replayCursor = await ReplayToWebSocketAsync(webSocket, eventStore, replayCursor, options.ReplayBatchSize, logger, session.SessionId, linkedCts.Token, subscriptionFilter).ConfigureAwait(false);
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
                    replayCursor = await ReplayToWebSocketAsync(webSocket, eventStore, replayCursor, options.ReplayBatchSize, logger, session.SessionId, linkedCts.Token, subscriptionFilter).ConfigureAwait(false);
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
        var writerTask = WriteWebSocketAsync(webSocket, session, replayCursor, linkedCts.Token,
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
                        cursor = await ReplayToWebSocketAsync(webSocket, eventStore, cursor, options.ReplayBatchSize, logger, session.SessionId, ct, subscriptionFilter).ConfigureAwait(false);
                        if (cursor > prev)
                        {
                            progress = true;
                        }
                    } while (progress || session.Reader.TryPeek(out _));

                    sessionManager.ClearDrainGrace(session.SessionId);
                    return cursor;
                }
        : null,
            onPoll: async (cursor, ct) =>
                await ReplayToWebSocketAsync(webSocket, eventStore, cursor, options.ReplayBatchSize, logger, session.SessionId, ct, subscriptionFilter).ConfigureAwait(false),
            pollInterval: options.CrossNodeSyncInterval);

        // Receive loop keeps the connection alive and detects client close.
        var buffer = new byte[1];
        try
        {
            while (webSocket.State == WebSocketState.Open && !linkedCts.Token.IsCancellationRequested)
            {
                var result = await webSocket.ReceiveAsync(buffer, linkedCts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (linkedCts.Token.IsCancellationRequested)
        {
            // Normal shutdown or disconnect.
        }
        catch (WebSocketException)
        {
            // Client disconnected abruptly.
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
            catch (WebSocketException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private static async Task WriteWebSocketAsync(
        WebSocket webSocket,
        FeatureStreamSession session,
        long replayCursor,
        CancellationToken cancellationToken,
        Func<long, CancellationToken, Task<long>>? onFirstRead = null,
        Func<long, CancellationToken, Task<long>>? onPoll = null,
        TimeSpan? pollInterval = null)
    {
        try
        {
            var effectivePollInterval = pollInterval.GetValueOrDefault(TimeSpan.FromSeconds(1));
            while (!cancellationToken.IsCancellationRequested)
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var waitToReadTask = session.Reader.WaitToReadAsync(waitCts.Token).AsTask();
                var waitForPollTask = onPoll == null
                    ? Task.Delay(Timeout.InfiniteTimeSpan, waitCts.Token)
                    : Task.Delay(effectivePollInterval, waitCts.Token);
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

                    // Skip events already delivered during replay to prevent duplicate delivery.
                    if (!message.IsHeartbeat && replayCursor > 0 && message.Envelope.Cursor <= replayCursor)
                    {
                        continue;
                    }

                    var payload = message.IsHeartbeat
                        ? JsonSerializer.SerializeToUtf8Bytes(
                            new FeatureStreamHeartbeat(),
                            FeatureStreamJsonContext.Default.FeatureStreamHeartbeat)
                        : JsonSerializer.SerializeToUtf8Bytes(
                            message.Envelope,
                            FeatureStreamJsonContext.Default.FeatureStreamEnvelope);

                    await webSocket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);

                    if (!message.IsHeartbeat)
                    {
                        replayCursor = message.Envelope.Cursor;
                    }
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

    private static async Task HandleSseStream(
        FeatureStreamSessionManager sessionManager,
        IFeatureChangeEventStore eventStore,
        FeatureStreamOptions options,
        ILogger logger,
        HttpContext context,
        IStreamSubscriptionFilter? subscriptionFilter)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";

        try
        {
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

        using var session = sessionManager.CreateSession(SseTransport, NullIfEmpty(clientLabel), subscriptionFilter);

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
                replayCursor = await ReplayToSseAsync(context.Response, eventStore, cursor!.Value, options.ReplayBatchSize, logger, session.SessionId, linkedCts.Token, subscriptionFilter).ConfigureAwait(false);

                // Catch-up: replay events published during the main replay window that
                // were silently dropped from the bounded channel pre-drain.
                replayCursor = await ReplayToSseAsync(context.Response, eventStore, replayCursor, options.ReplayBatchSize, logger, session.SessionId, linkedCts.Token, subscriptionFilter).ConfigureAwait(false);
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
                    replayCursor = await ReplayToSseAsync(context.Response, eventStore, replayCursor, options.ReplayBatchSize, logger, session.SessionId, linkedCts.Token, subscriptionFilter).ConfigureAwait(false);
                } while (replayCursor > previousCursor || session.Reader.TryPeek(out _));
            }

            // For replay sessions, grace clear is deferred to the first drain
            // iteration (below) so the reader creates headroom for the final sweep.
            // For fresh sessions, grace was already cleared above.
            bool handoffDone = !hasReplay;

            while (!linkedCts.Token.IsCancellationRequested)
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token);
                var waitToReadTask = session.Reader.WaitToReadAsync(waitCts.Token).AsTask();
                var waitForPollTask = Task.Delay(options.CrossNodeSyncInterval, waitCts.Token);
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

                    replayCursor = await ReplayToSseAsync(context.Response, eventStore, replayCursor, options.ReplayBatchSize, logger, session.SessionId, linkedCts.Token, subscriptionFilter).ConfigureAwait(false);
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
                            replayCursor = await ReplayToSseAsync(context.Response, eventStore, replayCursor, options.ReplayBatchSize, logger, session.SessionId, linkedCts.Token, subscriptionFilter).ConfigureAwait(false);
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
                            await context.Response.WriteAsync(": heartbeat\n\n", linkedCts.Token).ConfigureAwait(false);
                        }
                        else
                        {
                            var json = JsonSerializer.Serialize(
                                message.Envelope,
                                FeatureStreamJsonContext.Default.FeatureStreamEnvelope);

                            await context.Response.WriteAsync(
                                string.Concat(
                                    "id: ", message.Envelope.Cursor.ToString(CultureInfo.InvariantCulture), "\n",
                                    "event: feature-change\n",
                                    "data: ", json, "\n\n"),
                                linkedCts.Token).ConfigureAwait(false);

                            replayCursor = message.Envelope.Cursor;
                        }

                        await context.Response.Body.FlushAsync(linkedCts.Token).ConfigureAwait(false);
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

    private static async Task<long> ReplayToWebSocketAsync(
        WebSocket webSocket,
        IFeatureChangeEventStore eventStore,
        long fromCursor,
        int batchSize,
        ILogger logger,
        Guid sessionId,
        CancellationToken cancellationToken,
        IStreamSubscriptionFilter? subscriptionFilter = null)
    {
        var cursor = fromCursor;
        while (!cancellationToken.IsCancellationRequested)
        {
            var events = await eventStore.QueryAsync(cursor, null, null, batchSize, cancellationToken).ConfigureAwait(false);
            if (events.Count == 0)
            {
                break;
            }

            FeatureStreamLog.ReplayStarted(logger, events.Count, cursor, sessionId);

            foreach (var evt in events)
            {
                var envelope = FeatureStreamPublisher.ToEnvelope(evt);
                cursor = evt.Cursor;

                // Apply subscription filter during replay — advance cursor past filtered events.
                if (subscriptionFilter is not null
                    && !subscriptionFilter.Matches(envelope, evt.GeometryEnvelope, evt.PropertiesJson))
                {
                    continue;
                }

                var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, FeatureStreamJsonContext.Default.FeatureStreamEnvelope);
                await webSocket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }

            if (events.Count < batchSize)
            {
                break;
            }
        }

        return cursor;
    }

    private static async Task<long> ReplayToSseAsync(
        HttpResponse response,
        IFeatureChangeEventStore eventStore,
        long fromCursor,
        int batchSize,
        ILogger logger,
        Guid sessionId,
        CancellationToken cancellationToken,
        IStreamSubscriptionFilter? subscriptionFilter = null)
    {
        var cursor = fromCursor;
        while (!cancellationToken.IsCancellationRequested)
        {
            var events = await eventStore.QueryAsync(cursor, null, null, batchSize, cancellationToken).ConfigureAwait(false);
            if (events.Count == 0)
            {
                break;
            }

            FeatureStreamLog.ReplayStarted(logger, events.Count, cursor, sessionId);

            foreach (var evt in events)
            {
                var envelope = FeatureStreamPublisher.ToEnvelope(evt);
                cursor = evt.Cursor;

                // Apply subscription filter during replay — advance cursor past filtered events.
                if (subscriptionFilter is not null
                    && !subscriptionFilter.Matches(envelope, evt.GeometryEnvelope, evt.PropertiesJson))
                {
                    continue;
                }

                var json = JsonSerializer.Serialize(envelope, FeatureStreamJsonContext.Default.FeatureStreamEnvelope);
                await response.WriteAsync(
                    string.Concat(
                        "id: ", envelope.Cursor.ToString(CultureInfo.InvariantCulture), "\n",
                        "event: feature-change\n",
                        "data: ", json, "\n\n"),
                    cancellationToken).ConfigureAwait(false);
                await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (events.Count < batchSize)
            {
                break;
            }
        }

        return cursor;
    }

    private static IResult HandleListSessions(
        [FromServices] FeatureStreamSessionManager sessionManager,
        ILogger<FeatureStreamEndpointsLog> logger)
    {
        var sessions = sessionManager.GetSessions();
        var now = DateTimeOffset.UtcNow;

        var sessionResponses = sessions.Select(s => new FeatureStreamSessionResponse
        {
            SessionId = s.SessionId,
            ConnectedAt = s.ConnectedAt,
            ClientLabel = s.ClientLabel,
            Transport = s.Transport,
            LastQueuedCursor = s.LastQueuedCursor,
            DurationSeconds = (now - s.ConnectedAt).TotalSeconds,
            HasFilter = s.HasFilter,
            FilterSummary = s.FilterSummary,
            ServiceIdFilter = s.ServiceIdFilter,
            LayerIdFilter = s.LayerIdFilter
        }).ToArray();

        var wsSessions = sessionResponses.Count(s => s.Transport == WebSocketTransport);
        var sseSessions = sessionResponses.Count(s => s.Transport == SseTransport);

        var response = new FeatureStreamStatusResponse
        {
            ActiveSessions = sessionResponses.Length,
            WebSocketSessions = wsSessions,
            SseSessions = sseSessions,
            SlowConsumerDrops = sessionManager.SlowConsumerDrops,
            HeartbeatsSent = sessionManager.HeartbeatsSent,
            Sessions = sessionResponses,
            GeneratedAt = now
        };

        return Results.Json(
            ApiResponse<FeatureStreamStatusResponse>.CreateSuccess(response),
            FeatureStreamJsonContext.Default.ApiResponseFeatureStreamStatusResponse);
    }

    private static IResult HandleDisconnectSession(
        Guid sessionId,
        [FromServices] FeatureStreamSessionManager sessionManager,
        HttpContext context,
        ILogger<FeatureStreamEndpointsLog> logger)
    {
        if (!sessionManager.DisconnectSession(sessionId))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(
                context,
                StatusCodes.Status404NotFound,
                string.Concat("Feature stream session ", sessionId.ToString(), " not found."));
        }

        return Results.Json(
            ApiResponse<object>.SuccessWithMessage(string.Concat("Session ", sessionId.ToString(), " disconnected.")),
            FeatureStreamJsonContext.Default.ApiResponseObject);
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// Log category for feature stream endpoints.
/// </summary>
internal sealed class FeatureStreamEndpointsLog;

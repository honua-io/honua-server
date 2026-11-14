// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Honua.Core.Features.Realtime;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.SensorThings.Streaming;

internal static class ObservationStreamEndpoints
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    // A client that has stopped reading must not pin the handler, so a gap-frame write that overruns its budget aborts the transport.
    private static readonly TimeSpan GapWriteBudget = TimeSpan.FromSeconds(2);

    public static IEndpointRouteBuilder MapSensorThingsStreamEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/sta/v1.1/ObservationsStream", HandleStream)
            .AddEndpointFilter<LiveStreamAuthorizationFilter>()
            .WithDisplayName("STA Observation Stream")
            .WithName("StaObservationStream")
            .WithSummary("Stream new Observations in real time (SSE or WebSocket)")
            .WithMetadata(WebSocketEndpointMetadata.Instance)
            .WithDescription(
                "Opens a real-time stream of newly-ingested Observations. SSE: send " +
                "Accept: text/event-stream. WebSocket: send an Upgrade request. " +
                "Optional query param: datastreamId (tail a single Datastream). " +
                "Delivery uses committed batches with observations-family resume cursors. " +
                "Send Last-Event-ID or cursor to resume within retained history.")
            .WithTags("SensorThings")
            .RequireAuthorization()
            .Produces(200, contentType: "text/event-stream")
            .Produces(400)
            .Produces(401)
            .Produces(403)
            .Produces(429)
            .Produces(503);

        return endpoints;
    }

    private static async Task<IResult> HandleStream(
        HttpContext context,
        [FromServices] ObservationStreamSessionManager sessionManager,
        [FromServices] ObservationStreamScope scope)
    {
        var tenant = context.RequestServices.GetService<ITenantContext>();
        // Multi-tenant mode admits non-admins only on a claim-resolved tenant. Single-tenant
        // mode resolves every authenticated request to the deployment tenant (Source=Default).
        var tenancyEnabled = context.RequestServices.GetService<IOptions<TenantContextOptions>>()?.Value.Enabled == true;
        var resolvedScope = tenant?.Source == TenantContextSource.Claim
            || (!tenancyEnabled && tenant?.Source == TenantContextSource.Default);
        if (!context.User.IsInRole("admin") && (string.IsNullOrWhiteSpace(scope.TenantId) || !resolvedScope))
        {
            return StandardErrorHelpers.CreateForbidden(context,
                "Observation subscriptions require a resolved tenant scope.");
        }

        var (cursorValid, after) = ReadCursor(context);
        if (!cursorValid)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "A nonnegative observations-family cursor is required.");
        }

        long? datastreamId = null;
        if (context.Request.Query.TryGetValue("datastreamId", out var raw))
        {
            if (!long.TryParse(raw.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            {
                return StandardErrorHelpers.CreateBadRequest(context, "datastreamId must be a positive integer.");
            }

            datastreamId = parsed;
        }

        var isWebSocket = context.WebSockets.IsWebSocketRequest;
        var accept = context.Request.Headers.Accept.ToString();
        var isSse = accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);

        if (!isWebSocket && !isSse)
        {
            return StandardErrorHelpers.CreateBadRequest(
                context,
                "WebSocket upgrade or Accept: text/event-stream header required.");
        }

        // Admit before the WebSocket upgrade or SSE headers so a refused caller receives a
        // problem document, not an opened stream. Admission is keyed by the canonical actor
        // within the resolved tenant, so one credential cannot exhaust the node (#4198).
        var (session, rejectedBy) = await sessionManager.CreateSessionAsync(new ObservationStreamRequest(
            isWebSocket ? "WebSocket" : "SSE",
            datastreamId,
            scope,
            CanonicalSecurityActor.Resolve(context.User)?.ActorId, after),
            context.RequestAborted).ConfigureAwait(false);
        if (session is null)
        {
            return CreateAdmissionRejected(context, rejectedBy, sessionManager.Options);
        }

        await using var sessionLease = session;
        if (isWebSocket)
        {
            await HandleWebSocketAsync(context, session, datastreamId).ConfigureAwait(false);
        }
        else
        {
            await HandleSseAsync(context, session, datastreamId).ConfigureAwait(false);
        }

        return Results.Empty;
    }

    private static (bool Valid, long? After) ReadCursor(HttpContext context)
    {
        var hasQueryCursor = context.Request.Query.ContainsKey("cursor");
        var hasHeaderCursor = context.Request.Headers.ContainsKey("Last-Event-ID");
        var queryCursor = context.Request.Query["cursor"].ToString();
        var headerCursor = context.Request.Headers["Last-Event-ID"].ToString();
        var rawCursor = hasQueryCursor ? queryCursor : headerCursor;
        long? after = null;
        if ((hasQueryCursor && hasHeaderCursor && queryCursor != headerCursor)
            || ((hasQueryCursor || hasHeaderCursor) && (!rawCursor.StartsWith("observations:", StringComparison.Ordinal)
                || !long.TryParse(rawCursor.AsSpan("observations:".Length), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var sequence) || sequence < 0)))
        {
            return (false, null);
        }

        if (!string.IsNullOrEmpty(rawCursor))
        {
            after = long.Parse(rawCursor.AsSpan("observations:".Length), CultureInfo.InvariantCulture);
        }

        return (true, after);
    }

    private static IResult CreateAdmissionRejected(
        HttpContext context,
        ObservationStreamAdmissionLimit limit,
        ObservationStreamOptions options) => limit switch
        {
            // Per-caller quotas are the caller's own concurrency and clear as its sessions
            // close; only node exhaustion is a server-side availability condition.
            ObservationStreamAdmissionLimit.Principal => StandardErrorHelpers.CreateTooManyRequests(context,
                $"Observation stream limit of {options.MaxSessionsPerPrincipal} concurrent sessions per principal reached.",
                options.RetryAfterSeconds),
            ObservationStreamAdmissionLimit.Tenant => StandardErrorHelpers.CreateTooManyRequests(context,
                $"Observation stream limit of {options.MaxSessionsPerTenant} concurrent sessions per tenant reached.",
                options.RetryAfterSeconds),
            _ => StandardErrorHelpers.CreateServiceUnavailable(context,
                "Observation stream capacity on this node is exhausted.",
                options.RetryAfterSeconds)
        };

    private static async Task HandleSseAsync(
        HttpContext context,
        ObservationStreamSession session,
        long? datastreamId)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-store, no-transform";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted, session.DisconnectToken);
        var ct = linkedCts.Token;
        var clientGone = false;

        try
        {
            await WriteSseAsync(
                context.Response,
                "status",
                JsonSerializer.Serialize(
                    ObservationStreamStatus.Connected(datastreamId),
                    ObservationStreamJsonContext.Default.ObservationStreamStatus),
                ct).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var readTask = session.Reader.WaitToReadAsync(readCts.Token).AsTask();
                var timeoutTask = Task.Delay(HeartbeatInterval, readCts.Token);
                var completed = await Task.WhenAny(readTask, timeoutTask).ConfigureAwait(false);
                readCts.Cancel();

                if (completed == timeoutTask)
                {
                    await SwallowAsync(readTask).ConfigureAwait(false);
                    await WriteSseAsync(context.Response, "heartbeat", "{}", ct).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                    continue;
                }

                await SwallowAsync(timeoutTask).ConfigureAwait(false);
                if (!await readTask.ConfigureAwait(false))
                {
                    break;
                }

                while (session.Reader.TryRead(out var frame))
                {
                    var message = FormatDelivery(frame);
                    await context.Response.WriteAsync(
                        string.Concat(message.Cursor is null ? "" : "id: " + message.Cursor + "\n",
                            "event: ", message.Event, "\ndata: ", message.Json, "\n\n"), ct).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                    if (frame is RealtimeDelivery.Reset) return;
                }

                await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The session token or the request ended. A slow-consumer cancel still
            // needs a gap frame, and that write must not use the cancelled token.
        }
        catch (ChannelClosedException)
        {
            // Provider closes the subscription when delivery cannot continue.
        }
        catch (IOException)
        {
            clientGone = true;
        }
        catch (ObjectDisposedException)
        {
            clientGone = true;
        }

        if (!clientGone)
        {
            await WriteSlowConsumerGapAsync(context, session, datastreamId, socket: null).ConfigureAwait(false);
        }
    }

    private static async Task HandleWebSocketAsync(
        HttpContext context,
        ObservationStreamSession session,
        long? datastreamId)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted, session.DisconnectToken);
        var ct = linkedCts.Token;
        var clientGone = false;

        try
        {
            await SendSocketAsync(
                socket,
                JsonSerializer.Serialize(
                    ObservationStreamStatus.Connected(datastreamId),
                    ObservationStreamJsonContext.Default.ObservationStreamStatus),
                ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var readTask = session.Reader.WaitToReadAsync(readCts.Token).AsTask();
                var timeoutTask = Task.Delay(HeartbeatInterval, readCts.Token);
                var completed = await Task.WhenAny(readTask, timeoutTask).ConfigureAwait(false);
                readCts.Cancel();

                if (completed == timeoutTask)
                {
                    await SwallowAsync(readTask).ConfigureAwait(false);
                    await SendSocketAsync(socket, "{\"heartbeat\":true}", ct).ConfigureAwait(false);
                    continue;
                }

                await SwallowAsync(timeoutTask).ConfigureAwait(false);
                if (!await readTask.ConfigureAwait(false))
                {
                    break;
                }

                while (session.Reader.TryRead(out var frame))
                {
                    var message = FormatDelivery(frame);
                    await SendSocketAsync(socket, message.Json, ct).ConfigureAwait(false);
                    if (frame is RealtimeDelivery.Reset) return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The session token or the request ended. A slow-consumer cancel still
            // needs a gap frame, and that write must not use the cancelled token.
        }
        catch (ChannelClosedException)
        {
            // Provider closes the subscription when delivery cannot continue.
        }
        catch (WebSocketException)
        {
            clientGone = true;
        }

        if (!clientGone)
        {
            await WriteSlowConsumerGapAsync(context, session, datastreamId, socket).ConfigureAwait(false);
        }
    }

    private static async Task WriteSlowConsumerGapAsync(
        HttpContext context,
        ObservationStreamSession session,
        long? datastreamId,
        WebSocket? socket)
    {
        if (session.DisconnectReason != ObservationStreamDisconnectReason.SlowConsumer
            || context.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        var json = JsonSerializer.Serialize(
            ObservationStreamStatus.SlowConsumerGap(datastreamId),
            ObservationStreamJsonContext.Default.ObservationStreamStatus);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        budget.CancelAfter(GapWriteBudget);
        try
        {
            if (socket is null)
            {
                await WriteSseAsync(context.Response, "status", json, budget.Token).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(budget.Token).ConfigureAwait(false);
            }
            else if (socket.State == WebSocketState.Open)
            {
                await SendSocketAsync(socket, json, budget.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (
            budget.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
        {
            AbortTransport(context, socket);
        }
        catch (OperationCanceledException)
        {
            // The client aborted while the gap was being written.
        }
        catch (IOException)
        {
            // The client is already gone.
        }
        catch (ObjectDisposedException)
        {
            // The response was disposed before the gap could be written.
        }
        catch (WebSocketException)
        {
            // The socket closed before the gap could be written.
        }
    }

    private static void AbortTransport(HttpContext context, WebSocket? socket)
    {
        if (socket is not null)
        {
            try
            {
                socket.Abort();
            }
            catch (WebSocketException)
            {
                // The socket already failed.
            }
            catch (ObjectDisposedException)
            {
                // The socket was already disposed.
            }
        }

        context.Abort();
    }

    private static (string Event, string Json, string? Cursor) FormatDelivery(RealtimeDelivery delivery)
    {
        return delivery switch
        {
            RealtimeDelivery.Event item => ("observations", JsonSerializer.Serialize(
                new ObservationStreamBatch(item.Value.Position.ToString(),
                    item.Value.Payload.GetProperty("datastreamId").GetInt64(),
                    item.Value.Payload.GetProperty("observations")),
                ObservationStreamJsonContext.Default.ObservationStreamBatch), item.Value.Position.ToString()),
            RealtimeDelivery.Reset reset => ("reset", JsonSerializer.Serialize(
                new ObservationStreamReset(reset.Reason, new RealtimePosition(RealtimeFamily.Observations, reset.Head).ToString()),
                ObservationStreamJsonContext.Default.ObservationStreamReset), null),
            _ => ("heartbeat", "{\"heartbeat\":true}", null)
        };
    }

    private static Task WriteSseAsync(HttpResponse response, string eventName, string json, CancellationToken ct) =>
        response.WriteAsync(string.Concat("event: ", eventName, "\ndata: ", json, "\n\n"), ct);

    private static Task SendSocketAsync(WebSocket socket, string json, CancellationToken ct) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, ct);

    private static async Task SwallowAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when the alternate waiter is cancelled.
        }
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Protocols.SensorThings.Streaming;

/// <summary>
/// OGC SensorThings real-time observation streaming endpoint (Phase 3, #1747).
/// A single route serves both Server-Sent Events (<c>Accept: text/event-stream</c>) and
/// WebSocket (upgrade request) transports, reusing the bounded-channel session pattern of
/// the server feature-change stream. Newly-ingested observations (Phase 2) are pushed to
/// connected clients in near-real-time without an MQTT broker dependency.
/// </summary>
internal static class ObservationStreamEndpoints
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(25);

    /// <summary>
    /// The gap frame is best-effort. A client that has stopped reading must not pin the
    /// handler past this budget; the transport is aborted when the write does not finish.
    /// </summary>
    private static readonly TimeSpan GapWriteBudget = TimeSpan.FromSeconds(2);

    /// <summary>Maps the SensorThings observation-stream endpoint.</summary>
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
                "Delivery is ephemeral: there is no resume cursor, and a slow consumer is " +
                "disconnected with a gap status instead of a silent drop.")
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
        if (!context.User.IsInRole("admin") && (string.IsNullOrWhiteSpace(scope.TenantId)
            || tenant?.Source != TenantContextSource.Claim))
        {
            return StandardErrorHelpers.CreateForbidden(context,
                "Observation subscriptions require a resolved tenant scope.");
        }

        if (context.Request.Query.ContainsKey("cursor") || context.Request.Headers.ContainsKey("Last-Event-ID"))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Observation subscriptions are live-only and cannot replay a supplied cursor.");
        }

        long? datastreamId = null;
        if (context.Request.Query.TryGetValue("datastreamId", out var raw) &&
            long.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
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
        var session = sessionManager.TryCreateSession(
            isWebSocket ? "WebSocket" : "SSE",
            datastreamId,
            scope,
            CanonicalSecurityActor.Resolve(context.User)?.ActorId,
            out var rejectedBy);
        if (session is null)
        {
            return CreateAdmissionRejected(context, rejectedBy, sessionManager.Options);
        }

        using var sessionLease = session;
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
        context.Response.Headers.CacheControl = "no-cache";

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
                    if (frame.IsHeartbeat)
                    {
                        await WriteSseAsync(context.Response, "heartbeat", "{}", ct).ConfigureAwait(false);
                    }
                    else
                    {
                        var json = JsonSerializer.Serialize(frame, ObservationStreamJsonContext.Default.ObservationStreamFrame);
                        await WriteSseAsync(context.Response, "observation", json, ct).ConfigureAwait(false);
                    }
                }

                await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The session token or the request ended. A slow-consumer cancel still
            // needs a gap frame, and that write must not use the cancelled token.
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
                    if (frame.IsHeartbeat)
                    {
                        await SendSocketAsync(socket, "{\"heartbeat\":true}", ct).ConfigureAwait(false);
                    }
                    else
                    {
                        var json = JsonSerializer.Serialize(frame, ObservationStreamJsonContext.Default.ObservationStreamFrame);
                        await SendSocketAsync(socket, json, ct).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The session token or the request ended. A slow-consumer cancel still
            // needs a gap frame, and that write must not use the cancelled token.
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

    /// <summary>
    /// Emits the terminal gap without using the cancelled session token.
    /// The write is bounded: a client that stopped reading does not pin the handler,
    /// and the transport is aborted when the budget expires.
    /// </summary>
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

    /// <summary>Drops a transport that did not accept the terminal gap within the budget.</summary>
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
